using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Platform;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Platform;
using Avalonia.Platform.Surfaces;
using Avalonia.Rendering.Composition;
using Iciclecreek.Avalonia.WindowManager;
using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Rendering;
using Avalonia.VisualTree;
using Avalonia.Threading;
using System.Diagnostics;

namespace AvaloniaTerminalBuffer.Platform
{
    public class TerminalManagedWindow : ManagedWindow, IWindowImpl
    {
        private IInputRoot _inputRoot;
        private IWindowImpl _mainWindow;
        private IWindowImpl _parentWindow;
        private IPresentationSource? _presentationSource;
        private Size _clientSize;
        private bool _disposing;

        public TerminalManagedWindow(IWindowImpl mainWindow)
        {
            base.Content = new Panel();

            _mainWindow = mainWindow;

            // ManagedWindow events → IWindowImpl callbacks
            base.Closed += (_, _) => ((ITopLevelImpl)this).Closed?.Invoke();
            base.Activated += (_, _) => ((IWindowBaseImpl)this).Activated?.Invoke();
            base.Deactivated += (_, _) => ((IWindowBaseImpl)this).Deactivated?.Invoke();
            base.PositionChanged += (_, e) => ((IWindowBaseImpl)this).PositionChanged?.Invoke(e.Point);
            base.Resized += (_, e) => ((ITopLevelImpl)this).Resized?.Invoke(e.ClientSize, e.Reason);
            base.Closing += (_, e) =>
            {
                // Don't re-enter Avalonia's close path when we're already disposing.
                if (_disposing)
                    return;

                // Invoke Avalonia Window's closing handler; it returns false to cancel
                var closing = ((IWindowImpl)this).Closing;
                if (closing != null && !closing.Invoke(e.CloseReason))
                    e.Cancel = true;
            };

            // Propagate terminal resize → file picker window re-layout
            _mainWindow.Resized += (size, reason) => ((ITopLevelImpl)this).Resized?.Invoke(size, reason);
        }

        // --- ITopLevelImpl properties ---
        // Track size ourselves — base.ClientSize may throw before template is applied.
        public new Size ClientSize => _clientSize;
        public Size? FrameSize => _clientSize;
        public double RenderScaling => 1;
        public IPlatformRenderSurface[] Surfaces => _mainWindow.Surfaces;
        public Action<RawInputEventArgs>? Input { get; set; }
        public Action<Rect>? Paint { get; set; }
        public Action<double>? ScalingChanged { get; set; }
        public Action<WindowTransparencyLevel>? TransparencyLevelChanged { get; set; }
        public Compositor Compositor => _mainWindow.Compositor;
        public WindowTransparencyLevel TransparencyLevel => WindowTransparencyLevel.None;
        public AcrylicPlatformCompensationLevels AcrylicCompensationLevels => new(1, 1, 1);

        // --- IWindowBaseImpl properties ---
        public double DesktopScaling => 1d;
        public IPlatformHandle? Handle => _mainWindow.Handle;
        public Size MaxAutoSizeHint => _mainWindow.MaxAutoSizeHint;
        Action<PixelPoint>? IWindowBaseImpl.PositionChanged { get; set; }
        Action? IWindowBaseImpl.Deactivated { get; set; }
        Action? IWindowBaseImpl.Activated { get; set; }

        // --- IWindowImpl properties ---
        public bool WindowStateGetterIsUsable => false;
        public Action<WindowState>? WindowStateChanged { get; set; }
        public Action? GotInputWhenDisabled { get; set; }
        public bool IsClientAreaExtendedToDecorations => false;
        public Action<bool>? ExtendClientAreaToDecorationsChanged { get; set; }
        public bool NeedsManagedDecorations => false;
        public PlatformRequestedDrawnDecoration RequestedDrawnDecorations => default;
        public Thickness ExtendedMargins => default;
        public Thickness OffScreenMargin => default;
        Func<WindowCloseReason, bool>? IWindowImpl.Closing { get; set; }

        // --- ITopLevelImpl callbacks ---
        Action<Size, WindowResizeReason>? ITopLevelImpl.Resized { get; set; }
        Action? ITopLevelImpl.Closed { get; set; }
        Action? ITopLevelImpl.LostFocus { get; set; }

        // --- ITopLevelImpl methods ---
        public void SetInputRoot(IInputRoot inputRoot)
        {
            _inputRoot = inputRoot;

            // In Avalonia 12, inputRoot is a PresentationSource whose RootVisual (the Window)
            // is not yet assigned when SetInputRoot is called during the constructor.
            // Store the reference; content will be moved in Show() before layout runs.
            if (inputRoot is IPresentationSource ps)
                _presentationSource = ps;
        }

        /// <summary>
        /// Finds the Avalonia Window from the stored PresentationSource and moves its
        /// content into this ManagedWindow. Must be called before base.Show/ShowDialog
        /// so the content is in our tree before any layout pass fires.
        /// </summary>
        private void AdoptContentFromSource()
        {
            if (_presentationSource == null)
                return;

            // In Avalonia 12, RootVisual is a TopLevelHost that wraps the Window
            // as its first visual child.
            var root = _presentationSource.RootVisual;
            var win = root as Window
                      ?? root?.GetVisualChildren().OfType<Window>().FirstOrDefault();
            if (win != null)
            {
                this[!TitleProperty] = win[!Window.TitleProperty];
                this[!WindowStartupLocationProperty] = win[!Window.WindowStartupLocationProperty];
                // Move content from the Window to this ManagedWindow.
                // Detach first since a control can only have one parent.
                var content = win.Content;
                win.Content = null;
                this.DataContext = win.DataContext;
                this.Content = content;
            }

            _presentationSource = null;
        }
        public Point PointToClient(PixelPoint point) => point.ToPoint(1);
        public PixelPoint PointToScreen(Point point) => new((int)point.X, (int)point.Y);
        public void SetCursor(ICursorImpl? cursor) { }
        public IPopupImpl? CreatePopup() => null;
        public void SetTransparencyLevelHint(IReadOnlyList<WindowTransparencyLevel> transparencyLevels) { }
        public void SetFrameThemeVariant(PlatformThemeVariant themeVariant) { }

        public object? TryGetFeature(Type featureType)
        {
            if (featureType == typeof(IScreenImpl))
                return new Iciclecreek.Avalonia.TerminalFramebuffer.Platform.TerminalScreen();
            Debug.WriteLine($"Missing Feature: {featureType.Name}");
            return null;
        }

        // --- IWindowBaseImpl methods ---
        public void Show(bool activate, bool isDialog)
        {
            // Move content before Show so it's in our tree before any layout pass.
            AdoptContentFromSource();

            this.ShowActivated = activate;
            if (isDialog)
                base.ShowDialog();
            else
                base.Show();
        }

        public void Hide()
        {
            // Close the ManagedWindow so the dialog completes and returns its result.
            base.Close();
        }

        //public new void Activate() => base.Activate();

        public void Move(PixelPoint point) => Position = point;

        public void Resize(Size clientSize, WindowResizeReason reason = WindowResizeReason.Application)
        {
            _clientSize = clientSize;
            base.ClientSize = clientSize;
        }

        // --- IWindowImpl methods (delegate to base ManagedWindow where applicable) ---
        public void SetTitle(string? title) => Title = title ?? string.Empty;
        public void SetTopmost(bool value) => Topmost = value;
        public void SetIcon(IWindowIconImpl? icon) { }
        public void SetWindowDecorations(WindowDecorations enabled) { }
        public void SetParent(IWindowImpl? parent)
        {
            _parentWindow = parent;
        }
        public void SetEnabled(bool enable)
        {
            base.IsEnabled = enable;
        }

        public void SetMinMaxSize(Size minSize, Size maxSize)
        {
            base.MaxHeight = maxSize.Height;
            base.MaxWidth = maxSize.Width;
            base.MinHeight = minSize.Height;
            base.MinWidth = minSize.Width;
        }

        public void SetExtendClientAreaToDecorationsHint(bool extendIntoClientAreaHint) { }
        public void SetExtendClientAreaTitleBarHeightHint(double titleBarHeight) { }
        public void SetCanMinimize(bool value)
        {
        }
        public void SetCanMaximize(bool value)
        {
        }
        public void BeginMoveDrag(PointerPressedEventArgs e)
        {
        }
        public void BeginResizeDrag(WindowEdge edge, PointerPressedEventArgs e)
        {
        }
        public void ShowTaskbarIcon(bool value) { }
        void IWindowImpl.CanResize(bool value) => CanResize = value;

        public void Dispose()
        {
            if (_disposing)
                return;
            _disposing = true;

            // Close the ManagedWindow (removes from WindowsPanel).
            // The base.Closed event handler invokes ITopLevelImpl.Closed,
            // which tells Avalonia's TopLevel/Window we're closed and completes the dialog TCS.
            base.Close();
        }
    }
}
