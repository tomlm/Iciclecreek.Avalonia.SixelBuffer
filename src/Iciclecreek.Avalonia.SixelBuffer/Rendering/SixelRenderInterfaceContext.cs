using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Platform;
using Avalonia.Platform.Surfaces;
using Iciclecreek.Avalonia.SixelBuffer.Platform;
using Iciclecreek.Avalonia.SixelBuffer.Terminal;

namespace Iciclecreek.Avalonia.SixelBuffer.Rendering
{
    internal sealed class SixelRenderInterfaceContext : IPlatformRenderInterfaceContext
    {
        private readonly IPlatformRenderInterface _fallbackRenderInterface;
        private readonly IPlatformRenderInterfaceContext _fallbackContext;

        public SixelRenderInterfaceContext(IPlatformRenderInterface fallbackRenderInterface)
        {
            _fallbackRenderInterface = fallbackRenderInterface;
            _fallbackContext = fallbackRenderInterface.CreateBackendContext(null);
        }

        public IRenderTarget CreateRenderTarget(IEnumerable<IPlatformRenderSurface> surfaces)
        {
            var consoleWindow = surfaces.OfType<TerminalWindow>().Single();
            var terminal = AvaloniaLocator.Current.GetRequiredService<ITerminal>();

            var pixelSize = new PixelSize(
                (int)consoleWindow.ClientSize.Width,
                (int)consoleWindow.ClientSize.Height);
            IDrawingContextLayerImpl offscreenTarget = _fallbackContext.CreateOffscreenRenderTarget(pixelSize, new Vector(96, 96), true);

            return new SixelRenderTarget(
                consoleWindow, offscreenTarget, terminal,
                _fallbackRenderInterface, _fallbackContext,
                terminal.CellPixelWidth, terminal.CellPixelHeight);
        }

        public IDrawingContextLayerImpl CreateOffscreenRenderTarget(PixelSize pixelSize, Vector dpi, bool enableTextAntialiasing)
            => _fallbackContext.CreateOffscreenRenderTarget(pixelSize, dpi, enableTextAntialiasing);

        public PixelSize? MaxOffscreenRenderTargetPixelSize => _fallbackContext.MaxOffscreenRenderTargetPixelSize;

        public object? TryGetFeature(Type featureType) => _fallbackContext.TryGetFeature(featureType);
        public bool IsLost => _fallbackContext.IsLost;
        public IReadOnlyDictionary<Type, object> PublicFeatures => _fallbackContext.PublicFeatures;
        public void Dispose() => _fallbackContext.Dispose();
    }
}
