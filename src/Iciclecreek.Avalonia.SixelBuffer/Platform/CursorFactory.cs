using System;
using System.Collections.Generic;
using System.IO;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;

namespace Iciclecreek.Avalonia.SixelBuffer.Platform
{
    internal sealed class CursorFactory : ICursorFactory
    {
        private readonly Dictionary<StandardCursorType, CursorImpl> _cursors = new();

        public ICursorImpl GetCursor(StandardCursorType cursorType)
        {
            lock (_cursors)
            {
                if (_cursors.TryGetValue(cursorType, out CursorImpl cursor))
                    return cursor;
                cursor = new CursorImpl(cursorType);
                _cursors.Add(cursorType, cursor);
                return cursor;
            }
        }

        public ICursorImpl CreateCursor(Bitmap cursor, PixelPoint hotSpot)
        {
            // Convert Avalonia Bitmap to SKBitmap and register as a custom cursor
            using var stream = new MemoryStream();
            cursor.Save(stream);
            stream.Position = 0;
            var skBitmap = SKBitmap.Decode(stream);

            if (skBitmap != null)
            {
                var impl = new BitmapCursorImpl(skBitmap, hotSpot);
                return impl;
            }

            // Fallback to arrow
            return GetCursor(StandardCursorType.Arrow);
        }
    }

    internal sealed class CursorImpl : ICursorImpl
    {
        public CursorImpl(StandardCursorType cursorType) => CursorType = cursorType;
        public StandardCursorType CursorType { get; }
        public void Dispose() { }
    }

    internal sealed class BitmapCursorImpl : ICursorImpl
    {
        public SKBitmap Bitmap { get; }
        public PixelPoint HotSpot { get; }

        public BitmapCursorImpl(SKBitmap bitmap, PixelPoint hotSpot)
        {
            Bitmap = bitmap;
            HotSpot = hotSpot;
        }

        public void Dispose()
        {
            Bitmap.Dispose();
        }
    }
}
