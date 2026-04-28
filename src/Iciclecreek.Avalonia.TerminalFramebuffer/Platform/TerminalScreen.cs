using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Platform;

namespace Iciclecreek.Avalonia.TerminalFramebuffer.Platform
{
    internal class TerminalScreen : IScreenImpl
    {
        public int ScreenCount => 0;
        public IReadOnlyList<Screen> AllScreens => [];
        public Action Changed { get; set; }

        public Task<bool> RequestScreenDetails() => Task.FromResult(true);
        public Screen ScreenFromPoint(PixelPoint point) => null;
        public Screen ScreenFromRect(PixelRect rect) => null;
        public Screen ScreenFromTopLevel(ITopLevelImpl topLevel) => null;
        public Screen ScreenFromWindow(IWindowBaseImpl window) => null;
    }
}
