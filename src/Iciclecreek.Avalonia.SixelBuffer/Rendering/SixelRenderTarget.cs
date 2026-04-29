//#define DEBUG_RESERVED_COLORS
#nullable enable
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Input;
using Iciclecreek.Avalonia.SixelBuffer.Platform;
using Iciclecreek.Avalonia.SixelBuffer.Terminal;
using SkiaSharp;
using Avalonia.Threading;
using Avalonia;
using System.Threading.Tasks;

namespace Iciclecreek.Avalonia.SixelBuffer.Rendering
{
    internal class SixelRenderTarget : IRenderTarget
    {
        private readonly TerminalWindow _consoleWindow;
        private readonly ITerminal _terminal;
        private readonly IPlatformRenderInterface _fallbackRenderInterface;
        private readonly IPlatformRenderInterfaceContext _fallbackContext;
        private readonly int _cellPixelWidth;
        private readonly int _cellPixelHeight;
        private IDrawingContextLayerImpl _innerTarget;
        private bool _disposed;

        // Previous frames for diffing
        private byte[]? _previousFrame;      // with cursor composited (for diffing)
        private byte[]? _cleanFrame;          // without cursor (for cursor restore)
        private int _previousWidth;
        private int _previousHeight;

        // Dedicated render thread — serializes all terminal output
        private readonly BlockingCollection<Action> _renderQueue = new(boundedCapacity: 2);

        // Cached palette from last full frame (reused for dirty rect encoding)
        private byte[]? _cachedPalette;
        private int _cachedReservedCount;

        // Background thread sets this when it computes a new palette
        private byte[]? _pendingPalette;

        // Software cursor bitmap (cached per cursor type)
        private SKBitmap? _cursorBitmap;
        private StandardCursorType _cachedCursorType = (StandardCursorType)(-1);

        // Previous cursor cell position for fast cursor-only updates
        private int _prevCursorCol = -1;
        private int _prevCursorRow = -1;

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
            StartPaletteRefreshLoop();
        }

        public RenderTargetProperties Properties => new RenderTargetProperties
        {
            // Tell compositor we retain frame contents — it will only redraw dirty regions
            RetainsPreviousFrameContents = true,
            IsSuitableForDirectRendering = true
        };

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
                // Size changed — force full redraw
                _previousFrame = null;
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
                        catch (InvalidOperationException ex)
                        {
                            System.Diagnostics.Debug.WriteLine($"[RenderThread] {ex.Message}");
                        }
                    }
                }
                catch (TaskCanceledException)
                {
                }
            })
            {
                IsBackground = true,
                Name = "SixelRenderThread"
            };
            thread.Start();
        }

        private void RenderToDeviceCore()
        {
            IReadableBitmapImpl? readableBitmap = GetReadableBitmap();
            if (readableBitmap == null)
                return;

            // === UI THREAD: only read the locked framebuffer (must happen here) ===
            int width, height;
            byte[] cleanFrame;
            using (ILockedFramebuffer framebuffer = readableBitmap.Lock())
            {
                width = framebuffer.Size.Width;
                height = framebuffer.Size.Height;

                cleanFrame = new byte[width * height * 4];
                unsafe
                {
                    byte* src = (byte*)framebuffer.Address;
                    for (int y = 0; y < height; y++)
                    {
                        var srcSpan = new ReadOnlySpan<byte>(src + y * framebuffer.RowBytes, width * 4);
                        srcSpan.CopyTo(cleanFrame.AsSpan(y * width * 4, width * 4));
                    }
                }
            }

            if (readableBitmap != _innerTarget)
                ((IDisposable)readableBitmap).Dispose();

            // Capture minimal state needed by render thread
            byte[]? pendingPalette = Interlocked.Exchange(ref _pendingPalette, null);
            bool fullFrame = pendingPalette != null
                || _previousFrame == null
                || _previousWidth != width
                || _previousHeight != height;
            byte[]? prevFrame = _previousFrame;
            Point cursorPx = _consoleWindow.CursorPixelPosition;
            var cursorType = _consoleWindow.CursorType;
            var bitmapCursor = _consoleWindow.BitmapCursor;

            int cellW = _cellPixelWidth;
            int cellH = _cellPixelHeight;
            TerminalSize termSize = _terminal.Size;

            // === RENDER THREAD: everything else ===
            if (!_renderQueue.TryAdd(() =>
            {
                // Apply pending palette
                if (pendingPalette != null)
                    _cachedPalette = pendingPalette;

                byte[]? palette = _cachedPalette;
                int visW = Math.Min(width, termSize.Columns * cellW);
                int visH = Math.Min(height, (termSize.Rows - 1) * cellH);

                // Composite cursor
                var renderBitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
                unsafe
                {
                    fixed (byte* src = cleanFrame)
                        Buffer.MemoryCopy(src, (byte*)renderBitmap.GetPixels(), cleanFrame.Length, cleanFrame.Length);
                }

                CompositeCursorOnBitmap(renderBitmap, cursorPx, cursorType, bitmapCursor, cellW, cellH);

                byte[] renderFrame = new byte[width * height * 4];
                unsafe
                {
                    byte* pixels = (byte*)renderBitmap.GetPixels();
                    new ReadOnlySpan<byte>(pixels, renderFrame.Length).CopyTo(renderFrame);
                }

                renderBitmap.Dispose();

                // Update shared state (render thread owns these during execution)
                _previousFrame = renderFrame;
                _cleanFrame = cleanFrame;
                _previousWidth = width;
                _previousHeight = height;

                if (_cleanFrameBitmap == null || _cleanFrameBitmap.Width != width || _cleanFrameBitmap.Height != height)
                {
                    _cleanFrameBitmap?.Dispose();
                    _cleanFrameBitmap = new SKBitmap(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
                }

                unsafe
                {
                    fixed (byte* src = cleanFrame)
                        Buffer.MemoryCopy(src, (byte*)_cleanFrameBitmap.GetPixels(),
                            cleanFrame.Length, cleanFrame.Length);
                }

                _prevCursorCol = (int)(cursorPx.X / cellW);
                _prevCursorRow = (int)(cursorPx.Y / cellH);

                // Encode and write
                _terminal.HideCaret();

                if (fullFrame)
                {
                    // Always run fresh Quantize on full frames so frequency-based
                    // reserved colors are computed from the current frame content
                    Sixel sixel = Sixel.CreateFromBitmap(renderFrame, visW, visH, cellW, cellH);
                    _cachedPalette = sixel.Palette;
                    _cachedReservedCount = sixel.ReservedCount;

                    _terminal.SetCaretPosition(new CellPosition(0, 0));
#if DEBUG_RESERVED_COLORS
                    _terminal.WriteSixel(new CellPosition(0, 0), sixel.CreateDebugCopy());
#else
                    _terminal.WriteSixel(new CellPosition(0, 0), sixel);
#endif
                }
                else
                {
                    RenderDirtyRegions(renderFrame, prevFrame!, width, height);
                }

                _terminal.SetCaretPosition(new CellPosition(0, 0));
                _terminal.Flush();
            }))
            {
                // Queue full — drop frame
            }
        }

        /// <summary>
        ///     Composite cursor onto a bitmap (called on render thread).
        /// </summary>
        private void CompositeCursorOnBitmap(SKBitmap renderFrame, Point cursorPx,
            StandardCursorType cursorType, BitmapCursorImpl? bitmapCursor,
            int cellW, int cellH)
        {
            // Rebuild cursor bitmap if needed
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
                _cursorBitmap = SoftwareCursor.GetCursorBitmap(cursorType, cellW, cellH);
                _cachedCursorType = cursorType;
                _cachedBitmapCursor = null;
            }

            if (_cursorBitmap == null) return;

            int cellCol = (int)(cursorPx.X / cellW);
            int cellRow = (int)(cursorPx.Y / cellH);
            float drawX = cellCol * cellW;
            float drawY = cellRow * cellH;

            using var canvas = new SKCanvas(renderFrame);
            canvas.DrawBitmap(_cursorBitmap, drawX, drawY);
        }

        /// <summary>
        ///     Fast cursor-only update — called when only the cursor moved, no Avalonia frame change.
        ///     Restores old cursor cell from clean framebuffer, draws new cursor cell.
        ///     Uses cached palette to skip quantization.
        /// </summary>
        internal void UpdateCursorOnly()
        {
            if (_cleanFrame == null || _previousFrame == null) return;

            Point cursorPx = _consoleWindow.CursorPixelPosition;
            int newCol = (int)(cursorPx.X / _cellPixelWidth);
            int newRow = (int)(cursorPx.Y / _cellPixelHeight);

            if (newCol == _prevCursorCol && newRow == _prevCursorRow) return;

            int oldCol = _prevCursorCol;
            int oldRow = _prevCursorRow;
            int fbW = _previousWidth;
            int fbH = _previousHeight;
            byte[] cleanFrame = _cleanFrame;

            _prevCursorCol = newCol;
            _prevCursorRow = newRow;

            _renderQueue.TryAdd(() =>
            {
                // Restore old cursor cell from clean framebuffer
                if (oldCol >= 0 && oldRow >= 0)
                    WriteCellFromFrame(cleanFrame, fbW, fbH, oldCol, oldRow);

                // Draw new cursor cell (clean framebuffer + cursor composited)
                WriteCursorCell(newCol, newRow, fbW, fbH);

                _terminal.SetCaretPosition(new CellPosition(0, 0));
                _terminal.Flush();
            });
        }

        private void WriteCellFromFrame(byte[] frame, int fbW, int fbH, int col, int row)
        {
            int cellW = _cellPixelWidth;
            int cellH = _cellPixelHeight;
            int cursorCells = SoftwareCursor.CursorCellWidth;
            int regionW = cellW * cursorCells;
            int px = col * cellW;
            int py = row * cellH;
            int clampedW = Math.Min(regionW, fbW - px);
            if (clampedW <= 0 || py + cellH > fbH) return;

            byte[] region = new byte[clampedW * cellH * 4];
            for (int y = 0; y < cellH; y++)
                Array.Copy(frame, ((py + y) * fbW + px) * 4, region, y * clampedW * 4, clampedW * 4);

            Sixel sixel = Sixel.CreateFromBitmap(region, clampedW, cellH, cellW, cellH, _cachedPalette, _cachedReservedCount);
            var pos = new CellPosition((ushort)col, (ushort)row);
            _terminal.SetCaretPosition(pos);
#if DEBUG_RESERVED_COLORS
            _terminal.WriteSixel(pos, sixel.CreateDebugCopy());
#else
            _terminal.WriteSixel(pos, sixel);
#endif
        }

        private SKBitmap? _cleanFrameBitmap;

        private void WriteCursorCell(int col, int row, int fbW, int fbH)
        {
            int cellW = _cellPixelWidth;
            int cellH = _cellPixelHeight;
            int cursorCells = SoftwareCursor.CursorCellWidth;
            int regionW = cellW * cursorCells;
            int px = col * cellW;
            int py = row * cellH;
            int clampedW = Math.Min(regionW, fbW - px);
            if (clampedW <= 0 || py + cellH > fbH) return;

            // Get cursor bitmap
            var cursorType = _consoleWindow.CursorType;
            if (_cachedCursorType != cursorType || _cursorBitmap == null)
            {
                _cursorBitmap?.Dispose();
                _cursorBitmap = SoftwareCursor.GetCursorBitmap(cursorType, cellW, cellH);
                _cachedCursorType = cursorType;
            }

            if (_cleanFrameBitmap == null) return;

            // Composite: blit from clean frame + draw cursor on top
            var cellBitmap = new SKBitmap(clampedW, cellH, SKColorType.Bgra8888, SKAlphaType.Premul);
            using (var canvas = new SKCanvas(cellBitmap))
            {
                canvas.DrawBitmap(_cleanFrameBitmap!,
                    new SKRect(px, py, px + clampedW, py + cellH),
                    new SKRect(0, 0, clampedW, cellH));
                canvas.DrawBitmap(_cursorBitmap, 0, 0);
            }

            byte[] composited = new byte[clampedW * cellH * 4];
            unsafe
            {
                byte* pixels = (byte*)cellBitmap.GetPixels();
                new ReadOnlySpan<byte>(pixels, composited.Length).CopyTo(composited);
            }

            cellBitmap.Dispose();

            Sixel sixel = Sixel.CreateFromBitmap(composited, clampedW, cellH, cellW, cellH, _cachedPalette, _cachedReservedCount);
            var pos = new CellPosition((ushort)col, (ushort)row);
            _terminal.SetCaretPosition(pos);
#if DEBUG_RESERVED_COLORS
            _terminal.WriteSixel(pos, sixel.CreateDebugCopy());
#else
            _terminal.WriteSixel(pos, sixel);
#endif
        }


        private BitmapCursorImpl? _cachedBitmapCursor;

        private static SKBitmap ScaleBitmapCursor(BitmapCursorImpl cursor, int cellW, int cellH)
        {
            int targetW = cellW * SoftwareCursor.CursorCellWidth;
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

        private void RenderDirtyRegions(byte[] current, byte[] previous, int width, int height)
        {
            int cellW = _cellPixelWidth;
            int cellH = _cellPixelHeight;

            // Clamp to visible terminal area. Reserve the last row — writing a Sixel
            // on the very last row causes the terminal to scroll.
            TerminalSize termSize = _terminal.Size;
            int visiblePixelW = termSize.Columns * cellW;
            int visiblePixelH = (termSize.Rows - 1) * cellH;
            int clampedW = Math.Min(width, visiblePixelW);
            int clampedH = Math.Min(height, visiblePixelH);

            int cols = (clampedW + cellW - 1) / cellW;
            int rows = (clampedH + cellH - 1) / cellH;

            // Find bounding box of all dirty cells
            int minCol = int.MaxValue, maxCol = int.MinValue;
            int minRow = int.MaxValue, maxRow = int.MinValue;

            for (int cellRow = 0; cellRow < rows; cellRow++)
            {
                for (int cellCol = 0; cellCol < cols; cellCol++)
                {
                    int px = cellCol * cellW;
                    int py = cellRow * cellH;
                    int cw = Math.Min(cellW, width - px);
                    int ch = Math.Min(cellH, height - py);

                    if (cw > 0 && ch > 0 && IsCellDirty(current, previous, width, px, py, cw, ch))
                    {
                        if (cellCol < minCol) minCol = cellCol;
                        if (cellCol > maxCol) maxCol = cellCol;
                        if (cellRow < minRow) minRow = cellRow;
                        if (cellRow > maxRow) maxRow = cellRow;
                    }
                }
            }

            if (minCol > maxCol) return; // Nothing dirty

            // Emit one Sixel covering the bounding box, snapped to cell boundaries
            int pixelX = minCol * cellW;
            int pixelY = minRow * cellH;
            int regionW = (maxCol - minCol + 1) * cellW;
            int regionH = (maxRow - minRow + 1) * cellH;

            // Clamp to frame and visible terminal bounds
            regionW = Math.Min(regionW, Math.Min(width, visiblePixelW) - pixelX);
            regionH = Math.Min(regionH, Math.Min(height, visiblePixelH) - pixelY);

            // Pad to cell boundaries
            int paddedW = ((regionW + cellW - 1) / cellW) * cellW;
            int paddedH = ((regionH + cellH - 1) / cellH) * cellH;

            byte[] regionBgrx = new byte[paddedW * paddedH * 4];
            for (int y = 0; y < regionH; y++)
            {
                int srcOffset = ((pixelY + y) * width + pixelX) * 4;
                int dstOffset = y * paddedW * 4;
                Array.Copy(current, srcOffset, regionBgrx, dstOffset, Math.Min(regionW, width - pixelX) * 4);
            }

            Sixel sixel = Sixel.CreateFromBitmap(regionBgrx, paddedW, paddedH, cellW, cellH, _cachedPalette, _cachedReservedCount);
            _cachedPalette ??= sixel.Palette; // cache on first use if no full frame yet
            var pos = new CellPosition((ushort)minCol, (ushort)minRow);
            _terminal.SetCaretPosition(pos);
#if DEBUG_RESERVED_COLORS
            _terminal.WriteSixel(pos, sixel.CreateDebugCopy());
#else
            _terminal.WriteSixel(pos, sixel);
#endif
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsCellDirty(byte[] current, byte[] previous, int width,
            int px, int py, int cw, int ch)
        {
            for (int y = py; y < py + ch; y++)
            {
                int offset = (y * width + px) * 4;
                int len = cw * 4;
                if (!current.AsSpan(offset, len).SequenceEqual(previous.AsSpan(offset, len)))
                    return true;
            }

            return false;
        }

        private void StartPaletteRefreshLoop()
        {
            System.Threading.Tasks.Task.Run(async () =>
            {
                while (!_disposed)
                {
                    await System.Threading.Tasks.Task.Delay(500);

                    byte[]? frame = Volatile.Read(ref _cleanFrame);
                    byte[]? oldPalette = Volatile.Read(ref _cachedPalette);
                    if (frame == null || _previousWidth == 0 || _previousHeight == 0) continue;

                    int width = _previousWidth;
                    int height = _previousHeight;
                    if (frame.Length < width * height * 4) continue;

                    var (newPalette, _) = Sixel.QuantizeForPalette(frame);

                    // Only flag a refresh if enough palette entries changed
                    if (oldPalette != null && PaletteDiffCount(oldPalette, newPalette) < 20)
                        continue;

                    Volatile.Write(ref _pendingPalette, newPalette);

                    // Trigger a render on UI thread — compositor won't fire on its own if nothing visual changed
                    Dispatcher.UIThread.Post(() => _consoleWindow.InvalidateRender());
                }
            });
        }

        /// <summary>Count how many palette entries differ (each entry is 4 bytes BGRX).</summary>
        private static int PaletteDiffCount(byte[] a, byte[] b)
        {
            int count = 0;
            int entries = Math.Min(a.Length, b.Length) / 4;
            for (int i = 0; i < entries; i++)
            {
                int off = i * 4;
                if (a[off] != b[off] || a[off + 1] != b[off + 1] || a[off + 2] != b[off + 2])
                    count++;
            }

            return count;
        }

        private IReadableBitmapImpl? GetReadableBitmap()
        {
            if (_innerTarget is IReadableBitmapImpl readable)
                return readable;

            if (_innerTarget is IBitmapImpl)
            {
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

        private void OnResized(Size size, WindowResizeReason reason)
        {
            _previousFrame = null; // Force full redraw on resize
            _terminal.ClearScreen();
        }

    }
}
