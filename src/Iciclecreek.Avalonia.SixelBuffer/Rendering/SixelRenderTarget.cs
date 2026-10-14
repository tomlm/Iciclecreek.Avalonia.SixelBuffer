#nullable enable
using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Hashing;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform;
using Iciclecreek.Avalonia.SixelBuffer.Platform;
using Iciclecreek.Avalonia.SixelBuffer.Terminal;
using SkiaSharp;

namespace Iciclecreek.Avalonia.SixelBuffer.Rendering
{
    /// <summary>
    ///     Render target that turns Avalonia frames into Sixel or kitty graphics output.
    ///     Each frame is compared cell by cell with the frame the terminal shows. Changed
    ///     cells are joined into horizontal runs; runs the terminal has already been sent
    ///     replay from a content-keyed cache, and the rest are quantized together once and
    ///     encoded one sixel per run. The frame goes out as a single write, inside a DEC 2026
    ///     synchronized update when the terminal supports it.
    /// </summary>
    internal class SixelRenderTarget : IRenderTarget
    {
        private const long RunCacheBudgetBytes = 16L * 1024 * 1024;

        private readonly TerminalWindow _consoleWindow;
        private readonly ITerminal _terminal;
        private readonly IPlatformRenderInterface _fallbackRenderInterface;
        private readonly IPlatformRenderInterfaceContext _fallbackContext;
        // Render thread. A cell-size change arrives through OnResized on the UI thread and is
        // taken up here at the start of the next frame, never in the middle of one.
        private int _cellPixelWidth;
        private int _cellPixelHeight;
        private volatile int _pendingCellPixelWidth;
        private volatile int _pendingCellPixelHeight;
        private IDrawingContextLayerImpl _innerTarget;
        private volatile bool _disposed;

        // Dedicated render thread — serializes all terminal output. Every field below,
        // unless noted, is owned by that thread.
        private readonly BlockingCollection<Action> _renderQueue = new(boundedCapacity: 2);

        // The frame the terminal currently shows, without the cursor (pooled)
        private byte[]? _shown;
        private int _shownWidth;
        private int _shownHeight;
        private volatile bool _fullRedraw = true; // written by the UI thread on resize

        // Dirty cell mask, the spans of dirty cells per row built from it, and the runs
        // each span is cut into (exact runs of at most 256 colors, or one Wu run)
        private bool[] _dirty = Array.Empty<bool>();
        private readonly List<Run> _spans = new();
        private readonly List<Run> _runs = new();
        private readonly List<Run> _wuRuns = new();
        private readonly XxHash128 _hasher = new();
        private readonly Dictionary<uint, byte> _runMap = new(256);

        // A run that overflows 256 colors within this many cells is picture-like and goes
        // to Wu whole; a longer one is simply cut and a new exact run started.
        private const int MinExactCells = 4;

        // Palette index per pixel at frame coordinates (valid for the cells written this frame)
        private byte[]? _frameIndices;
        private byte[]? _runIndices;
        private byte[]? _spanPixels;
        private byte[]? _wuGather;
        private byte[]? _wuIndices;
        private readonly ArrayBufferWriter<byte> _runWriter = new(16 * 1024);
        private readonly ContentCache _runCache = new(RunCacheBudgetBytes);

        // Kitty: the screen is a fixed grid of blocks, each an image under an id derived from
        // its position. A changed block is sent again under its id, which replaces it in place.
        // The size trades the terminal's cost per picture against the bytes resent per change:
        // 8x4 left a 307x75 screen at ~780 pictures, which cost Avalonia.Terminal ~15 ms a frame
        // to draw; 16x8 is a quarter of that for four times the pixels resent on a small change.
        private const int BlockCols = 16;
        private const int BlockRows = 8;
        // Blocks go as zlib RGB unless FRAMEBUFFER_KITTY_FORMAT=png asks for PNG. Measured on
        // 64x64 blocks, PNG through Skia was 5-8x slower to encode and no smaller: a text block
        // was 1.4 KB zlib against 1.7 KB PNG, a noisy one 12 KB against 14.6 KB.
        private static readonly bool KittyPng =
            string.Equals(Environment.GetEnvironmentVariable("FRAMEBUFFER_KITTY_FORMAT"), "png", StringComparison.OrdinalIgnoreCase);
        private byte[]? _blockPixels;
        private byte[]? _blockRgb;
        private readonly MemoryStream _zlibScratch = new();

        // Software cursor: bitmap cache plus where it was last drawn
        private SKBitmap? _cursorBitmap;
        private StandardCursorType _cachedCursorType = (StandardCursorType)(-1);
        private BitmapCursorImpl? _cachedBitmapCursor;
        private CursorState _prevCursor;
        private Point _prevCursorDraw = new(-1, -1);
        private CursorRegion _prevCursorRegion = CursorRegion.None;

        internal SixelRenderTarget(
            TerminalWindow consoleWindow,
            IDrawingContextLayerImpl innerTarget,
            ITerminal terminal,
            IPlatformRenderInterface fallbackRenderInterface,
            IPlatformRenderInterfaceContext fallbackContext,
            int cellPixelWidth,
            int cellPixelHeight)
        {
            _consoleWindow = consoleWindow;
            _innerTarget = innerTarget;
            _terminal = terminal;
            _fallbackRenderInterface = fallbackRenderInterface;
            _fallbackContext = fallbackContext;
            _cellPixelWidth = cellPixelWidth;
            _cellPixelHeight = cellPixelHeight;
            _consoleWindow.Resized += OnResized;
            StartRenderThread();
            StartCursorRefreshLoop();
        }

        public RenderTargetProperties Properties => new RenderTargetProperties
        {
            // Tell compositor we retain frame contents — it will only redraw dirty regions
            RetainsPreviousFrameContents = true,
            IsSuitableForDirectRendering = false
        };

        public bool IsCorrupted => false;

        public PlatformRenderTargetState PlatformRenderTargetState =>
            _disposed ? PlatformRenderTargetState.Disposed : PlatformRenderTargetState.Ready;

        public IDrawingContextImpl CreateDrawingContext(IRenderTarget.RenderTargetSceneInfo sceneInfo,
            out RenderTargetDrawingContextProperties contextProperties)
        {
            contextProperties = new RenderTargetDrawingContextProperties { PreviousFrameIsRetained = true };
            if (_innerTarget.PixelSize != sceneInfo.Size)
            {
                _innerTarget.Dispose();
                _innerTarget = _fallbackContext.CreateOffscreenRenderTarget(sceneInfo.Size, new Vector(96, 96), true);
                _fullRedraw = true;
            }

            IDrawingContextImpl innerCtx = _innerTarget.CreateDrawingContext();
            return new SixelDrawingContext(innerCtx, this);
        }

        public void Dispose()
        {
            _disposed = true;
            _renderQueue.CompleteAdding();
            _consoleWindow.Resized -= OnResized;
            _innerTarget?.Dispose();
        }

        internal void RenderToDevice()
        {
            RenderToDeviceCore();
        }

        private void StartRenderThread()
        {
            var thread = new Thread(() =>
            {
                try
                {
                    foreach (var action in _renderQueue.GetConsumingEnumerable())
                    {
                        try { action(); }
                        catch (Exception ex)
                        {
                            Debug.WriteLine($"[RenderThread] {ex}");
                        }
                    }
                }
                catch (ObjectDisposedException)
                {
                }
            })
            {
                IsBackground = true,
                Name = "SixelRenderThread"
            };
            thread.Start();
        }

        #region UI thread: frame capture

        private void RenderToDeviceCore()
        {
            if (_disposed) return;

            IReadableBitmapImpl? readableBitmap = GetReadableBitmap();
            if (readableBitmap == null)
                return;

            // === UI THREAD: only read the locked framebuffer (must happen here) ===
            int width, height;
            byte[] frame;
            using (ILockedFramebuffer framebuffer = readableBitmap.Lock())
            {
                width = framebuffer.Size.Width;
                height = framebuffer.Size.Height;
                int stride = width * 4;

                // Pooled: the render thread returns it once a newer frame replaces it
                frame = RentFrame(stride * height);
                unsafe
                {
                    byte* src = (byte*)framebuffer.Address;
                    if (framebuffer.RowBytes == stride)
                    {
                        new ReadOnlySpan<byte>(src, stride * height).CopyTo(frame);
                    }
                    else
                    {
                        for (int y = 0; y < height; y++)
                            new ReadOnlySpan<byte>(src + y * framebuffer.RowBytes, stride)
                                .CopyTo(frame.AsSpan(y * stride, stride));
                    }
                }
            }

            if (readableBitmap != _innerTarget)
                ((IDisposable)readableBitmap).Dispose();

            CursorState cursor = CaptureCursor();

            bool queued = false;
            try
            {
                queued = _renderQueue.TryAdd(() => RenderFrame(frame, width, height, cursor));
            }
            catch (InvalidOperationException)
            {
                // Disposed while rendering
            }

            if (!queued)
                ReturnFrame(frame); // queue full — drop this frame
        }

        // Frame buffers rented on the UI thread and returned on the render thread. A private
        // stack guarantees they are reused across those two threads; ArrayPool.Shared keeps
        // per-core stacks and can end up allocating a fresh multi-megabyte array every frame.
        private readonly ConcurrentStack<byte[]> _framePool = new();
        private const int FramePoolMax = 4;

        private byte[] RentFrame(int size)
        {
            while (_framePool.TryPop(out byte[]? buffer))
            {
                if (buffer.Length == size) return buffer;
                // Size changed: drop buffers of the old size
            }

            return GC.AllocateUninitializedArray<byte>(size);
        }

        private void ReturnFrame(byte[] buffer)
        {
            if (_framePool.Count < FramePoolMax)
                _framePool.Push(buffer);
        }

        private CursorState CaptureCursor() => new(
            _consoleWindow.CursorPixelPosition,
            _consoleWindow.CursorType,
            _consoleWindow.BitmapCursor,
            _consoleWindow.SoftwareCursorEnabled);

        private IReadableBitmapImpl? GetReadableBitmap()
        {
            if (_innerTarget is IReadableBitmapImpl readable)
                return readable;

            if (_innerTarget is IBitmapImpl)
            {
                // Slow path: encodes the frame as an image and decodes it again. The Skia
                // offscreen target is readable, so this should never run; flag it if it does.
                Debug.WriteLine("[SixelRenderTarget] inner target is not readable — using encode/decode fallback");
                var ms = new MemoryStream();
                _innerTarget.Save(ms);
                ms.Position = 0;
                IBitmapImpl loaded = _fallbackRenderInterface.LoadBitmap(ms);
                if (loaded is IReadableBitmapImpl readableLoaded)
                    return readableLoaded;
                loaded.Dispose();
            }

            return null;
        }

        #endregion

        #region Render thread: frames

        private void RenderFrame(byte[] frame, int width, int height, CursorState cursor)
        {
            // A new cell size (another DPI, another font) comes with a full redraw, which OnResized
            // has already asked for: nothing drawn at the old size may survive it.
            int pendingWidth = _pendingCellPixelWidth;
            if (pendingWidth > 0)
            {
                _cellPixelHeight = _pendingCellPixelHeight;
                _cellPixelWidth = pendingWidth;
                _pendingCellPixelWidth = 0;
            }

            GetVisibleCells(width, height, out int cols, out int rows);
            EnsureMask(cols, rows);

            bool full = _fullRedraw || _shown == null || _shownWidth != width || _shownHeight != height;
            _fullRedraw = false;

            if (full)
            {
                if (_clearPending)
                {
                    _clearPending = false;
                    _terminal.ClearScreen();
                }

                Array.Fill(_dirty, true, 0, cols * rows);
            }
            else
            {
                Array.Clear(_dirty, 0, cols * rows);
                DiffFrames(_shown!, frame, width, cols, rows);
            }

            PlaceCursor(cursor, width, height, cols, rows, out Point cursorDraw, out CursorRegion cursorRegion);

            if (_terminal.Protocol == ImageProtocol.Kitty)
            {
                WriteBlocks(frame, width, cols, rows, full, cursor, cursorDraw, cursorRegion);
            }
            else
            {
                BuildSpans(cols, rows);
                if (_spans.Count > 0)
                    WriteRuns(frame, width, height, cursor, cursorDraw, cursorRegion);
            }

            // This frame is what the terminal shows now
            if (_shown != null)
                ReturnFrame(_shown);
            _shown = frame;
            _shownWidth = width;
            _shownHeight = height;
        }

        /// <summary>
        ///     Cursor-only update: only the cells the cursor left and the cells it now covers
        ///     are rewritten, from the frame the terminal already shows.
        /// </summary>
        private void RenderCursor(CursorState cursor)
        {
            if (_shown == null) return;

            int width = _shownWidth;
            int height = _shownHeight;
            GetVisibleCells(width, height, out int cols, out int rows);
            EnsureMask(cols, rows);
            Array.Clear(_dirty, 0, cols * rows);

            if (!PlaceCursor(cursor, width, height, cols, rows, out Point cursorDraw, out CursorRegion cursorRegion))
                return; // nothing moved or changed

            if (_terminal.Protocol == ImageProtocol.Kitty)
            {
                WriteBlocks(_shown, width, cols, rows, false, cursor, cursorDraw, cursorRegion);
                return;
            }

            BuildSpans(cols, rows);
            if (_spans.Count > 0)
                WriteRuns(_shown, width, height, cursor, cursorDraw, cursorRegion);
        }

        /// <summary>
        ///     Cells of the frame that are inside the terminal. The last terminal row is never
        ///     written: a sixel there makes the terminal scroll.
        /// </summary>
        private void GetVisibleCells(int width, int height, out int cols, out int rows)
        {
            TerminalSize termSize = _terminal.Size;
            cols = Math.Max(0, Math.Min(width / _cellPixelWidth, termSize.Columns));
            rows = Math.Max(0, Math.Min(height / _cellPixelHeight, termSize.Rows - 1));
        }

        private void EnsureMask(int cols, int rows)
        {
            if (_dirty.Length < cols * rows)
                _dirty = new bool[cols * rows];
        }

        /// <summary>
        ///     Mark every cell whose pixels differ between the shown frame and the new one.
        ///     Whole pixel rows are compared first so an unchanged frame costs one
        ///     vectorized compare per row and no per-cell work.
        /// </summary>
        private void DiffFrames(byte[] prev, byte[] cur, int width, int cols, int rows)
        {
            int stride = width * 4;
            int cellBytes = _cellPixelWidth * 4;
            int rowBytes = cols * cellBytes;
            int cellH = _cellPixelHeight;

            for (int row = 0; row < rows; row++)
            {
                int maskRow = row * cols;
                int remaining = cols;
                for (int y = row * cellH, yEnd = y + cellH; y < yEnd && remaining > 0; y++)
                {
                    int off = y * stride;
                    ReadOnlySpan<byte> a = prev.AsSpan(off, rowBytes);
                    ReadOnlySpan<byte> b = cur.AsSpan(off, rowBytes);
                    if (a.SequenceEqual(b)) continue;

                    for (int col = 0; col < cols; col++)
                    {
                        if (_dirty[maskRow + col]) continue;
                        int co = col * cellBytes;
                        if (!a.Slice(co, cellBytes).SequenceEqual(b.Slice(co, cellBytes)))
                        {
                            _dirty[maskRow + col] = true;
                            remaining--;
                        }
                    }
                }
            }
        }

        /// <summary>
        ///     Work out where the software cursor is drawn this frame. If it moved or changed,
        ///     the cells it left and the cells it covers are marked dirty. Returns whether
        ///     anything about the cursor changed.
        /// </summary>
        private bool PlaceCursor(CursorState cursor, int width, int height, int cols, int rows,
            out Point cursorDraw, out CursorRegion cursorRegion)
        {
            cursorDraw = new Point(-1, -1);
            cursorRegion = CursorRegion.None;
            if (cursor.Software)
            {
                cursorDraw = GetCursorDrawPosition(cursor.PixelPosition);
                cursorRegion = GetCursorRegion(cursorDraw, width, height);
            }

            bool changed = cursor.Software != _prevCursor.Software
                || cursorDraw != _prevCursorDraw
                || cursor.Type != _prevCursor.Type
                || !ReferenceEquals(cursor.Bitmap, _prevCursor.Bitmap);

            if (changed)
            {
                MarkRegion(_prevCursorRegion, cols, rows);
                MarkRegion(cursorRegion, cols, rows);
            }

            _prevCursor = cursor;
            _prevCursorDraw = cursorDraw;
            _prevCursorRegion = cursorRegion;
            return changed;
        }

        private void MarkRegion(CursorRegion region, int cols, int rows)
        {
            if (region.IsEmpty) return;
            int r0 = Math.Max(0, region.Row), r1 = Math.Min(rows, region.Row + region.Rows);
            int c0 = Math.Max(0, region.Col), c1 = Math.Min(cols, region.Col + region.Cols);
            for (int r = r0; r < r1; r++)
                for (int c = c0; c < c1; c++)
                    _dirty[r * cols + c] = true;
        }

        private void BuildSpans(int cols, int rows)
        {
            _spans.Clear();
            for (int row = 0; row < rows; row++)
            {
                int baseIdx = row * cols;
                int col = 0;
                while (col < cols)
                {
                    if (!_dirty[baseIdx + col]) { col++; continue; }
                    int start = col;
                    while (col < cols && _dirty[baseIdx + col]) col++;
                    _spans.Add(new Run { Row = row, Col = start, Cols = col - start });
                }
            }
        }

        /// <summary>
        ///     Send the dirty spans. Each span is cut into runs as its cells are indexed: a run
        ///     grows while it stays within 256 colors, so it always indexes exactly; a stretch
        ///     too dense for that (a picture) becomes one Wu run. Runs seen before replay from
        ///     the cache. One flush, inside a synchronized update.
        /// </summary>
        private void WriteRuns(byte[] frame, int width, int height, CursorState cursor, Point cursorDraw, CursorRegion cursorRegion)
        {
            int cellW = _cellPixelWidth;
            int cellH = _cellPixelHeight;
            int stride = width * 4;

            EnsureSize(ref _frameIndices, width * height);

            _terminal.BeginFrame();
            _terminal.HideCaret();
            try
            {
                // Pass 1: cut each span into runs, indexing the exact ones as we go
                _runs.Clear();
                foreach (Run span in _spans)
                {
                    bool touchesCursor = cursorRegion.Overlaps(span);
                    // Byte offset of the span's top-left pixel in the frame
                    byte[] src = frame;
                    int srcStride = stride;
                    int srcX = span.Row * cellH * stride + span.Col * cellW * 4;
                    if (touchesCursor)
                    {
                        // Index from a copy with the cursor drawn in; the frame itself stays clean
                        int spanW = span.Cols * cellW;
                        EnsureSize(ref _spanPixels, spanW * 4 * cellH);
                        CopyRows(frame, stride, srcX, _spanPixels!, spanW * 4, 0, spanW * 4, cellH);
                        DrawCursor(_spanPixels!, spanW, spanW * 4, cellH, cursor,
                            (float)(cursorDraw.X - span.Col * cellW), (float)(cursorDraw.Y - span.Row * cellH));
                        src = _spanPixels!;
                        srcStride = spanW * 4;
                        srcX = 0;
                    }

                    SegmentSpan(src, srcStride, srcX, width, span, !touchesCursor);
                }

                // Pass 2: replay cached runs, encode exact runs, collect Wu runs
                _wuRuns.Clear();
                int wuWidth = 0;
                for (int i = 0; i < _runs.Count; i++)
                {
                    Run run = _runs[i];
                    if (run.Cacheable)
                    {
                        run.Key = KeyOf(frame, stride, run);
                        if (_runCache.TryGet(run.Key, out byte[] cached))
                        {
                            _terminal.WriteImage(new CellPosition((ushort)run.Col, (ushort)run.Row), cached);
                            continue;
                        }
                    }

                    if (run.Wu)
                    {
                        run.WuX = wuWidth;
                        wuWidth += run.Cols * cellW;
                        _wuRuns.Add(run);
                        continue;
                    }

                    EncodeRun(run, width);
                }

                // Pass 3: Wu runs share one quantize, then encode like the rest
                if (wuWidth > 0)
                {
                    int wuStride = wuWidth * 4;
                    int wuPx = wuWidth * cellH;
                    EnsureSize(ref _wuGather, wuStride * cellH);
                    EnsureSize(ref _wuIndices, wuPx);
                    byte[] wuGather = _wuGather!;
                    foreach (Run run in _wuRuns)
                        CopyRows(frame, stride, run.Row * cellH * stride + run.Col * cellW * 4,
                            wuGather, wuStride, run.WuX * 4, run.Cols * cellW * 4, cellH);

                    if (cursor.Software && !cursorRegion.IsEmpty)
                    {
                        foreach (Run run in _wuRuns)
                        {
                            if (run.Cacheable) continue; // only runs the cursor touches
                            DrawCursor(wuGather, wuWidth, wuStride, cellH, cursor,
                                (float)(cursorDraw.X - run.Col * cellW + run.WuX), (float)(cursorDraw.Y - run.Row * cellH),
                                run.WuX, run.Cols * cellW);
                        }
                    }

                    Sixel.QuantizeWu(wuGather, wuPx, _wuIndices!, out byte[] wuPalette, out int wuCount);

                    foreach (Run run in _wuRuns)
                    {
                        int runW = run.Cols * cellW;
                        // Scatter this run's indices to frame coordinates, where EncodeRun reads them
                        CopyRows(_wuIndices!, wuWidth, run.WuX, _frameIndices!, width,
                            run.Row * cellH * width + run.Col * cellW, runW, cellH);
                        Run r = run;
                        r.Palette = wuPalette;
                        r.PaletteCount = wuCount;
                        EncodeRun(r, width);
                    }
                }
            }
            finally
            {
                // Always close the synchronized update, or a failed frame freezes the terminal
                _terminal.SetCaretPosition(new CellPosition(0, 0));
                _terminal.EndFrame();
            }
        }

        /// <summary>
        ///     Cut one span of dirty cells into runs. Cells are indexed into the current run's
        ///     palette one by one; when a cell does not fit, a long run is closed and a new one
        ///     started at that cell, while a short one means dense colors, and the rest of the
        ///     span becomes a single Wu run.
        /// </summary>
        private void SegmentSpan(byte[] src, int srcStride, int srcX, int width, Run span, bool cacheable)
        {
            int cellW = _cellPixelWidth;
            Dictionary<uint, byte> map = _runMap;
            map.Clear();

            int end = span.Col + span.Cols;
            int runStart = span.Col;
            for (int c = span.Col; c < end; c++)
            {
                if (IndexCell(src, srcStride, srcX + (c - span.Col) * cellW * 4, width, span.Row, c, map))
                    continue;

                if (c - runStart >= MinExactCells)
                {
                    AddExactRun(span.Row, runStart, c - runStart, map, cacheable);
                    map.Clear();
                    runStart = c;
                    c--; // index this cell again, against the fresh palette
                    continue;
                }

                _runs.Add(new Run { Row = span.Row, Col = runStart, Cols = end - runStart, Wu = true, Cacheable = cacheable });
                return;
            }

            if (end > runStart)
                AddExactRun(span.Row, runStart, end - runStart, map, cacheable);
        }

        /// <summary>
        ///     Index one cell's pixels into <paramref name="map"/>, writing indices into
        ///     <see cref="_frameIndices"/>. Returns false as soon as a 257th color appears.
        /// </summary>
        private bool IndexCell(byte[] src, int srcStride, int srcX, int width, int row, int col, Dictionary<uint, byte> map)
        {
            int cellW = _cellPixelWidth;
            int cellH = _cellPixelHeight;
            byte[] indices = _frameIndices!;
            int baseY = row * cellH;
            uint last = 0;
            byte lastIdx = 0;
            bool haveLast = false;

            for (int y = 0; y < cellH; y++)
            {
                int so = y * srcStride + srcX;
                int io = (baseY + y) * width + col * cellW;
                for (int x = 0; x < cellW; x++, so += 4)
                {
                    uint color = BitConverter.ToUInt32(src, so) & 0x00FFFFFFu;
                    if (haveLast && color == last)
                    {
                        indices[io + x] = lastIdx;
                        continue;
                    }

                    if (!map.TryGetValue(color, out byte idx))
                    {
                        if (map.Count == 256) return false;
                        idx = (byte)map.Count;
                        map.Add(color, idx);
                    }

                    indices[io + x] = idx;
                    last = color;
                    lastIdx = idx;
                    haveLast = true;
                }
            }

            return true;
        }

        private void AddExactRun(int row, int col, int cols, Dictionary<uint, byte> map, bool cacheable)
        {
            int count = map.Count;
            byte[] palette = new byte[count * 4];
            foreach (KeyValuePair<uint, byte> entry in map)
            {
                int po = entry.Value * 4;
                palette[po] = (byte)entry.Key;
                palette[po + 1] = (byte)(entry.Key >> 8);
                palette[po + 2] = (byte)(entry.Key >> 16);
                palette[po + 3] = 0xFF;
            }

            _runs.Add(new Run { Row = row, Col = col, Cols = cols, Palette = palette, PaletteCount = count, Cacheable = cacheable });
        }

        /// <summary>Encode one run from its indices in <see cref="_frameIndices"/>, send it, and cache it.</summary>
        private void EncodeRun(Run run, int width)
        {
            int cellW = _cellPixelWidth;
            int cellH = _cellPixelHeight;
            int runW = run.Cols * cellW;
            EnsureSize(ref _runIndices, runW * cellH);
            byte[] runIndices = _runIndices!;
            CopyRows(_frameIndices!, width, run.Row * cellH * width + run.Col * cellW, runIndices, runW, 0, runW, cellH);

            var sixel = new Sixel(run.Palette!, run.PaletteCount, runIndices, runW, cellH, cellW, cellH);
            _runWriter.ResetWrittenCount();
            sixel.RenderTo(_runWriter);
            ReadOnlySpan<byte> bytes = _runWriter.WrittenSpan;

            _terminal.WriteImage(new CellPosition((ushort)run.Col, (ushort)run.Row), bytes);
            if (run.Cacheable)
                _runCache.Add(run.Key, bytes.ToArray());
        }

        #endregion

        #region Render thread: kitty blocks

        /// <summary>
        ///     Send every block that holds a dirty cell. A block's image id is its position in
        ///     the grid, so sending it again replaces what the terminal shows there; nothing
        ///     accumulates and nothing needs deleting while the grid stays the same size. Blocks
        ///     seen before replay their compressed pixels from the cache.
        /// </summary>
        private void WriteBlocks(byte[] frame, int width, int cols, int rows, bool full,
            CursorState cursor, Point cursorDraw, CursorRegion cursorRegion)
        {
            if (Array.IndexOf(_dirty, true, 0, cols * rows) < 0)
                return;

            int cellW = _cellPixelWidth;
            int cellH = _cellPixelHeight;
            int stride = width * 4;
            int blockCols = (cols + BlockCols - 1) / BlockCols;

            _terminal.BeginFrame();
            _terminal.HideCaret();
            try
            {
                // A full frame follows a resize or a clear: the grid may have changed shape, so
                // ids may mean new positions. Start from no images at all.
                if (full)
                    _terminal.WriteEsc(Esc.KittyDeleteAllImages);

                for (int by = 0; by * BlockRows < rows; by++)
                {
                    for (int bx = 0; bx * BlockCols < cols; bx++)
                    {
                        int c0 = bx * BlockCols;
                        int r0 = by * BlockRows;
                        int bc = Math.Min(BlockCols, cols - c0);
                        int br = Math.Min(BlockRows, rows - r0);
                        if (!AnyDirty(c0, r0, bc, br, cols))
                            continue;

                        int w = bc * cellW;
                        int h = br * cellH;
                        int blockBytes = w * h * 4;
                        EnsureSize(ref _blockPixels, blockBytes);
                        byte[] block = _blockPixels!;
                        CopyRows(frame, stride, r0 * cellH * stride + c0 * cellW * 4, block, w * 4, 0, w * 4, h);

                        bool touchesCursor = cursor.Software && cursorRegion.OverlapsCells(c0, r0, bc, br);
                        if (touchesCursor)
                            DrawCursor(block, w, w * 4, h, cursor,
                                (float)(cursorDraw.X - c0 * cellW), (float)(cursorDraw.Y - r0 * cellH));

                        ContentKey key = default;
                        byte[]? payload = null;
                        if (!touchesCursor)
                        {
                            key = ContentKey.Of(block.AsSpan(0, blockBytes), w, h);
                            if (_runCache.TryGet(key, out byte[] cached))
                                payload = cached;
                        }

                        if (payload == null)
                        {
                            payload = KittyPng
                                ? KittyGraphics.EncodePng(block.AsSpan(0, blockBytes), w, h)
                                : KittyGraphics.CompressRgb(block.AsSpan(0, blockBytes), w, h, ref _blockRgb, _zlibScratch);
                            if (!touchesCursor)
                                _runCache.Add(key, payload);
                        }

                        int imageId = by * blockCols + bx + 1;
                        _runWriter.ResetWrittenCount();
                        KittyGraphics.WriteTransmit(_runWriter, imageId, w, h, payload, KittyPng);
                        _terminal.WriteImage(new CellPosition((ushort)c0, (ushort)r0), _runWriter.WrittenSpan);
                    }
                }
            }
            finally
            {
                _terminal.SetCaretPosition(new CellPosition(0, 0));
                _terminal.EndFrame();
            }
        }

        private bool AnyDirty(int c0, int r0, int bc, int br, int cols)
        {
            for (int r = r0; r < r0 + br; r++)
                for (int c = c0; c < c0 + bc; c++)
                    if (_dirty[r * cols + c]) return true;
            return false;
        }

        private ContentKey KeyOf(byte[] frame, int stride, Run run)
        {
            int cellH = _cellPixelHeight;
            int runBytes = run.Cols * _cellPixelWidth * 4;
            int srcX = run.Col * _cellPixelWidth * 4;
            _hasher.Reset();
            for (int y = 0; y < cellH; y++)
                _hasher.Append(frame.AsSpan((run.Row * cellH + y) * stride + srcX, runBytes));
            return new ContentKey(_hasher.GetCurrentHashAsUInt128(), run.Cols * _cellPixelWidth, cellH);
        }

        /// <summary>Draw the software cursor into a BGRA strip at (dx, dy), clipped to [clipX, clipX+clipW).</summary>
        private void DrawCursor(byte[] pixels, int widthPx, int strideBytes, int heightPx, CursorState cursor, float dx, float dy,
            int clipX = 0, int clipW = -1)
        {
            EnsureCursorBitmap(cursor.Type, cursor.Bitmap, _cellPixelWidth, _cellPixelHeight);
            if (_cursorBitmap == null) return;

            unsafe
            {
                fixed (byte* p = pixels)
                {
                    using var bitmap = new SKBitmap();
                    bitmap.InstallPixels(new SKImageInfo(widthPx, heightPx, SKColorType.Bgra8888, SKAlphaType.Premul),
                        (IntPtr)p, strideBytes);
                    using var canvas = new SKCanvas(bitmap);
                    if (clipW > 0)
                        canvas.ClipRect(new SKRect(clipX, 0, clipX + clipW, heightPx));
                    canvas.DrawBitmap(_cursorBitmap, dx, dy);
                    canvas.Flush();
                }
            }
        }

        private static void EnsureSize(ref byte[]? buffer, int size)
        {
            if (buffer == null || buffer.Length < size)
                buffer = GC.AllocateUninitializedArray<byte>(size);
        }

        #endregion

        #region Software cursor

        /// <summary>
        ///     Make sure <see cref="_cursorBitmap"/> matches the requested cursor (render thread).
        /// </summary>
        private void EnsureCursorBitmap(StandardCursorType cursorType, BitmapCursorImpl? bitmapCursor,
            int cellW, int cellH)
        {
            if (bitmapCursor != null)
            {
                if (_cachedBitmapCursor != bitmapCursor)
                {
                    _cursorBitmap?.Dispose();
                    _cursorBitmap = ScaleBitmapCursor(bitmapCursor, cellW, cellH);
                    _cachedBitmapCursor = bitmapCursor;
                    _cachedCursorType = (StandardCursorType)(-1);
                }
            }
            else if (_cachedCursorType != cursorType || _cursorBitmap == null)
            {
                _cursorBitmap?.Dispose();
                _cursorBitmap = TerminalCursor.GetCursorBitmap(cursorType, cellW, cellH);
                _cachedCursorType = cursorType;
                _cachedBitmapCursor = null;
            }
        }

        private static SKBitmap ScaleBitmapCursor(BitmapCursorImpl cursor, int cellW, int cellH)
        {
            int targetW = cellW * TerminalCursor.CursorCellWidth;
            int targetH = cellH;

            var scaled = new SKBitmap(targetW, targetH, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(scaled);
            canvas.Clear(SKColors.Transparent);
            canvas.DrawBitmap(cursor.Bitmap,
                new SKRect(0, 0, cursor.Bitmap.Width, cursor.Bitmap.Height),
                new SKRect(0, 0, targetW, targetH),
                new SKPaint());
            return scaled;
        }

        /// <summary>
        ///     Pixel position of the cursor bitmap's top-left corner. With SGR-Pixels mouse
        ///     reporting (DECSET 1016) the cursor follows the pointer exactly; otherwise the
        ///     terminal only reports cells, so the cursor snaps to the cell origin.
        /// </summary>
        private Point GetCursorDrawPosition(Point cursorPx)
        {
            if (_terminal.PixelMouseSupported)
                return new Point(Math.Floor(cursorPx.X), Math.Floor(cursorPx.Y));

            int col = (int)(cursorPx.X / _cellPixelWidth);
            int row = (int)(cursorPx.Y / _cellPixelHeight);
            return new Point(col * _cellPixelWidth, row * _cellPixelHeight);
        }

        /// <summary>
        ///     The cells a cursor bitmap drawn at <paramref name="drawPos"/> touches, clamped to
        ///     the framebuffer. A pixel-positioned cursor may straddle up to 3 columns and 2 rows.
        /// </summary>
        private CursorRegion GetCursorRegion(Point drawPos, int fbW, int fbH)
        {
            int cellW = _cellPixelWidth;
            int cellH = _cellPixelHeight;
            int x = (int)drawPos.X;
            int y = (int)drawPos.Y;
            if (x < 0 || y < 0) return CursorRegion.None;

            int w = cellW * TerminalCursor.CursorCellWidth;
            int h = cellH;
            int col0 = x / cellW;
            int row0 = y / cellH;
            int col1 = Math.Min((x + w - 1) / cellW, fbW / cellW - 1);
            int row1 = Math.Min((y + h - 1) / cellH, fbH / cellH - 1);
            if (col1 < col0 || row1 < row0) return CursorRegion.None;

            return new CursorRegion(col0, row0, col1 - col0 + 1, row1 - row0 + 1);
        }

        /// <summary>
        ///     Called from the cursor refresh loop when only the cursor moved or changed.
        /// </summary>
        internal void UpdateCursorOnly()
        {
            if (_disposed) return;
            if (!_consoleWindow.SoftwareCursorEnabled) return; // terminal draws its own pointer (OSC 22)

            CursorState cursor = CaptureCursor();
            try
            {
                _renderQueue.TryAdd(() => RenderCursor(cursor));
            }
            catch (InvalidOperationException)
            {
                // Disposed
            }
        }

        private void StartCursorRefreshLoop()
        {
            System.Threading.Tasks.Task.Run(async () =>
            {
                while (!_disposed)
                {
                    await System.Threading.Tasks.Task.Delay(33); // ~30fps cursor refresh

                    if (!_consoleWindow.CursorDirty) continue;
                    _consoleWindow.ClearCursorDirty();

                    UpdateCursorOnly();
                }
            });
        }

        #endregion

        private void OnResized(Size size, WindowResizeReason reason)
        {
            // The clear is done by the render thread as part of the next full frame. Issued
            // from here it could land in the middle of a frame being written, erasing cells
            // that had just been painted and would not be painted again until they change.
            if (_terminal.CellPixelWidth > 0 && _terminal.CellPixelHeight > 0 &&
                (_terminal.CellPixelWidth != _cellPixelWidth || _terminal.CellPixelHeight != _cellPixelHeight))
            {
                // Height first: the render thread takes the width as the signal and reads the
                // height after it.
                _pendingCellPixelHeight = _terminal.CellPixelHeight;
                _pendingCellPixelWidth = _terminal.CellPixelWidth;
            }

            _clearPending = true;
            _fullRedraw = true;
        }

        private volatile bool _clearPending; // written by the UI thread on resize

        /// <summary>Cursor state captured on the UI thread for the render thread.</summary>
        private readonly record struct CursorState(Point PixelPosition, StandardCursorType Type,
            BitmapCursorImpl? Bitmap, bool Software);

        /// <summary>A horizontal run of dirty cells on one row.</summary>
        private struct Run
        {
            public int Row;
            public int Col;
            public int Cols;
            public bool Cacheable;   // false when the cursor is drawn into it
            public bool Wu;          // too many colors to index exactly: quantized with the other Wu runs
            public int WuX;          // column in the Wu gather image (pixels)
            public ContentKey Key;
            public byte[]? Palette;  // the palette this run is encoded against
            public int PaletteCount;
        }

        /// <summary>Copy a block of rows between two row-major buffers with different strides (byte units).</summary>
        private static void CopyRows(byte[] src, int srcStride, int srcX, byte[] dst, int dstStride, int dstX, int bytes, int rows)
        {
            for (int y = 0; y < rows; y++)
                Buffer.BlockCopy(src, y * srcStride + srcX, dst, y * dstStride + dstX, bytes);
        }

        /// <summary>Cell-aligned rectangle covered by the software cursor.</summary>
        private readonly record struct CursorRegion(int Col, int Row, int Cols, int Rows)
        {
            public static CursorRegion None => new(-1, -1, 0, 0);
            public bool IsEmpty => Cols <= 0 || Rows <= 0;

            public bool Overlaps(in Run run) =>
                !IsEmpty &&
                run.Row >= Row && run.Row < Row + Rows &&
                run.Col < Col + Cols && Col < run.Col + run.Cols;

            public bool OverlapsCells(int col, int row, int cols, int rows) =>
                !IsEmpty &&
                col < Col + Cols && Col < col + cols &&
                row < Row + Rows && Row < row + rows;
        }
    }
}
