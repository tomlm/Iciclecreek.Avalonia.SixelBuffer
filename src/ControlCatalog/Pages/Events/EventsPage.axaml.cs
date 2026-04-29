using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Interactivity;
using Iciclecreek.Avalonia.SixelBuffer.Platform;
using Iciclecreek.Avalonia.SixelBuffer.Terminal;

namespace ControlCatalog.Pages.Events
{
    public partial class EventsPage : ContentPage
    {
        private ITerminal _terminal;

        public EventsPage()
        {
            InitializeComponent();
            DataContext = new EventsViewModel();
        }

        private EventsViewModel ViewModel => DataContext as EventsViewModel;

        // --- Pointer tab ---

        private void OnPointerMoved(object sender, PointerEventArgs e)
            => AddPointerEvent(e, e.GetCurrentPoint(this));

        private void OnPointerPressed(object sender, PointerPressedEventArgs e)
            => AddPointerEvent(e, e.GetCurrentPoint(this));

        private void OnPointerReleased(object sender, PointerReleasedEventArgs e)
            => AddPointerEvent(e, e.GetCurrentPoint(this));

        private void OnPointerWheelChanged(object sender, PointerWheelEventArgs e)
            => AddPointerEvent(e, e.GetCurrentPoint(this));

        private void AddPointerEvent(PointerEventArgs e, PointerPoint point, [CallerMemberName] string name = null)
        {
            MouseButton button = e is PointerReleasedEventArgs pre ? pre.InitialPressMouseButton : MouseButton.None;
            var ev = new EventItem
            {
                Name = name,
                Summary = $"[{point.Position.X:F0},{point.Position.Y:F0}] {name} ({e.KeyModifiers})",
                Details = $"Kind: {e.Properties.PointerUpdateKind}\n" +
                          $"Position: [{point.Position}]\n" +
                          $"KeyModifiers: {e.KeyModifiers}\n" +
                          $"Wheel: {(e is PointerWheelEventArgs pwe ? pwe.Delta.ToString() : "N/A")}\n" +
                          $"ClickCount: {(e is PointerPressedEventArgs ppe ? ppe.ClickCount : 0)}\n" +
                          $"Button: {button}"
            };
            ViewModel.AddPointerEvent(ev);
        }

        // --- Keyboard tab ---

        private void OnKeyDown(object sender, KeyEventArgs e)
            => AddKeyEvent(e, "KeyDown");

        private void OnKeyUp(object sender, KeyEventArgs e)
            => AddKeyEvent(e, "KeyUp");

        private void AddKeyEvent(KeyEventArgs e, string name)
        {
            var ev = new EventItem
            {
                Name = name,
                Summary = $"{name} {e.Key} ({e.KeyModifiers})",
                Details = $"Key: {e.Key} ({(int)e.Key})\n" +
                          $"PhysicalKey: {e.PhysicalKey}\n" +
                          $"KeyModifiers: {e.KeyModifiers}\n" +
                          $"KeySymbol: {e.KeySymbol}\n" +
                          $"KeyDeviceType: {e.KeyDeviceType}"
            };
            ViewModel.AddKeyboardEvent(ev);
        }

        // --- RawMouse tab ---

        private void OnRawMouse(RawPointerEventType type, Point point, Vector? delta, RawInputModifiers modifiers)
        {
            var ev = new EventItem
            {
                Name = type.ToString(),
                Summary = $"[{point.X:F0},{point.Y:F0}] {type} ({modifiers})",
                Details = $"Type: {type}\n" +
                          $"Point: {point}\n" +
                          $"Modifiers: {modifiers}\n" +
                          $"Delta: {delta}"
            };
            ViewModel.AddRawMouseEvent(ev);
        }

        private void OnRawMouseEntered(object sender, PointerEventArgs e)
        {
            EnsureTerminal();
            if (_terminal != null)
                _terminal.MouseEvent += OnRawMouse;
        }

        private void OnRawMouseExited(object sender, PointerEventArgs e)
        {
            if (_terminal != null)
                _terminal.MouseEvent -= OnRawMouse;
        }

        // --- RawKeyboard tab ---

        private void OnRawKey(Key key, char ch, RawInputModifiers mods, bool isDown, ulong timestamp, bool tryAsText)
        {
            var ev = new EventItem
            {
                Name = $"Raw{(isDown ? "Down" : "Up")} {key}",
                Summary = $"{(isDown ? "↓" : "↑")} {key} '{(char.IsControl(ch) ? '?' : ch)}' 0x{(int)ch:X2} ({mods})",
                Details = $"Key: {key}\n" +
                          $"Char: '{ch}' ({(int)ch}) 0x{(int)ch:X2}\n" +
                          $"Modifiers: {mods}\n" +
                          $"IsDown: {isDown}\n" +
                          $"TryAsTextInput: {tryAsText}"
            };
            ViewModel.AddRawKeyboardEvent(ev);
        }

        private void OnRawKeyboardGotFocus(object sender, RoutedEventArgs e)
        {
            EnsureTerminal();
            if (_terminal != null)
                _terminal.KeyEvent += OnRawKey;
        }

        private void OnRawKeyboardLostFocus(object sender, RoutedEventArgs e)
        {
            if (_terminal != null)
                _terminal.KeyEvent -= OnRawKey;
        }

        // --- Common ---

        private void OnDoubleTapped(object sender, TappedEventArgs e)
        {
            // Could show a detail dialog here
        }

        protected override void OnUnloaded(RoutedEventArgs e)
        {
            base.OnUnloaded(e);
            if (_terminal != null)
            {
                _terminal.KeyEvent -= OnRawKey;
                _terminal.MouseEvent -= OnRawMouse;
            }
        }

        private void EnsureTerminal()
        {
            if (_terminal != null) return;
            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel?.PlatformImpl is TerminalWindow tw)
                _terminal = tw.Terminal;
        }
    }

    public class EventItem
    {
        public string Name { get; set; }
        public string Summary { get; set; }
        public string Details { get; set; }
    }

    public class EventsViewModel : INotifyPropertyChanged
    {
        private const int MaxEvents = 500;

        public ObservableCollection<EventItem> PointerEvents { get; } = new();
        public ObservableCollection<EventItem> KeyboardEvents { get; } = new();
        public ObservableCollection<EventItem> RawMouseEvents { get; } = new();
        public ObservableCollection<EventItem> RawKeyboardEvents { get; } = new();

        private EventItem _selectedPointerEvent;
        public EventItem SelectedPointerEvent
        {
            get => _selectedPointerEvent;
            set { _selectedPointerEvent = value; OnPropertyChanged(); }
        }

        private EventItem _selectedKeyboardEvent;
        public EventItem SelectedKeyboardEvent
        {
            get => _selectedKeyboardEvent;
            set { _selectedKeyboardEvent = value; OnPropertyChanged(); }
        }

        private EventItem _selectedRawMouseEvent;
        public EventItem SelectedRawMouseEvent
        {
            get => _selectedRawMouseEvent;
            set { _selectedRawMouseEvent = value; OnPropertyChanged(); }
        }

        private EventItem _selectedRawKeyboardEvent;
        public EventItem SelectedRawKeyboardEvent
        {
            get => _selectedRawKeyboardEvent;
            set { _selectedRawKeyboardEvent = value; OnPropertyChanged(); }
        }

        public void AddPointerEvent(EventItem ev)
        {
            PointerEvents.Add(ev);
            while (PointerEvents.Count > MaxEvents) PointerEvents.RemoveAt(0);
            SelectedPointerEvent = ev;
        }

        public void AddKeyboardEvent(EventItem ev)
        {
            KeyboardEvents.Add(ev);
            while (KeyboardEvents.Count > MaxEvents) KeyboardEvents.RemoveAt(0);
            SelectedKeyboardEvent = ev;
        }

        public void AddRawMouseEvent(EventItem ev)
        {
            RawMouseEvents.Add(ev);
            while (RawMouseEvents.Count > MaxEvents) RawMouseEvents.RemoveAt(0);
            SelectedRawMouseEvent = ev;
        }

        public void AddRawKeyboardEvent(EventItem ev)
        {
            RawKeyboardEvents.Add(ev);
            while (RawKeyboardEvents.Count > MaxEvents) RawKeyboardEvents.RemoveAt(0);
            SelectedRawKeyboardEvent = ev;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
