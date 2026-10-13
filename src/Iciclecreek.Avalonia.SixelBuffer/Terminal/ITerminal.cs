using System;
using Avalonia;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Iciclecreek.Avalonia.SixelBuffer.Rendering;

namespace Iciclecreek.Avalonia.SixelBuffer.Terminal
{
    public interface ITerminal : IDisposable
    {
        TerminalSize Size { get; }
        int CellPixelWidth { get; }
        int CellPixelHeight { get; }
        int PixelWidth { get; }
        int PixelHeight { get; }

        /// <summary>True when the terminal advertised Sixel graphics in its primary device attributes.</summary>
        bool SixelSupported { get; }

        /// <summary>True when the terminal answered the kitty graphics protocol query.</summary>
        bool KittyGraphicsSupported { get; }

        /// <summary>The protocol frames are sent with, resolved from what the terminal supports.</summary>
        ImageProtocol Protocol { get; }

        /// <summary>
        ///     True when the terminal reports mouse positions in pixels (DECSET 1016, SGR-Pixels).
        ///     When false, positions are derived from the character cell grid.
        /// </summary>
        bool PixelMouseSupported { get; }

        /// <summary>
        ///     True when the terminal answered the OSC 22 pointer shape query, meaning the
        ///     mouse pointer shape can be set with <see cref="SetPointerShape"/> and the
        ///     composited software cursor is not needed.
        /// </summary>
        bool PointerShapeSupported { get; }

        /// <summary>
        ///     True when the terminal honours DEC private mode 2026 (synchronized output), so a
        ///     frame written between <see cref="BeginFrame"/> and <see cref="EndFrame"/> paints atomically.
        /// </summary>
        bool SynchronizedOutputSupported { get; }

        event Action? Resized;
        event Action<Key, char, RawInputModifiers, bool, ulong, bool>? KeyEvent;

        /// <summary>
        ///     Raised for mouse input. The <see cref="Point"/> is always in pixel coordinates;
        ///     terminals without pixel mouse support report the top-left pixel of the cell.
        /// </summary>
        event Action<RawPointerEventType, Point, Vector?, RawInputModifiers>? MouseEvent;
        event Action<bool>? FocusEvent;

        void SetTitle(string title);

        /// <summary>
        ///     Set the terminal's mouse pointer shape via OSC 22 using a CSS cursor name
        ///     (e.g. "default", "pointer", "text", "ew-resize"). No-op when
        ///     <see cref="PointerShapeSupported"/> is false.
        /// </summary>
        void SetPointerShape(string shape);
        void SetCaretPosition(CellPosition position);

        /// <summary>Queue the hide-cursor sequence. It is written with the next flush.</summary>
        void HideCaret();
        void WriteEsc(string escape);

        /// <summary>
        ///     Queue a rendered image (the bytes of a complete Sixel or kitty sequence) at a cell
        ///     position. The cursor is positioned there first.
        /// </summary>
        void WriteImage(CellPosition position, ReadOnlySpan<byte> image);

        /// <summary>Start a frame. Output is held until <see cref="EndFrame"/> flushes it in one write.</summary>
        void BeginFrame();

        /// <summary>End the frame and flush everything queued since <see cref="BeginFrame"/>.</summary>
        void EndFrame();
        void Flush();
        void ClearScreen();
        void PrepareConsole();
        void RestoreConsole();
    }
}
