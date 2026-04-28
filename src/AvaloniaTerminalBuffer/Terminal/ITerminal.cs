using System;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Terminal.Rendering;

namespace Avalonia.Terminal.Terminal
{
    public interface ITerminal : IDisposable
    {
        TerminalSize Size { get; }
        int CellPixelWidth { get; }
        int CellPixelHeight { get; }
        int PixelWidth { get; }
        int PixelHeight { get; }

        event Action Resized;
        event Action<Key, char, RawInputModifiers, bool, ulong, bool> KeyEvent;
        event Action<RawPointerEventType, Point, Vector?, RawInputModifiers> MouseEvent;
        event Action<bool> FocusEvent;

        void SetTitle(string title);
        void SetCaretPosition(CellPosition position);
        void HideCaret();
        void WriteEsc(string escape);
        void WriteSixel(CellPosition position, Sixel sixel);
        void Flush();
        void ClearScreen();
        void PrepareConsole();
        void RestoreConsole();
    }
}
