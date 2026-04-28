using System;
using System.Buffers;
using System.IO;
using System.Text;
using Avalonia.Terminal.Rendering;

namespace Avalonia.Terminal.Terminal
{
    /// <summary>
    ///     Buffered ANSI terminal output. Only supports Sixel rendering (no character-cell output).
    /// </summary>
    internal class AnsiTerminalOutput
    {
        private readonly ArrayBufferWriter<byte> _outputBuffer = new();
        private readonly Stream _stdOut;
        private CellPosition _headPosition;

        public AnsiTerminalOutput()
        {
            System.Console.OutputEncoding = Encoding.UTF8;
            _stdOut = System.Console.OpenStandardOutput();
        }

        public TerminalSize Size { get; set; }
        public int CellPixelWidth { get; internal set; } = 8;
        public int CellPixelHeight { get; internal set; } = 16;
        public int PixelWidth { get; internal set; }
        public int PixelHeight { get; internal set; }

        public void SetCaretPosition(CellPosition position)
        {
            if (position == _headPosition) return;
            SetCaretPositionInternal(position);
        }

        public void HideCaret()
        {
            WriteText(Esc.HideCursor);
            Flush();
        }

        public void WriteSixel(CellPosition position, Sixel sixel)
        {
            //System.Diagnostics.Debug.WriteLine($"[WriteSixel] [{position.Column},{position.Row}] {sixel.Width}x{sixel.Height} {sixel.Render().Length} bytes");
            SetCaretPosition(position);

            ReadOnlySpan<byte> bytes = sixel.Render();
            bytes.CopyTo(_outputBuffer.GetSpan(bytes.Length));
            _outputBuffer.Advance(bytes.Length);

            var newPosition = new CellPosition((ushort)(position.Column + sixel.CellsWidth), position.Row);
            SetCaretPositionInternal(newPosition);
        }

        public void Flush()
        {
            if (_outputBuffer.WrittenCount > 0)
            {
                _stdOut.Write(_outputBuffer.WrittenSpan);
                _stdOut.Flush();
                _outputBuffer.Clear();
            }
        }

        public void WriteText(string str)
        {
            int max = Encoding.UTF8.GetMaxByteCount(str.Length);
            Span<byte> span = _outputBuffer.GetSpan(max);
            int written = Encoding.UTF8.GetBytes(str.AsSpan(), span);
            _outputBuffer.Advance(written);
        }

        public void PrepareConsole()
        {
            System.Console.Write(Esc.EnableAlternateBuffer);
            Size = new TerminalSize(
                (ushort)System.Console.WindowWidth,
                (ushort)System.Console.WindowHeight);
            ClearScreen();
        }

        public void RestoreConsole()
        {
            WriteText(Esc.DisableAlternateBuffer);
            WriteText(Esc.Reset);
            WriteText(Esc.ShowCursor);
            Flush();
        }

        public void ClearScreen()
        {
            WriteText(Esc.ClearScreen);
            _headPosition = new CellPosition(0, 0);
            WriteText(Esc.SetCursorPosition(0, 0));
            Flush();
        }

        public void SetTitle(string title)
        {
            WriteText(Esc.SetWindowTitle(title));
            Flush();
        }

        private void SetCaretPositionInternal(CellPosition position)
        {
            WriteText(Esc.SetCursorPosition(position.Column, position.Row));
            _headPosition = position;
        }
    }
}
