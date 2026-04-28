using System;
using System.IO;
using Avalonia.Media;
using Avalonia.Platform;

namespace Avalonia.Terminal.Rendering
{
    internal class SixelDrawingContext : IDrawingContextImpl
    {
        internal readonly IDrawingContextImpl Inner;
        private readonly SixelRenderTarget _renderTarget;

        public SixelDrawingContext(IDrawingContextImpl inner, SixelRenderTarget renderTarget = null)
        {
            Inner = inner;
            _renderTarget = renderTarget;
        }

        public Matrix Transform
        {
            get => Inner.Transform;
            set => Inner.Transform = value;
        }

        public void Clear(Color color) => Inner.Clear(color);
        public void DrawBitmap(IBitmapImpl source, double opacity, Rect sourceRect, Rect destRect) => Inner.DrawBitmap(source, opacity, sourceRect, destRect);
        public void DrawBitmap(IBitmapImpl source, IBrush opacityMask, Rect opacityMaskRect, Rect destRect) => Inner.DrawBitmap(source, opacityMask, opacityMaskRect, destRect);
        public void DrawEllipse(IBrush brush, IPen pen, Rect rect) => Inner.DrawEllipse(brush, pen, rect);
        public void DrawGlyphRun(IBrush foreground, IGlyphRunImpl glyphRun) => Inner.DrawGlyphRun(foreground, glyphRun);
        public void DrawLine(IPen pen, Point p1, Point p2) => Inner.DrawLine(pen, p1, p2);
        public void DrawGeometry(IBrush brush, IPen pen, IGeometryImpl geometry)
        {
            try { Inner.DrawGeometry(brush, pen, geometry); }
            catch (InvalidOperationException) { /* Skia can fail on degenerate geometry/brush sizes */ }
        }

        public void DrawRectangle(IBrush brush, IPen pen, RoundedRect rrect, BoxShadows boxShadows = default)
        {
            try { Inner.DrawRectangle(brush, pen, rrect, boxShadows); }
            catch (InvalidOperationException) { /* Skia can fail on degenerate sizes */ }
        }
        public void DrawRegion(IBrush brush, IPen pen, IPlatformRenderInterfaceRegion region) => Inner.DrawRegion(brush, pen, region);

        public IDrawingContextLayerImpl CreateLayer(PixelSize size)
            => new SixelLayerWrapper(Inner.CreateLayer(size));

        public void PushClip(Rect clip) => Inner.PushClip(clip);
        public void PushClip(RoundedRect clip) => Inner.PushClip(clip);
        public void PushClip(IPlatformRenderInterfaceRegion region) => Inner.PushClip(region);
        public void PopClip() => Inner.PopClip();
        public void PushOpacity(double opacity, Rect? bounds) => Inner.PushOpacity(opacity, bounds);
        public void PopOpacity() => Inner.PopOpacity();
        public void PushOpacityMask(IBrush mask, Rect bounds) => Inner.PushOpacityMask(mask, bounds);
        public void PopOpacityMask() => Inner.PopOpacityMask();
        public void PushGeometryClip(IGeometryImpl clip) => Inner.PushGeometryClip(clip);
        public void PopGeometryClip() => Inner.PopGeometryClip();
        public void PushRenderOptions(RenderOptions renderOptions) => Inner.PushRenderOptions(renderOptions);
        public void PopRenderOptions() => Inner.PopRenderOptions();
        public void PushTextOptions(TextOptions textOptions) => Inner.PushTextOptions(textOptions);
        public void PopTextOptions() => Inner.PopTextOptions();
        public void PushLayer(Rect bounds) => Inner.PushLayer(bounds);
        public void PopLayer() => Inner.PopLayer();
        public object GetFeature(Type t) => Inner.GetFeature(t);

        public void Dispose()
        {
            Inner.Dispose();
            _renderTarget?.RenderToDevice();
        }
    }

    internal class SixelLayerWrapper : IDrawingContextLayerImpl
    {
        private readonly IDrawingContextLayerImpl _inner;

        public SixelLayerWrapper(IDrawingContextLayerImpl inner) => _inner = inner;

        public void Blit(IDrawingContextImpl context)
        {
            IDrawingContextImpl actual = context is SixelDrawingContext sixel ? sixel.Inner : context;
            _inner.Blit(actual);
        }

        public bool CanBlit => _inner.CanBlit;
        public IDrawingContextImpl CreateDrawingContext() => _inner.CreateDrawingContext();
        public bool IsCorrupted => _inner.IsCorrupted;
        public Vector Dpi => ((IBitmapImpl)_inner).Dpi;
        public PixelSize PixelSize => ((IBitmapImpl)_inner).PixelSize;
        public int Version => ((IBitmapImpl)_inner).Version;
        public void Save(string fileName, int? quality = null) => ((IBitmapImpl)_inner).Save(fileName, quality);
        public void Save(Stream stream, int? quality = null) => ((IBitmapImpl)_inner).Save(stream, quality);
        public void Dispose() => _inner.Dispose();
    }
}
