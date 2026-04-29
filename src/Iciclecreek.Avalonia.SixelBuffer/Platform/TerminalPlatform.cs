using System;
using Avalonia;
using Avalonia.Controls.Platform;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform;
using Avalonia.Rendering;
using Avalonia.Threading;
using AvaloniaTerminalBuffer.Platform;

namespace Iciclecreek.Avalonia.SixelBuffer.Platform
{
    internal class TerminalPlatform : IWindowingPlatform
    {
        private IWindowImpl _mainWindow;

        public IWindowImpl CreateWindow()
        {
            if (_mainWindow != null)
                return new TerminalManagedWindow(_mainWindow);
            else
                _mainWindow = new TerminalWindow();
            return _mainWindow;
        }

        public IWindowImpl CreateEmbeddableWindow()
            => throw new NotSupportedException("Create Embeddable Window not supported in terminal mode.");

        public ITrayIconImpl CreateTrayIcon()
            => throw new NotSupportedException("Tray icons are not supported in terminal mode.");

        public ITopLevelImpl CreateEmbeddableTopLevel()
            => throw new NotSupportedException("Embedded top levels are not supported in terminal mode.");

        public void GetWindowsZOrder(ReadOnlySpan<IWindowImpl> windows, Span<long> zOrder)
        {
            for (int i = 0; i < zOrder.Length; i++) zOrder[i] = 0;
        }

        public void Initialize()
        {
            // 10fps is sufficient for terminal rendering and prevents
            // animation frames from starving input processing on the UI thread
            var renderTimer = new UiThreadRenderTimer(10);
            Dispatcher.InitializeUIThreadDispatcher(new ManagedDispatcherImpl(null));
            AvaloniaLocator.CurrentMutable.BindToSelf(this)
                .Bind<IWindowingPlatform>().ToConstant(this)
                .Bind<IRenderTimer>().ToConstant(renderTimer)
                .Bind<IRenderLoop>().ToConstant(RenderLoop.FromTimer(renderTimer))
                .Bind<PlatformHotkeyConfiguration>().ToConstant(new PlatformHotkeyConfiguration(KeyModifiers.Control))
                .Bind<IKeyboardDevice>().ToConstant(new ConsoleKeyboardDevice())
                .Bind<IMouseDevice>().ToConstant(new MouseDevice())
                .Bind<ICursorFactory>().ToConstant(new CursorFactory())
                .Bind<IPlatformIconLoader>().ToConstant(new DummyIconLoader())
                .Bind<IPlatformSettings>().ToConstant(new PlatformSettings())
                .Bind<IRuntimePlatform>().ToConstant(new StandardRuntimePlatform());
            ;
        }
    }

    internal class ConsoleKeyboardDevice : KeyboardDevice { }

    internal class DummyIconLoader : IPlatformIconLoader
    {
        public IWindowIconImpl LoadIcon(string fileName) => null;
        public IWindowIconImpl LoadIcon(System.IO.Stream stream) => null;
        public IWindowIconImpl LoadIcon(IBitmapImpl bitmap) => null;
    }

    internal class PlatformSettings : DefaultPlatformSettings { }
}
