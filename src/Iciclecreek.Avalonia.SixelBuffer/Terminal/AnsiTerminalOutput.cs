using System;
using System.Buffers;
using System.IO;
using System.Text;
using Iciclecreek.Avalonia.SixelBuffer.Rendering;

namespace Iciclecreek.Avalonia.SixelBuffer.Terminal
{
    /// <summary>
    ///     Buffered ANSI terminal output. Only supports Sixel rendering (no character-cell output).
    /// </summary>
    internal class AnsiTerminalOutput
    {
        // A frame above this size is handed back after the flush so one huge frame
        // doesn't pin its buffer for the life of the process.
        private const int KeptBufferBytes = 4 * 1024 * 1024;

        private ArrayBufferWriter<byte> _outputBuffer = new(64 * 1024);
        private readonly Stream _stdOut;

        // Where the terminal's cursor is, when we know. Null after a sixel: terminals
        // disagree about where the cursor lands after an image, so the next write
        // always positions explicitly.
        private CellPosition? _headPosition;

        // The render thread writes frames while the UI thread may write title / pointer
        // shape escapes. Each public write is atomic under this lock so sequences never interleave.
        private readonly object _sync = new();

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

        /// <summary>True when the terminal honours DEC 2026 synchronized updates.</summary>
        public bool SynchronizedOutput { get; set; }

        public void SetCaretPosition(CellPosition position)
        {
            lock (_sync)
            {
                if (_headPosition == position) return;
                SetCaretPositionInternal(position);
            }
        }

        /// <summary>Queue the hide-cursor sequence; it goes out with the frame.</summary>
        public void HideCaret()
        {
            WriteText(Esc.HideCursor);
        }

        /// <summary>Open a frame: start a synchronized update when the terminal supports it.</summary>
        public void BeginFrame()
        {
            if (SynchronizedOutput)
                WriteText(Esc.BeginSynchronizedUpdate);
        }

        /// <summary>Close the frame and write everything queued since <see cref="BeginFrame"/> in one write.</summary>
        public void EndFrame()
        {
            lock (_sync)
            {
                if (SynchronizedOutput)
                    WriteText(Esc.EndSynchronizedUpdate);
                Flush();
                if (_outputBuffer.Capacity > KeptBufferBytes)
                    _outputBuffer = new ArrayBufferWriter<byte>(64 * 1024);
            }
        }

        /// <summary>
        ///     Append a rendered image at a cell position. The cursor is always positioned
        ///     explicitly first (a pending wrap after the last column would otherwise put the
        ///     image a row off), and is treated as unknown afterwards.
        /// </summary>
        public void WriteImage(CellPosition position, ReadOnlySpan<byte> image)
        {
            lock (_sync)
            {
                SetCaretPositionInternal(position);
                image.CopyTo(_outputBuffer.GetSpan(image.Length));
                _outputBuffer.Advance(image.Length);
                _headPosition = null;
            }
        }

        public void Flush()
        {
            lock (_sync)
            {
                if (_outputBuffer.WrittenCount > 0)
                {
                    _stdOut.Write(_outputBuffer.WrittenSpan);
                    _stdOut.Flush();
                    _outputBuffer.ResetWrittenCount(); // Clear() would also zero the whole capacity
                }
            }
        }

        public void WriteText(string str)
        {
            lock (_sync)
            {
                int max = Encoding.UTF8.GetMaxByteCount(str.Length);
                Span<byte> span = _outputBuffer.GetSpan(max);
                int written = Encoding.UTF8.GetBytes(str.AsSpan(), span);
                _outputBuffer.Advance(written);
            }
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
