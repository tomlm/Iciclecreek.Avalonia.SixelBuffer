using System;
using System.Collections.Generic;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Platform.Surfaces;
using Avalonia.Rendering.Composition;
using Iciclecreek.Avalonia.SixelBuffer.Terminal;

namespace Iciclecreek.Avalonia.SixelBuffer.Platform
{
    public class TerminalWindow : IWindowImpl, IPlatformRenderSurface
    {
        public readonly ITerminal Terminal;
        private readonly IKeyboardDevice _keyboardDevice;
        private readonly IMouseDevice _mouseDevice = null!;
        private IInputRoot _inputRoot = null!;
        private bool _disposed;
        private IStorageProvider? _storageProvider;

        public TerminalWindow()
        {
            _keyboardDevice = AvaloniaLocator.Current.GetRequiredService<IKeyboardDevice>();
            _mouseDevice = AvaloniaLocator.Current.GetService<IMouseDevice>()!;
            Terminal = AvaloniaLocator.Current.GetRequiredService<ITerminal>();

            Terminal.Resized += OnTerminalResized;
            Terminal.KeyEvent += OnKeyEvent;
            Terminal.MouseEvent += OnMouseEvent;
            Terminal.FocusEvent += OnFocusEvent;

            Handle = null!;
        }

        // ClientSize excludes the last row to prevent Sixel scroll
        public Size ClientSize
        {
            get
            {
                return new Size(
                    Terminal.PixelWidth,
                    Terminal.PixelHeight - Terminal.CellPixelHeight);
            }
        }

        public Size? FrameSize => ClientSize;
        public double RenderScaling => 1;
        public IPlatformRenderSurface[] Surfaces => [this];
        bool IPlatformRenderSurface.IsReady => true;

        public Action<RawInputEventArgs>? Input { get; set; }
        public Action<Rect>? Paint { get; set; }
        public Action<Size, WindowResizeReason>? Resized { get; set; }
        public Action<double>? ScalingChanged { get; set; }
        public Action<WindowTransparencyLevel>? TransparencyLevelChanged { get; set; }
        public Compositor Compositor { get; } = new(null);
        public Action? Closed { get; set; }
        public Action? LostFocus { get; set; }
        public WindowTransparencyLevel TransparencyLevel => WindowTransparencyLevel.None;
        public AcrylicPlatformCompensationLevels AcrylicCompensationLevels => new(1, 1, 1);

        public void SetInputRoot(IInputRoot inputRoot) => _inputRoot = inputRoot;
        public Point PointToClient(PixelPoint point) => point.ToPoint(1);
        public PixelPoint PointToScreen(Point point) => new((int)point.X, (int)point.Y);

        // Software cursor state — read by SixelRenderTarget to composite the cursor
        internal StandardCursorType CursorType { get; private set; } = StandardCursorType.Arrow;
        internal BitmapCursorImpl? BitmapCursor { get; private set; }
        internal Point CursorPixelPosition { get; private set; }
        internal bool CursorDirty { get; private set; }

        internal void ClearCursorDirty() => CursorDirty = false;

        internal void InvalidateRender() => Paint?.Invoke(new Rect(ClientSize));

        //EnsureWindowsPanel();

        //// ManagedWindow.Show() requires a WindowsPanel somewhere in the main window's
        //// visual tree. Auto-inject one (as the main window's content wrapper) if absent.
        //private void EnsureWindowsPanel()
        //{
        //    var lifetime = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
        //    var mainWindow = lifetime?.MainWindow;
        //    if (mainWindow == null) return;

        //    if (mainWindow.FindDescendantOfType<WindowsPanel>(true) != null) return;

        //    // WindowsPanel is itself a ContentControl — wrap the existing content inside it
        //    // so the panel acts as a full-size overlay host above the app's normal UI.
        //    var panel = new WindowsPanel { Content = mainWindow.Content };
        //    mainWindow.Content = panel;
        //}

        public void SetCursor(ICursorImpl? cursor)
        {
            if (cursor is BitmapCursorImpl bitmapCursor)
            {
                BitmapCursor = bitmapCursor;
                CursorType = StandardCursorType.Arrow; // fallback type
            }
            else
            {
                BitmapCursor = null;
                CursorType = cursor is CursorImpl ci ? ci.CursorType : StandardCursorType.Arrow;
            }

            CursorDirty = true;
        }
        public IPopupImpl? CreatePopup() => null;
        public void SetTransparencyLevelHint(IReadOnlyList<WindowTransparencyLevel> transparencyLevels) { }
        public void SetFrameThemeVariant(PlatformThemeVariant themeVariant) { }

        public void Show(bool activate, bool isDialog)
        {
            if (activate) Activated?.Invoke();
        }

        public void Hide() { }
        public void Activate() { }
        public void SetTopmost(bool value) { }
        public double DesktopScaling => 1d;
        public PixelPoint Position { get; }
        public Action<PixelPoint>? PositionChanged { get; set; }
        public Action? Deactivated { get; set; }
        public Action? Activated { get; set; }
        public IPlatformHandle Handle { get; }
        public Size MaxAutoSizeHint { get; }

        public void SetTitle(string? title) => Terminal.SetTitle(title ?? string.Empty);
        public void SetParent(IWindowImpl? parent) { }
        public void SetEnabled(bool enable) { }
        public void SetWindowDecorations(WindowDecorations decorations) { }
        public void SetIcon(IWindowIconImpl? icon) { }
        public void ShowTaskbarIcon(bool value) { }
        public void CanResize(bool value) { }
        public void BeginMoveDrag(PointerPressedEventArgs e) { }
        public void BeginResizeDrag(WindowEdge edge, PointerPressedEventArgs e) { }

        public void Resize(Size clientSize, WindowResizeReason reason = WindowResizeReason.Application)
        {
            // Terminal controls the size, not the app
        }

        public void Move(PixelPoint point) { }
        public void SetMinMaxSize(Size minSize, Size maxSize) { }
        public void SetExtendClientAreaToDecorationsHint(bool extendIntoClientAreaHint) { }
        public void SetExtendClientAreaChromeHints(int hints) { }
        public void SetExtendClientAreaTitleBarHeightHint(double titleBarHeight) { }

        public WindowState WindowState { get; set; }
        public bool WindowStateGetterIsUsable => false;
        public Action<WindowState>? WindowStateChanged { get; set; }
        public PlatformRequestedDrawnDecoration RequestedDrawnDecorations => default;
        public Action? GotInputWhenDisabled { get; set; }
        public Func<WindowCloseReason, bool>? Closing { get; set; }
        public bool IsClientAreaExtendedToDecorations { get; }
        public Action<bool>? ExtendClientAreaToDecorationsChanged { get; set; }
        public bool NeedsManagedDecorations { get; }
        public Thickness ExtendedMargins { get; }
        public Thickness OffScreenMargin { get; }

        public object? TryGetFeature(Type featureType)
        {
            if (featureType == typeof(IScreenImpl))
            {
                return new TerminalScreen();
            }

            if (featureType == typeof(IStorageProvider))
            {
                if (_storageProvider == null && _inputRoot is TopLevel topLevel)
                {
                    var type = Type.GetType("Avalonia.Platform.Storage.FileIO.BclStorageProvider, Avalonia.Base");
                    if (type != null)
                        _storageProvider = (IStorageProvider)Activator.CreateInstance(type, topLevel)!;
                }
                return _storageProvider;
            }

            if (featureType == typeof(ILauncher))
            {
                var type = Type.GetType("Avalonia.Platform.Storage.FileIO.BclLauncher, Avalonia.Base");
                if (type != null)
                    return Activator.CreateInstance(type, nonPublic: true)!;
            }

            Debug.WriteLine($"Missing Feature: {featureType.Name}");
            return null;
        }

        public void GetWindowsZOrder(Span<Window> windows, Span<long> zOrder)
        {
            for (int i = 0; i < zOrder.Length; i++) zOrder[i] = 0;
        }

        public void SetCanMinimize(bool value) { }
        public void SetCanMaximize(bool value) { }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                Closed?.Invoke();
                Terminal.Resized -= OnTerminalResized;
                Terminal.KeyEvent -= OnKeyEvent;
                Terminal.MouseEvent -= OnMouseEvent;
                Terminal.FocusEvent -= OnFocusEvent;
                if (Terminal is IDisposable disposable)
                    disposable.Dispose();
            }
        }

        private void OnTerminalResized()
        {
            Resized?.Invoke(ClientSize, WindowResizeReason.Unspecified);
        }

        private RawInputModifiers _activeModifiers;

        private void OnKeyEvent(Key key, char keyChar, RawInputModifiers modifiers, bool down,
            ulong timestamp, bool tryAsTextInput)
        {
            // KeySymbol is required for access key handling (Alt+F → "f")
            string? keySymbol = keyChar > 0 && !char.IsControl(keyChar) ? keyChar.ToString() : null;

            if (down)
            {
                // Synthesize modifier key presses for newly active modifiers
                // (Avalonia's AccessKeyHandler needs standalone Alt KeyDown to activate accelerators)
                PressNewModifiers(modifiers, timestamp);

                var args = new RawKeyEventArgs(_keyboardDevice, timestamp, _inputRoot,
                    RawKeyEventType.KeyDown, key, modifiers, PhysicalKey.None, keySymbol, KeyDeviceType.Keyboard);
                Input?.Invoke(args);
            }
            else
            {
                var args = new RawKeyEventArgs(_keyboardDevice, timestamp, _inputRoot,
                    RawKeyEventType.KeyUp, key, modifiers, PhysicalKey.None, keySymbol, KeyDeviceType.Keyboard);
                Input?.Invoke(args);

                // Send text input on KeyUp (matching Consolonia's pattern):
                // only if tryAsTextInput, key wasn't handled, and it's a printable char
                if (tryAsTextInput &&
                    !args.Handled &&
                    !char.IsControl(keyChar) &&
                    !modifiers.HasFlag(RawInputModifiers.Alt) &&
                    !modifiers.HasFlag(RawInputModifiers.Control))
                {
                    Input?.Invoke(new RawTextInputEventArgs(_keyboardDevice, timestamp,
                        _inputRoot, keyChar.ToString()));
                }

                // Terminal delivers key combos atomically — release all modifiers after KeyUp
                ReleaseOldModifiers(RawInputModifiers.None, timestamp);
            }
        }

        private void PressNewModifiers(RawInputModifiers modifiers, ulong timestamp)
        {
            if (modifiers.HasFlag(RawInputModifiers.Alt) && !_activeModifiers.HasFlag(RawInputModifiers.Alt))
                SendModifierKey(Key.LeftAlt, RawKeyEventType.KeyDown, modifiers, timestamp);
            if (modifiers.HasFlag(RawInputModifiers.Control) && !_activeModifiers.HasFlag(RawInputModifiers.Control))
                SendModifierKey(Key.LeftCtrl, RawKeyEventType.KeyDown, modifiers, timestamp);
            if (modifiers.HasFlag(RawInputModifiers.Shift) && !_activeModifiers.HasFlag(RawInputModifiers.Shift))
                SendModifierKey(Key.LeftShift, RawKeyEventType.KeyDown, modifiers, timestamp);

            _activeModifiers = modifiers;
        }

        private void ReleaseOldModifiers(RawInputModifiers modifiers, ulong timestamp)
        {
            if (!modifiers.HasFlag(RawInputModifiers.Alt) && _activeModifiers.HasFlag(RawInputModifiers.Alt))
                SendModifierKey(Key.LeftAlt, RawKeyEventType.KeyUp, RawInputModifiers.None, timestamp);
            if (!modifiers.HasFlag(RawInputModifiers.Control) && _activeModifiers.HasFlag(RawInputModifiers.Control))
                SendModifierKey(Key.LeftCtrl, RawKeyEventType.KeyUp, RawInputModifiers.None, timestamp);
            if (!modifiers.HasFlag(RawInputModifiers.Shift) && _activeModifiers.HasFlag(RawInputModifiers.Shift))
                SendModifierKey(Key.LeftShift, RawKeyEventType.KeyUp, RawInputModifiers.None, timestamp);

            _activeModifiers = modifiers;
        }

        private void SendModifierKey(Key key, RawKeyEventType type, RawInputModifiers modifiers, ulong timestamp)
        {
            Input?.Invoke(new RawKeyEventArgs(_keyboardDevice, timestamp, _inputRoot,
                type, key, modifiers, PhysicalKey.None, null, KeyDeviceType.Keyboard));
        }

        private void OnMouseEvent(RawPointerEventType type, Point point, Vector? wheelDelta,
            RawInputModifiers modifiers)
        {
            // Scale mouse coords from cells to pixels
            point = new Point(point.X * Terminal.CellPixelWidth, point.Y * Terminal.CellPixelHeight);

            var oldPos = CursorPixelPosition;
            CursorPixelPosition = point;

            // Fast cursor update if cursor cell changed (no full frame render)
            int newCol = (int)(point.X / Terminal.CellPixelWidth);
            int newRow = (int)(point.Y / Terminal.CellPixelHeight);
            int oldCol = (int)(oldPos.X / Terminal.CellPixelWidth);
            int oldRow = (int)(oldPos.Y / Terminal.CellPixelHeight);
            if (newCol != oldCol || newRow != oldRow)
                CursorDirty = true;

            ulong timestamp = (ulong)Environment.TickCount64;
            if (type == RawPointerEventType.Wheel)
            {
                Input?.Invoke(new RawMouseWheelEventArgs(_mouseDevice, timestamp, _inputRoot,
                    point, (Vector)wheelDelta!, modifiers));
            }
            else
            {
                Input?.Invoke(new RawPointerEventArgs(_mouseDevice, timestamp, _inputRoot,
                    type, point, modifiers));
            }
        }

        private void OnFocusEvent(bool focused)
        {
            if (focused) Activated?.Invoke();
            else Deactivated?.Invoke();
        }
    }
}
