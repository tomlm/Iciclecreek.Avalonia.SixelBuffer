using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Input;
using Avalonia.Platform;
using SkiaSharp;

namespace Iciclecreek.Avalonia.SixelBuffer.Rendering
{
    /// <summary>
    ///     Provides Windows-style cursor bitmaps for software cursor rendering.
    ///     Cursors are drawn with SkiaSharp paths for vector-quality anti-aliased rendering
    ///     at any cell size.
    ///     Users can override cursors by calling <see cref="RegisterCursor"/> or
    ///     <see cref="LoadCursorsFromAssets"/>.
    /// </summary>
    public static class SoftwareCursor
    {
        private static readonly Dictionary<StandardCursorType, SKBitmap> _overrides = new();

        /// <summary>
        ///     Register a custom cursor bitmap for a cursor type.
        ///     The bitmap will be scaled to cell size when rendered.
        /// </summary>
        public static void RegisterCursor(StandardCursorType cursorType, SKBitmap bitmap)
        {
            _overrides[cursorType] = bitmap;
        }

        /// <summary>
        ///     Try to load cursor overrides from an Avalonia asset URI base path.
        ///     Looks for {basePath}/{CursorName}.png for each StandardCursorType.
        /// </summary>
        public static void LoadCursorsFromAssets(string avaresBasePath)
        {
            foreach (StandardCursorType type in Enum.GetValues<StandardCursorType>())
            {
                if (type == StandardCursorType.None) continue;
                string uri = $"{avaresBasePath}/{type}.png";
                try
                {
                    var assetUri = new Uri(uri);
                    var assets = AvaloniaLocator.Current.GetService<IAssetLoader>();
                    if (assets == null) continue;
                    using var stream = assets.Open(assetUri);
                    var bitmap = SKBitmap.Decode(stream);
                    if (bitmap != null)
                        _overrides[type] = bitmap;
                }
                catch (Exception ex) when (ex is System.IO.FileNotFoundException or InvalidOperationException or ArgumentException)
                {
                    // Asset not found — use built-in
                }
            }
        }

        /// <summary>
        ///     Get an SKBitmap for the given cursor type, scaled to cell size.
        /// </summary>
        /// <summary>Cursor width in cells.</summary>
        internal const int CursorCellWidth = 2;

        internal static SKBitmap GetCursorBitmap(StandardCursorType cursorType, int cellW, int cellH)
        {
            int w = cellW * CursorCellWidth;
            int h = cellH;

            if (_overrides.TryGetValue(cursorType, out var custom))
            {
                if (custom.Width == w && custom.Height == h)
                    return custom.Copy();

                var scaled = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
                using var canvas = new SKCanvas(scaled);
                canvas.DrawBitmap(custom,
                    new SKRect(0, 0, custom.Width, custom.Height),
                    new SKRect(0, 0, w, h),
                    new SKPaint { FilterQuality = SKFilterQuality.High });
                return scaled;
            }

            return DrawCursor(cursorType, w, h);
        }

        // Common paints for Windows-style cursors
        private static SKPaint WhiteFill => new()
        {
            Color = SKColors.White,
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };

        private static SKPaint BlackStroke(float width) => new()
        {
            Color = SKColors.Black,
            Style = SKPaintStyle.Stroke,
            StrokeWidth = width,
            IsAntialias = true,
            StrokeCap = SKStrokeCap.Round,
            StrokeJoin = SKStrokeJoin.Round
        };

        private static SKPaint BlackFill => new()
        {
            Color = SKColors.Black,
            Style = SKPaintStyle.Fill,
            IsAntialias = true
        };

        private static SKBitmap DrawCursor(StandardCursorType type, int w, int h)
        {
            var bitmap = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.Transparent);

            // Scale factor relative to a 16x32 reference cursor
            float sx = w / 16f;
            float sy = h / 32f;
            float s = Math.Min(sx, sy);
            float stroke = Math.Max(1f, s * 1.2f);

            switch (type)
            {
                case StandardCursorType.Arrow:
                default:
                    DrawArrow(canvas, s, stroke);
                    break;
                case StandardCursorType.Ibeam:
                    DrawIbeam(canvas, w, h, s, stroke);
                    break;
                case StandardCursorType.Hand:
                    DrawHand(canvas, s, stroke);
                    break;
                case StandardCursorType.Cross:
                    DrawCross(canvas, w, h, s, stroke);
                    break;
                case StandardCursorType.SizeWestEast:
                    DrawSizeWE(canvas, w, h, s, stroke);
                    break;
                case StandardCursorType.SizeNorthSouth:
                    DrawSizeNS(canvas, w, h, s, stroke);
                    break;
                case StandardCursorType.SizeAll:
                    DrawSizeAll(canvas, w, h, s, stroke);
                    break;
                case StandardCursorType.No:
                    DrawNo(canvas, w, h, s, stroke);
                    break;
                case StandardCursorType.Wait:
                case StandardCursorType.AppStarting:
                    DrawWait(canvas, w, h, s, stroke);
                    break;
                case StandardCursorType.UpArrow:
                    DrawUpArrow(canvas, w, h, s, stroke);
                    break;
                case StandardCursorType.Help:
                    DrawHelp(canvas, w, h, s, stroke);
                    break;
                case StandardCursorType.TopSide:
                    DrawSizeNS(canvas, w, h, s, stroke);
                    break;
                case StandardCursorType.BottomSide:
                    DrawSizeNS(canvas, w, h, s, stroke);
                    break;
                case StandardCursorType.LeftSide:
                    DrawSizeWE(canvas, w, h, s, stroke);
                    break;
                case StandardCursorType.RightSide:
                    DrawSizeWE(canvas, w, h, s, stroke);
                    break;
                case StandardCursorType.TopRightCorner:
                case StandardCursorType.BottomLeftCorner:
                    DrawSizeNESW(canvas, w, h, s, stroke);
                    break;
                case StandardCursorType.TopLeftCorner:
                case StandardCursorType.BottomRightCorner:
                    DrawSizeNWSE(canvas, w, h, s, stroke);
                    break;
                case StandardCursorType.DragMove:
                    DrawSizeAll(canvas, w, h, s, stroke);
                    break;
                case StandardCursorType.DragCopy:
                    DrawDragCopy(canvas, s, stroke);
                    break;
                case StandardCursorType.DragLink:
                    DrawDragLink(canvas, s, stroke);
                    break;
                case StandardCursorType.None:
                    // Invisible cursor — leave transparent
                    break;
            }

            return bitmap;
        }

        /// <summary>Draw a path with white fill and black outline.</summary>
        private static void DrawOutlinedPath(SKCanvas canvas, SKPath path, float stroke)
        {
            canvas.DrawPath(path, WhiteFill);
            canvas.DrawPath(path, BlackStroke(stroke));
        }

        private static void DrawArrow(SKCanvas canvas, float s, float stroke)
        {
            // Classic pointer: tall narrow triangle with angled stem
            using var path = new SKPath();
            path.MoveTo(1 * s, 1 * s);       // tip
            path.LineTo(1 * s, 18 * s);      // left edge down
            path.LineTo(4 * s, 15 * s);      // notch in
            path.LineTo(7 * s, 21 * s);      // stem bottom-right
            path.LineTo(9 * s, 19.5f * s);   // stem right edge
            path.LineTo(6 * s, 14 * s);      // stem top
            path.LineTo(10 * s, 14 * s);     // right wing
            path.Close();

            DrawOutlinedPath(canvas, path, stroke);
        }

        private static void DrawIbeam(SKCanvas canvas, int w, int h, float s, float stroke)
        {
            // Text cursor: thin vertical bar with top/bottom serifs, left-aligned
            float cx = 4 * s;
            float serifW = 4 * s;
            float barW = 0.6f * s;
            float top = 1.5f * s;
            float bottom = h - 1.5f * s;

            using var path = new SKPath();
            path.MoveTo(cx - serifW / 2, top);
            path.LineTo(cx + serifW / 2, top);
            path.LineTo(cx + serifW / 2, top + 1.5f * s);
            path.LineTo(cx + barW, top + 1.5f * s);
            path.LineTo(cx + barW, bottom - 1.5f * s);
            path.LineTo(cx + serifW / 2, bottom - 1.5f * s);
            path.LineTo(cx + serifW / 2, bottom);
            path.LineTo(cx - serifW / 2, bottom);
            path.LineTo(cx - serifW / 2, bottom - 1.5f * s);
            path.LineTo(cx - barW, bottom - 1.5f * s);
            path.LineTo(cx - barW, top + 1.5f * s);
            path.LineTo(cx - serifW / 2, top + 1.5f * s);
            path.Close();

            DrawOutlinedPath(canvas, path, stroke);
        }

        private static void DrawHand(SKCanvas canvas, float s, float stroke)
        {
            // Pointing hand: index finger up, other fingers curled, palm below
            using var path = new SKPath();
            // Index finger
            path.MoveTo(4 * s, 1 * s);
            path.LineTo(6 * s, 1 * s);
            path.QuadTo(7 * s, 1 * s, 7 * s, 2 * s);
            path.LineTo(7 * s, 10 * s);
            // Middle finger
            path.LineTo(8.5f * s, 8.5f * s);
            path.QuadTo(9.5f * s, 8 * s, 10 * s, 9 * s);
            path.LineTo(10 * s, 11 * s);
            // Ring finger
            path.LineTo(11 * s, 9.5f * s);
            path.QuadTo(12 * s, 9 * s, 12.5f * s, 10 * s);
            path.LineTo(12.5f * s, 12 * s);
            // Pinky
            path.LineTo(13 * s, 11 * s);
            path.QuadTo(14 * s, 10.5f * s, 14.5f * s, 11.5f * s);
            path.LineTo(14.5f * s, 17 * s);
            // Palm bottom
            path.QuadTo(14.5f * s, 21 * s, 11 * s, 21 * s);
            path.LineTo(5 * s, 21 * s);
            path.QuadTo(1.5f * s, 21 * s, 1.5f * s, 17 * s);
            path.LineTo(1.5f * s, 13 * s);
            // Thumb area
            path.QuadTo(1.5f * s, 11 * s, 3 * s, 11 * s);
            path.LineTo(4 * s, 11 * s);
            path.Close();

            DrawOutlinedPath(canvas, path, stroke);
        }

        private static void DrawCross(SKCanvas canvas, int w, int h, float s, float stroke)
        {
            // Precision crosshair: thin cross with gap in center
            float cx = 7 * s;
            float cy = 10 * s;
            float arm = 6 * s;
            float gap = 1.5f * s;
            float t = 0.6f * s;

            using var path = new SKPath();
            // Top arm
            path.AddRect(new SKRect(cx - t, cy - arm, cx + t, cy - gap));
            // Bottom arm
            path.AddRect(new SKRect(cx - t, cy + gap, cx + t, cy + arm));
            // Left arm
            path.AddRect(new SKRect(cx - arm, cy - t, cx - gap, cy + t));
            // Right arm
            path.AddRect(new SKRect(cx + gap, cy - t, cx + arm, cy + t));

            DrawOutlinedPath(canvas, path, stroke);
        }

        private static void DrawSizeWE(SKCanvas canvas, int w, int h, float s, float stroke)
        {
            // Horizontal double-arrow centered vertically
            float cy = 10 * s;
            float left = 1.5f * s;
            float right = w - 1.5f * s;
            float ah = 4 * s;  // arrowhead height
            float shaft = 1.2f * s;

            using var path = new SKPath();
            path.MoveTo(left, cy);
            path.LineTo(left + ah, cy - ah);
            path.LineTo(left + ah, cy - shaft);
            path.LineTo(right - ah, cy - shaft);
            path.LineTo(right - ah, cy - ah);
            path.LineTo(right, cy);
            path.LineTo(right - ah, cy + ah);
            path.LineTo(right - ah, cy + shaft);
            path.LineTo(left + ah, cy + shaft);
            path.LineTo(left + ah, cy + ah);
            path.Close();

            DrawOutlinedPath(canvas, path, stroke);
        }

        private static void DrawSizeNS(SKCanvas canvas, int w, int h, float s, float stroke)
        {
            // Vertical double-arrow centered horizontally
            float cx = 7 * s;
            float top = 1.5f * s;
            float bottom = h - 1.5f * s;
            float aw = 4 * s;
            float shaft = 1.2f * s;

            using var path = new SKPath();
            path.MoveTo(cx, top);
            path.LineTo(cx + aw, top + aw);
            path.LineTo(cx + shaft, top + aw);
            path.LineTo(cx + shaft, bottom - aw);
            path.LineTo(cx + aw, bottom - aw);
            path.LineTo(cx, bottom);
            path.LineTo(cx - aw, bottom - aw);
            path.LineTo(cx - shaft, bottom - aw);
            path.LineTo(cx - shaft, top + aw);
            path.LineTo(cx - aw, top + aw);
            path.Close();

            DrawOutlinedPath(canvas, path, stroke);
        }

        private static void DrawSizeAll(SKCanvas canvas, int w, int h, float s, float stroke)
        {
            // Four-way arrow, centered, fills available space
            float cx = 7 * s;
            float cy = 10 * s;
            float arm = 9 * s;
            float aw = 4 * s;
            float shaft = 1.2f * s;

            using var path = new SKPath();
            // Up arrow
            path.MoveTo(cx, cy - arm);
            path.LineTo(cx + aw, cy - arm + aw);
            path.LineTo(cx + shaft, cy - arm + aw);
            path.LineTo(cx + shaft, cy - shaft);
            // Right arrow
            path.LineTo(cx + arm - aw, cy - shaft);
            path.LineTo(cx + arm - aw, cy - aw);
            path.LineTo(cx + arm, cy);
            path.LineTo(cx + arm - aw, cy + aw);
            path.LineTo(cx + arm - aw, cy + shaft);
            path.LineTo(cx + shaft, cy + shaft);
            // Down arrow
            path.LineTo(cx + shaft, cy + arm - aw);
            path.LineTo(cx + aw, cy + arm - aw);
            path.LineTo(cx, cy + arm);
            path.LineTo(cx - aw, cy + arm - aw);
            path.LineTo(cx - shaft, cy + arm - aw);
            path.LineTo(cx - shaft, cy + shaft);
            // Left arrow
            path.LineTo(cx - arm + aw, cy + shaft);
            path.LineTo(cx - arm + aw, cy + aw);
            path.LineTo(cx - arm, cy);
            path.LineTo(cx - arm + aw, cy - aw);
            path.LineTo(cx - arm + aw, cy - shaft);
            path.LineTo(cx - shaft, cy - shaft);
            path.LineTo(cx - shaft, cy - arm + aw);
            path.LineTo(cx - aw, cy - arm + aw);
            path.Close();

            DrawOutlinedPath(canvas, path, stroke);
        }

        private static void DrawNo(SKCanvas canvas, int w, int h, float s, float stroke)
        {
            // Circle-slash prohibition sign, fills available space
            float cx = 7 * s;
            float cy = 10 * s;
            float r = 9 * s;
            float t = 2 * s;

            // Outer ring as path (thick ring, not filled circle)
            using var ring = new SKPath();
            ring.AddCircle(cx, cy, r);
            ring.AddCircle(cx, cy, r - t);
            ring.FillType = SKPathFillType.EvenOdd;
            DrawOutlinedPath(canvas, ring, stroke);

            // Diagonal bar
            float dx = r * 0.65f;
            using var bar = new SKPath();
            float bt = t * 0.7f;
            bar.MoveTo(cx - dx - bt * 0.3f, cy + dx - bt * 0.3f);
            bar.LineTo(cx - dx + bt * 0.3f, cy + dx + bt * 0.3f);
            bar.LineTo(cx + dx + bt * 0.3f, cy - dx + bt * 0.3f);
            bar.LineTo(cx + dx - bt * 0.3f, cy - dx - bt * 0.3f);
            bar.Close();
            DrawOutlinedPath(canvas, bar, stroke);
        }

        private static void DrawWait(SKCanvas canvas, int w, int h, float s, float stroke)
        {
            // Hourglass
            float cx = 7 * s;
            float top = 2 * s;
            float bottom = h - 2 * s;
            float hw = 5 * s;
            float mid = (top + bottom) / 2;
            float neck = 1.5f * s;

            using var path = new SKPath();
            // Top half
            path.MoveTo(cx - hw, top);
            path.LineTo(cx + hw, top);
            path.LineTo(cx + hw, top + 1.5f * s);
            path.LineTo(cx + neck, mid);
            // Bottom half
            path.LineTo(cx + hw, bottom - 1.5f * s);
            path.LineTo(cx + hw, bottom);
            path.LineTo(cx - hw, bottom);
            path.LineTo(cx - hw, bottom - 1.5f * s);
            path.LineTo(cx - neck, mid);
            path.LineTo(cx - hw, top + 1.5f * s);
            path.Close();

            DrawOutlinedPath(canvas, path, stroke);
        }

        private static void DrawSizeNESW(SKCanvas canvas, int w, int h, float s, float stroke)
        {
            // Diagonal NE-SW double arrow
            float cx = 7 * s, cy = 10 * s;
            float arm = 5.5f * s;
            float aw = 3 * s;
            float shaft = s;
            float dx = arm * 0.707f;  // cos(45)

            using var path = new SKPath();
            // NE arrowhead
            path.MoveTo(cx + dx, cy - dx);
            path.LineTo(cx + dx, cy - dx + aw);
            path.LineTo(cx + dx - shaft * 0.7f, cy - dx + shaft * 0.7f);
            // Shaft to SW
            path.LineTo(cx - dx + shaft * 0.7f, cy + dx - shaft * 0.7f);
            path.LineTo(cx - dx + aw, cy + dx);
            // SW arrowhead
            path.LineTo(cx - dx, cy + dx);
            path.LineTo(cx - dx, cy + dx - aw);
            path.LineTo(cx - dx + shaft * 0.7f, cy + dx - shaft * 0.7f);
            // Shaft back to NE
            path.LineTo(cx + dx - shaft * 0.7f, cy - dx + shaft * 0.7f);
            path.LineTo(cx + dx - aw, cy - dx);
            path.Close();

            DrawOutlinedPath(canvas, path, stroke);
        }

        private static void DrawSizeNWSE(SKCanvas canvas, int w, int h, float s, float stroke)
        {
            // Diagonal NW-SE double arrow
            float cx = 7 * s, cy = 10 * s;
            float arm = 5.5f * s;
            float aw = 3 * s;
            float shaft = s;
            float dx = arm * 0.707f;

            using var path = new SKPath();
            // NW arrowhead
            path.MoveTo(cx - dx, cy - dx);
            path.LineTo(cx - dx + aw, cy - dx);
            path.LineTo(cx - dx + shaft * 0.7f, cy - dx + shaft * 0.7f);
            // Shaft to SE
            path.LineTo(cx + dx - shaft * 0.7f, cy + dx - shaft * 0.7f);
            path.LineTo(cx + dx, cy + dx - aw);
            // SE arrowhead
            path.LineTo(cx + dx, cy + dx);
            path.LineTo(cx + dx - aw, cy + dx);
            path.LineTo(cx + dx - shaft * 0.7f, cy + dx - shaft * 0.7f);
            // Shaft back to NW
            path.LineTo(cx - dx + shaft * 0.7f, cy - dx + shaft * 0.7f);
            path.LineTo(cx - dx, cy - dx + aw);
            path.Close();

            DrawOutlinedPath(canvas, path, stroke);
        }

        private static void DrawUpArrow(SKCanvas canvas, int w, int h, float s, float stroke)
        {
            // Single upward arrow
            float cx = 7 * s;
            float top = 2 * s;
            float bottom = h - 2 * s;
            float aw = 4 * s;
            float shaft = 1.2f * s;

            using var path = new SKPath();
            path.MoveTo(cx, top);
            path.LineTo(cx + aw, top + aw);
            path.LineTo(cx + shaft, top + aw);
            path.LineTo(cx + shaft, bottom);
            path.LineTo(cx - shaft, bottom);
            path.LineTo(cx - shaft, top + aw);
            path.LineTo(cx - aw, top + aw);
            path.Close();

            DrawOutlinedPath(canvas, path, stroke);
        }

        private static void DrawHelp(SKCanvas canvas, int w, int h, float s, float stroke)
        {
            // Arrow with question mark bubble
            DrawArrow(canvas, s * 0.65f, stroke);

            float bx = 12 * s;
            float by = 15 * s;
            float br = 5 * s;

            using var bubble = new SKPath();
            bubble.AddCircle(bx, by, br);
            DrawOutlinedPath(canvas, bubble, stroke);

            using var font = new SKPaint
            {
                Color = SKColors.Black,
                TextSize = 7 * s,
                IsAntialias = true,
                Typeface = SKTypeface.Default,
                TextAlign = SKTextAlign.Center,
                FakeBoldText = true
            };
            canvas.DrawText("?", bx, by + 2.5f * s, font);
        }

        private static void DrawDragCopy(SKCanvas canvas, float s, float stroke)
        {
            DrawArrow(canvas, s * 0.65f, stroke);

            // Plus badge
            float bx = 11 * s;
            float by = 16 * s;
            float sz = 3 * s;

            using var badge = new SKPath();
            badge.AddRoundRect(new SKRect(bx - sz, by - sz, bx + sz, by + sz), s, s);
            DrawOutlinedPath(canvas, badge, stroke);

            float t = 0.6f * s;
            using var plus = new SKPath();
            plus.AddRect(new SKRect(bx - sz * 0.6f, by - t, bx + sz * 0.6f, by + t));
            plus.AddRect(new SKRect(bx - t, by - sz * 0.6f, bx + t, by + sz * 0.6f));
            canvas.DrawPath(plus, BlackFill);
        }

        private static void DrawDragLink(SKCanvas canvas, float s, float stroke)
        {
            DrawArrow(canvas, s * 0.65f, stroke);

            // Curved arrow badge
            float bx = 11 * s;
            float by = 16 * s;
            float sz = 3 * s;

            using var badge = new SKPath();
            badge.AddRoundRect(new SKRect(bx - sz, by - sz, bx + sz, by + sz), s, s);
            DrawOutlinedPath(canvas, badge, stroke);

            // Small arrow inside badge
            using var arrow = new SKPath();
            arrow.MoveTo(bx - sz * 0.4f, by + sz * 0.2f);
            arrow.LineTo(bx + sz * 0.4f, by - sz * 0.4f);
            arrow.LineTo(bx + sz * 0.4f, by + sz * 0.1f);
            canvas.DrawPath(arrow, BlackStroke(stroke));
        }
    }
}
