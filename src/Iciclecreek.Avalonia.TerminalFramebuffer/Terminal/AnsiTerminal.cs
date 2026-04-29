using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Iciclecreek.Avalonia.TerminalFramebuffer.Rendering;
using Avalonia.Threading;
using Avalonia;

namespace Iciclecreek.Avalonia.TerminalFramebuffer.Terminal
{
    /// <summary>
    ///     Cross-platform ANSI terminal with VT escape sequence input parsing.
    ///     Supports keyboard and SGR extended mouse mode.
    /// </summary>
    public class AnsiTerminal : ITerminal
    {
        private readonly AnsiTerminalOutput _output;
        private bool _disposed;

        public AnsiTerminal()
        {
            _output = new AnsiTerminalOutput();
            System.Console.TreatControlCAsInput = true;
        }

        public TerminalSize Size
        {
            get => _output.Size;
            private set
            {
                if (_output.Size == value) return;
                _output.Size = value;
                Resized?.Invoke();
            }
        }

        public int CellPixelWidth => _output.CellPixelWidth;
        public int CellPixelHeight => _output.CellPixelHeight;
        public int PixelWidth => _output.PixelWidth;
        public int PixelHeight => _output.PixelHeight;

        public event Action Resized;
        public event Action<Key, char, RawInputModifiers, bool, ulong, bool> KeyEvent;
        public event Action<RawPointerEventType, Point, Vector?, RawInputModifiers> MouseEvent;
        public event Action<bool> FocusEvent;

        public void SetTitle(string title) => _output.SetTitle(title);
        public void SetCaretPosition(CellPosition position) => _output.SetCaretPosition(position);
        public void HideCaret() => _output.HideCaret();
        public void WriteEsc(string escape) => _output.WriteText(escape);
        public void WriteSixel(CellPosition position, Sixel sixel) => _output.WriteSixel(position, sixel);
        public void Flush() => _output.Flush();
        public void ClearScreen() => _output.ClearScreen();

        public void PrepareConsole()
        {
            _output.PrepareConsole();
            EnableRawMode();
            DetectCellSize();

            // Enable SGR extended mouse tracking
            _output.WriteText(Esc.EnableAllMouseEvents);
            _output.WriteText(Esc.EnableExtendedMouseTracking);
            _output.Flush();

            StartSizeCheckTimer();
            StartInputReading();
        }

        public void RestoreConsole()
        {
            _output.WriteText(Esc.DisableAllMouseEvents);
            _output.WriteText(Esc.DisableExtendedMouseTracking);
            _output.RestoreConsole();
            RestoreRawMode();
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                RestoreConsole();
            }
        }

        #region Cell size detection

        private void DetectCellSize()
        {
            int cellW = 0, cellH = 0;

            // Try querying cell pixel size directly (most reliable)
            string response16 = RequestAnsiResponse(Esc.QueryCellPixelSize, 't', 200);

            int idx6 = response16.IndexOf('6');
            if (idx6 >= 0 && response16.EndsWith('t'))
            {
                string inner = response16[(idx6 + 1)..^1];
                string[] parts = inner.Split(';', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2)
                {
                    int.TryParse(parts[0], out cellH);
                    int.TryParse(parts[1], out cellW);
                }
            }

            // Fallback: \x1b[14t — derive cell size from text area pixels / cols
            if (cellW <= 0 || cellH <= 0)
            {
                string response14 = RequestAnsiResponse(Esc.QueryTextAreaPixelSize, 't', 200);

                int idx4 = response14.IndexOf('4');
                if (idx4 >= 0 && response14.EndsWith('t'))
                {
                    string inner = response14[(idx4 + 1)..^1];
                    string[] parts = inner.Split(';', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 2 &&
                        int.TryParse(parts[0], out int heightPx) &&
                        int.TryParse(parts[1], out int widthPx) &&
                        widthPx > 0 && heightPx > 0)
                    {
                        int cols14 = System.Console.WindowWidth;
                        int rows14 = System.Console.WindowHeight;
                        if (cols14 > 0 && rows14 > 0)
                        {
                            // Round to nearest instead of truncating
                            cellW = (widthPx + cols14 / 2) / cols14;
                            cellH = (heightPx + rows14 / 2) / rows14;
                        }
                    }
                }
            }

            // Final fallback
            if (cellW <= 0 || cellH <= 0)
            {
                cellW = 8;
                cellH = 16;
            }

            _output.CellPixelWidth = cellW;
            _output.CellPixelHeight = cellH;

            // Always compute pixel dimensions from current cols/rows * cell size
            // (re-read Console.WindowWidth/Height in case it changed during detection)
            int finalCols = System.Console.WindowWidth;
            int finalRows = System.Console.WindowHeight;
            _output.Size = new TerminalSize((ushort)finalCols, (ushort)finalRows);
            _output.PixelWidth = finalCols * cellW;
            _output.PixelHeight = finalRows * cellH;
        }

        private string RequestAnsiResponse(string request, char terminator, int timeoutMs)
        {
            _output.WriteText(request);
            _output.Flush();

            var sb = new StringBuilder();
            long deadline = Environment.TickCount64 + timeoutMs;
            var buf = new byte[1];

            while (Environment.TickCount64 < deadline)
            {
                int bytesRead;
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    if (!System.Console.KeyAvailable) continue;
                    char c = System.Console.ReadKey(true).KeyChar;
                    sb.Append(c);
                    if (c == terminator) break;
                }
                else
                {
                    unsafe
                    {
                        fixed (byte* p = buf)
                            bytesRead = (int)libc_read(STDIN_FILENO, (IntPtr)p, (UIntPtr)1);
                    }

                    if (bytesRead <= 0) continue;
                    char c = (char)buf[0];
                    sb.Append(c);
                    if (c == terminator) break;
                }
            }

            return sb.ToString();
        }

        #endregion

        #region Size check timer

        private void StartSizeCheckTimer()
        {
            Task.Run(async () =>
            {
                while (!_disposed)
                {
                    await WaitForDispatcher();
                    int timeout = 1500;

                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        var (cols, rows) = GetTerminalSize();
                        if (Size.Columns != cols || Size.Rows != rows)
                        {
                            _output.PixelWidth = cols * _output.CellPixelWidth;
                            _output.PixelHeight = rows * _output.CellPixelHeight;
                            Size = new TerminalSize((ushort)cols, (ushort)rows);
                            timeout = 1;
                        }
                    }, DispatcherPriority.Input);
                    await Task.Delay(timeout);
                }
            });
        }

        /// <summary>
        ///     Get terminal size without touching System.Console (which can reset termios on Unix).
        /// </summary>
        private static (int cols, int rows) GetTerminalSize()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                return (System.Console.WindowWidth, System.Console.WindowHeight);

            // Use TIOCGWINSZ ioctl to get terminal size directly
            if (ioctl(STDOUT_FILENO, TIOCGWINSZ, out WinSize ws) == 0 && ws.ws_col > 0 && ws.ws_row > 0)
                return (ws.ws_col, ws.ws_row);

            // Fallback
            return (System.Console.WindowWidth, System.Console.WindowHeight);
        }

        #endregion

        #region Input reading and VT parsing

        /// <summary>
        ///     Single cross-platform input reader. Raw mode is enabled via EnableRawMode()
        ///     (SetConsoleMode on Windows, tcsetattr on Unix). Then we read raw bytes from
        ///     stdin and parse VT escape sequences ourselves.
        ///     On Unix we use libc read() directly to avoid .NET Console resetting terminal mode.
        /// </summary>
        private void StartInputReading()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Debug.WriteLine("[Input] Using Windows Console.ReadKey path");
                StartInputReadingWindows();
            }
            else
            {
                Debug.WriteLine("[Input] Using Unix libc read() path");
                StartInputReadingUnix();
            }
        }

        /// <summary>
        ///     On Windows, Console.ReadKey(true) is the only reliable way to read input
        ///     without echo. OpenStandardInput doesn't deliver console input events.
        ///     ESC sequences (mouse) are collected into byte buffers for VT parsing.
        ///     Regular keys use ConsoleKeyInfo for proper modifier handling.
        /// </summary>
        private void StartInputReadingWindows()
        {
            Task.Run(async () =>
            {
                await WaitForDispatcher();

                while (!_disposed)
                {
                    ConsoleKeyInfo keyInfo;
                    try { keyInfo = System.Console.ReadKey(true); }
                    catch (InvalidOperationException) { continue; }

                    if (keyInfo.KeyChar == '\x1b')
                    {
                        // Escape — collect follow-up bytes for VT sequence (mouse, etc.)
                        var buf = new List<byte> { 0x1b };
                        await Task.Delay(1);
                        while (System.Console.KeyAvailable)
                        {
                            try { buf.Add((byte)System.Console.ReadKey(true).KeyChar); }
                            catch (InvalidOperationException) { break; }
                        }

                        if (buf.Count == 1)
                        {
                            await Dispatcher.UIThread.InvokeAsync(
                                () => RaiseKey(Key.Escape, '\x1b', RawInputModifiers.None),
                                DispatcherPriority.Input);
                        }
                        else
                        {
                            var data = buf.ToArray();
                            await Dispatcher.UIThread.InvokeAsync(
                                () => ProcessInput(data), DispatcherPriority.Input);
                        }
                    }
                    else
                    {
                        var ki = keyInfo;
                        await Dispatcher.UIThread.InvokeAsync(
                            () => ProcessConsoleKeyInfo(ki), DispatcherPriority.Input);
                    }
                }
            });
        }

        private void ProcessConsoleKeyInfo(ConsoleKeyInfo keyInfo)
        {
            Key key = ConvertConsoleKey(keyInfo.Key);
            RawInputModifiers mods = RawInputModifiers.None;
            if (keyInfo.Modifiers.HasFlag(ConsoleModifiers.Shift)) mods |= RawInputModifiers.Shift;
            if (keyInfo.Modifiers.HasFlag(ConsoleModifiers.Alt)) mods |= RawInputModifiers.Alt;
            if (keyInfo.Modifiers.HasFlag(ConsoleModifiers.Control)) mods |= RawInputModifiers.Control;

            // When the terminal is in VT/raw mode, Console.ReadKey returns ConsoleKey.None
            // with no modifiers. Fall back to CharToKey and derive modifiers from the character.
            if (key == Key.None && keyInfo.KeyChar != '\0')
            {
                char ch = keyInfo.KeyChar;
                key = CharToKey(ch);
                if (mods == RawInputModifiers.None)
                {
                    if (char.IsUpper(ch) || "~!@#$%^&*()_+{}|:\"<>?".Contains(ch))
                        mods = RawInputModifiers.Shift;
                    else if (ch >= '\x01' && ch <= '\x1a')
                        mods = RawInputModifiers.Control;
                }
            }

            RaiseKey(key, keyInfo.KeyChar, mods);
        }

        // Mapping from ConsoleKey to Avalonia Key — matches Consolonia's DefaultNetConsole
        private static readonly Dictionary<ConsoleKey, Key> ConsoleKeyMapping = new()
        {
            { ConsoleKey.Spacebar, Key.Space },
            { ConsoleKey.RightArrow, Key.Right },
            { ConsoleKey.LeftArrow, Key.Left },
            { ConsoleKey.UpArrow, Key.Up },
            { ConsoleKey.DownArrow, Key.Down },
            { ConsoleKey.Backspace, Key.Back },
            { ConsoleKey.Applications, Key.Apps },
            { ConsoleKey.Attention, Key.Attn },
            { ConsoleKey.LaunchApp1, Key.LaunchApplication1 },
            { ConsoleKey.LaunchApp2, Key.LaunchApplication2 },
            { ConsoleKey.MediaNext, Key.MediaNextTrack },
            { ConsoleKey.MediaPrevious, Key.MediaPreviousTrack },
            { ConsoleKey.MediaStop, Key.MediaStop },
            { ConsoleKey.MediaPlay, Key.MediaPlayPause },
            { ConsoleKey.LaunchMediaSelect, Key.SelectMedia },
            { ConsoleKey.EraseEndOfFile, Key.EraseEof },
            { ConsoleKey.LeftWindows, Key.LWin },
            { ConsoleKey.RightWindows, Key.RWin },
            { (ConsoleKey)18, Key.LeftAlt },
            { (ConsoleKey)16, Key.LeftShift },
            { (ConsoleKey)17, Key.LeftCtrl },
        };

        private static Key ConvertConsoleKey(ConsoleKey consoleKey)
        {
            if (ConsoleKeyMapping.TryGetValue(consoleKey, out Key key))
                return key;
            if (Enum.IsDefined(consoleKey) && Enum.TryParse(consoleKey.ToString(), out key))
                return key;
            return Key.None;
        }

        /// <summary>
        ///     On Unix with raw mode, read bytes directly via libc read() to avoid
        ///     .NET Console resetting terminal attributes.
        /// </summary>
        private void StartInputReadingUnix()
        {
            Task.Run(async () =>
            {
                await WaitForDispatcher();

                var buf = new byte[256];
                while (!_disposed)
                {
                    int bytesRead;
                    unsafe
                    {
                        fixed (byte* p = buf)
                            bytesRead = (int)libc_read(STDIN_FILENO, (IntPtr)p, (UIntPtr)buf.Length);
                    }

                    if (bytesRead <= 0) continue;

                    var data = buf.AsSpan(0, bytesRead).ToArray();
                    await Dispatcher.UIThread.InvokeAsync(() => ProcessInput(data), DispatcherPriority.Input);
                }
            });
        }

        private void ProcessInput(byte[] data)
        {
            int i = 0;
            while (i < data.Length)
            {
                if (data[i] == 0x1B && i + 1 < data.Length)
                {
                    int consumed = TryParseEscapeSequence(data, i);
                    if (consumed > 0)
                    {
                        i += consumed;
                        continue;
                    }

                    // Lone escape
                    RaiseKey(Key.Escape, '\x1b', RawInputModifiers.None);
                    i++;
                }
                else if (data[i] == 0x1B)
                {
                    RaiseKey(Key.Escape, '\x1b', RawInputModifiers.None);
                    i++;
                }
                else if (data[i] < 0x20)
                {
                    i += HandleControlChar(data[i]);
                }
                else
                {
                    // Regular character(s) — decode UTF-8
                    i += HandleTextInput(data, i);
                }
            }
        }

        private int TryParseEscapeSequence(byte[] data, int start)
        {
            if (start + 1 >= data.Length) return 0;

            byte next = data[start + 1];

            // CSI: ESC [
            if (next == '[' && start + 2 < data.Length)
                return TryParseCsi(data, start);

            // SS3: ESC O (F1-F4)
            if (next == 'O' && start + 2 < data.Length)
            {
                Key key = data[start + 2] switch
                {
                    (byte)'P' => Key.F1,
                    (byte)'Q' => Key.F2,
                    (byte)'R' => Key.F3,
                    (byte)'S' => Key.F4,
                    _ => Key.None
                };
                if (key != Key.None)
                {
                    RaiseKey(key, '\0', RawInputModifiers.None);
                    return 3;
                }
            }

            // Alt+char: ESC <char>
            if (next >= 0x20)
            {
                char c = (char)next;
                Key key = CharToKey(c);
                RaiseKey(key, c, RawInputModifiers.Alt);
                return 2;
            }

            return 0;
        }

        private int TryParseCsi(byte[] data, int start)
        {
            // Find the end of the CSI sequence (a letter or ~ or M/m)
            int seqStart = start + 2;

            // SGR mouse: ESC [ < params M/m
            if (data[seqStart] == '<')
                return TryParseSgrMouse(data, start);

            int end = seqStart;
            while (end < data.Length && data[end] >= 0x20 && data[end] < 0x40)
                end++;

            if (end >= data.Length) return 0;

            byte finalByte = data[end];
            string paramStr = Encoding.ASCII.GetString(data, seqStart, end - seqStart);
            int totalLen = end - start + 1;

            // Parse modifiers from param (e.g., "1;5" means Ctrl)
            RawInputModifiers mods = RawInputModifiers.None;
            string[] parts = paramStr.Split(';');
            if (parts.Length >= 2 && int.TryParse(parts[1], out int modParam))
                mods = DecodeModifiers(modParam);

            switch (finalByte)
            {
                case (byte)'A': RaiseKey(Key.Up, '\0', mods); return totalLen;
                case (byte)'B': RaiseKey(Key.Down, '\0', mods); return totalLen;
                case (byte)'C': RaiseKey(Key.Right, '\0', mods); return totalLen;
                case (byte)'D': RaiseKey(Key.Left, '\0', mods); return totalLen;
                case (byte)'H': RaiseKey(Key.Home, '\0', mods); return totalLen;
                case (byte)'F': RaiseKey(Key.End, '\0', mods); return totalLen;
                case (byte)'Z': RaiseKey(Key.Tab, '\0', RawInputModifiers.Shift); return totalLen;
                case (byte)'~':
                    if (int.TryParse(parts[0], out int tilde))
                    {
                        Key key = tilde switch
                        {
                            1 => Key.Home, 2 => Key.Insert, 3 => Key.Delete,
                            4 => Key.End, 5 => Key.PageUp, 6 => Key.PageDown,
                            15 => Key.F5, 17 => Key.F6, 18 => Key.F7,
                            19 => Key.F8, 20 => Key.F9, 21 => Key.F10,
                            23 => Key.F11, 24 => Key.F12,
                            _ => Key.None
                        };
                        if (key != Key.None) RaiseKey(key, '\0', mods);
                    }
                    return totalLen;
                case (byte)'I': // Focus in
                    FocusEvent?.Invoke(true);
                    return totalLen;
                case (byte)'O': // Focus out
                    FocusEvent?.Invoke(false);
                    return totalLen;
            }

            return totalLen; // consume unknown CSI
        }

        private int TryParseSgrMouse(byte[] data, int start)
        {
            // Format: ESC [ < button ; col ; row M/m
            int paramStart = start + 3; // after ESC [ <
            int end = paramStart;
            while (end < data.Length && data[end] != 'M' && data[end] != 'm')
                end++;

            if (end >= data.Length) return 0;

            bool isRelease = data[end] == 'm';
            string paramStr = Encoding.ASCII.GetString(data, paramStart, end - paramStart);
            string[] parts = paramStr.Split(';');
            if (parts.Length != 3) return end - start + 1;

            if (!int.TryParse(parts[0], out int button) ||
                !int.TryParse(parts[1], out int col) ||
                !int.TryParse(parts[2], out int row))
                return end - start + 1;

            // SGR reports 1-based coordinates
            col--;
            row--;

            var point = new Point(col, row);
            int buttonId = button & 0x03;
            bool motion = (button & 0x20) != 0;
            bool wheel = (button & 0x40) != 0;

            RawInputModifiers mods = RawInputModifiers.None;
            if ((button & 0x04) != 0) mods |= RawInputModifiers.Shift;
            if ((button & 0x08) != 0) mods |= RawInputModifiers.Alt;
            if ((button & 0x10) != 0) mods |= RawInputModifiers.Control;

            if (wheel)
            {
                double delta = buttonId == 0 ? 1 : -1;
                MouseEvent?.Invoke(RawPointerEventType.Wheel, point, new Vector(0, delta), mods);
            }
            else if (motion)
            {
                // During drag, include which button is held so Avalonia recognizes it as a drag
                mods |= buttonId switch
                {
                    0 => RawInputModifiers.LeftMouseButton,
                    1 => RawInputModifiers.MiddleMouseButton,
                    2 => RawInputModifiers.RightMouseButton,
                    _ => RawInputModifiers.None
                };
                MouseEvent?.Invoke(RawPointerEventType.Move, point, null, mods);
            }
            else
            {
                RawPointerEventType type;
                if (isRelease)
                {
                    type = buttonId switch
                    {
                        0 => RawPointerEventType.LeftButtonUp,
                        1 => RawPointerEventType.MiddleButtonUp,
                        2 => RawPointerEventType.RightButtonUp,
                        _ => RawPointerEventType.Move
                    };
                }
                else
                {
                    type = buttonId switch
                    {
                        0 => RawPointerEventType.LeftButtonDown,
                        1 => RawPointerEventType.MiddleButtonDown,
                        2 => RawPointerEventType.RightButtonDown,
                        _ => RawPointerEventType.Move
                    };
                }

                MouseEvent?.Invoke(type, point, null, mods);
            }

            return end - start + 1;
        }

        private int HandleControlChar(byte c)
        {
            // Map control bytes to keys.
            // 0x01-0x1A are Ctrl+A through Ctrl+Z (except special cases below).
            // 0x7F is DEL (Backspace on most terminals).
            // 0x08 is BS (Backspace on some terminals, or Ctrl+H).
            Key key;
            RawInputModifiers mods;

            switch (c)
            {
                case 0x08: // BS — Backspace (some terminals) or Ctrl+Backspace
                case 0x7F: // DEL — Backspace (most terminals)
                    key = Key.Back;
                    mods = RawInputModifiers.None;
                    break;
                case 0x09: // HT — Tab
                    key = Key.Tab;
                    mods = RawInputModifiers.None;
                    break;
                case 0x0A: // LF — Enter (Unix)
                case 0x0D: // CR — Enter
                    key = Key.Enter;
                    mods = RawInputModifiers.None;
                    break;
                case 0x00: // Ctrl+Space / Ctrl+@
                    key = Key.Space;
                    mods = RawInputModifiers.Control;
                    break;
                case >= 0x01 and <= 0x07: // Ctrl+A through Ctrl+G
                    key = Key.A + (c - 0x01);
                    mods = RawInputModifiers.Control;
                    break;
                case >= 0x0B and <= 0x0C: // Ctrl+K, Ctrl+L
                    key = Key.A + (c - 0x01);
                    mods = RawInputModifiers.Control;
                    break;
                case >= 0x0E and <= 0x1A: // Ctrl+N through Ctrl+Z
                    key = Key.A + (c - 0x01);
                    mods = RawInputModifiers.Control;
                    break;
                default:
                    key = Key.None;
                    mods = RawInputModifiers.None;
                    break;
            }

            if (key != Key.None)
                RaiseKey(key, (char)c, mods);

            return 1;
        }

        private int HandleTextInput(byte[] data, int start)
        {
            // Determine UTF-8 byte count
            byte first = data[start];
            int byteCount = first < 0x80 ? 1 :
                            first < 0xE0 ? 2 :
                            first < 0xF0 ? 3 : 4;

            if (start + byteCount > data.Length)
                byteCount = data.Length - start;

            string text = Encoding.UTF8.GetString(data, start, byteCount);
            if (text.Length > 0)
            {
                char c = text[0];
                Key key = CharToKey(c);
                RaiseKey(key, c, RawInputModifiers.None);
            }

            return byteCount;
        }

        private static RawInputModifiers DecodeModifiers(int modParam)
        {
            // xterm modifier encoding: param = 1 + (shift?1:0) + (alt?2:0) + (ctrl?4:0)
            int m = modParam - 1;
            RawInputModifiers mods = RawInputModifiers.None;
            if ((m & 1) != 0) mods |= RawInputModifiers.Shift;
            if ((m & 2) != 0) mods |= RawInputModifiers.Alt;
            if ((m & 4) != 0) mods |= RawInputModifiers.Control;
            return mods;
        }

        private static Key CharToKey(char c)
        {
            return c switch
            {
                >= 'a' and <= 'z' => Key.A + (c - 'a'),
                >= 'A' and <= 'Z' => Key.A + (c - 'A'),
                >= '0' and <= '9' => Key.D0 + (c - '0'),
                ' ' => Key.Space,
                '\t' => Key.Tab,
                '\r' or '\n' => Key.Enter,
                '\b' or '\x7f' => Key.Back,
                '\x1b' => Key.Escape,
                '`' or '~' => Key.OemTilde,
                '-' or '_' => Key.OemMinus,
                '=' or '+' => Key.OemPlus,
                '[' or '{' => Key.OemOpenBrackets,
                ']' or '}' => Key.OemCloseBrackets,
                '\\' or '|' => Key.OemBackslash,
                ';' or ':' => Key.OemSemicolon,
                '\'' or '"' => Key.OemQuotes,
                ',' or '<' => Key.OemComma,
                '.' or '>' => Key.OemPeriod,
                '/' or '?' => Key.OemQuestion,
                '!' => Key.D1,
                '@' => Key.D2,
                '#' => Key.D3,
                '$' => Key.D4,
                '%' => Key.D5,
                '^' => Key.D6,
                '&' => Key.D7,
                '*' => Key.D8,
                '(' => Key.D9,
                ')' => Key.D0,
                // Ctrl+letter produces 0x01-0x1A
                >= '\x01' and <= '\x1a' => Key.A + (c - '\x01'),
                _ => Key.None
            };
        }

        private void RaiseKey(Key key, char character, RawInputModifiers modifiers,
            bool tryAsTextInput = true)
        {
            ulong timestamp = (ulong)Environment.TickCount64;
            // KeyDown first
            KeyEvent?.Invoke(key, character, modifiers, true, timestamp, false);
            // KeyUp with tryAsTextInput — TerminalWindow will send RawTextInputEventArgs
            // only if the key wasn't handled and isn't a control char
            KeyEvent?.Invoke(key, character, modifiers, false, timestamp, tryAsTextInput);
        }

        private static async Task WaitForDispatcher()
        {
            // Dispatcher is initialized in TerminalPlatform.Initialize() before PrepareConsole.
            // Just wait briefly for the main loop to start processing.
            await Task.Delay(100);
        }

        #endregion

        #region Raw mode (disable echo, disable line buffering)

        private uint _savedConsoleMode;

        private void EnableRawMode()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                IntPtr handle = GetStdHandle(STD_INPUT_HANDLE);
                GetConsoleMode(handle, out _savedConsoleMode);
                uint newMode = (_savedConsoleMode & ~(ENABLE_ECHO_INPUT | ENABLE_LINE_INPUT))
                               | ENABLE_VIRTUAL_TERMINAL_INPUT;
                SetConsoleMode(handle, newMode);
            }
            else
            {
                // Allocate native memory for termios structs
                _savedTermiosPtr = Marshal.AllocHGlobal(TERMIOS_BUF_SIZE);
                IntPtr rawPtr = Marshal.AllocHGlobal(TERMIOS_BUF_SIZE);
                try
                {
                    int ret = tcgetattr(STDIN_FILENO, _savedTermiosPtr);
                    if (ret != 0)
                    {
                        int errno = Marshal.GetLastPInvokeError();
                        Marshal.FreeHGlobal(_savedTermiosPtr);
                        _savedTermiosPtr = IntPtr.Zero;
                        throw new InvalidOperationException(
                            $"tcgetattr failed: returned {ret}, errno={errno} ({GetErrnoMessage(errno)})");
                    }

                    // Copy saved state, then apply cfmakeraw
                    unsafe
                    {
                        Buffer.MemoryCopy(
                            _savedTermiosPtr.ToPointer(), rawPtr.ToPointer(),
                            TERMIOS_BUF_SIZE, TERMIOS_BUF_SIZE);
                    }

                    cfmakeraw(rawPtr);

                    ret = tcsetattr(STDIN_FILENO, TCSAFLUSH, rawPtr);
                    if (ret != 0)
                    {
                        int errno = Marshal.GetLastPInvokeError();
                        throw new InvalidOperationException(
                            $"tcsetattr failed: returned {ret}, errno={errno} ({GetErrnoMessage(errno)})");
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(rawPtr);
                }
            }
        }

        private void RestoreRawMode()
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                if (_savedConsoleMode != 0)
                {
                    IntPtr handle = GetStdHandle(STD_INPUT_HANDLE);
                    SetConsoleMode(handle, _savedConsoleMode);
                }
            }
            else
            {
                if (_savedTermiosPtr != IntPtr.Zero)
                {
                    tcsetattr(STDIN_FILENO, TCSAFLUSH, _savedTermiosPtr);
                    Marshal.FreeHGlobal(_savedTermiosPtr);
                    _savedTermiosPtr = IntPtr.Zero;
                }
            }
        }

        // === Windows: SetConsoleMode ===

        private const int STD_INPUT_HANDLE = -10;
        private const uint ENABLE_ECHO_INPUT = 0x0004;
        private const uint ENABLE_LINE_INPUT = 0x0002;
        private const uint ENABLE_VIRTUAL_TERMINAL_INPUT = 0x0200;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr GetStdHandle(int nStdHandle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetConsoleMode(IntPtr hConsoleHandle, out uint lpMode);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleMode(IntPtr hConsoleHandle, uint dwMode);

        // === Unix: tcgetattr / cfmakeraw / tcsetattr ===
        //
        // We allocate native memory for the termios struct and let cfmakeraw()
        // handle all the platform-specific flag manipulation.

        private const int STDIN_FILENO = 0;
        private const int TCSAFLUSH = 2;
        private const int TERMIOS_BUF_SIZE = 256; // generous — actual struct is 44-72 bytes
        private IntPtr _savedTermiosPtr;

        [DllImport("libc", SetLastError = true)]
        private static extern int tcgetattr(int fd, IntPtr termios);

        [DllImport("libc", SetLastError = true)]
        private static extern int tcsetattr(int fd, int optionalActions, IntPtr termios);

        [DllImport("libc")]
        private static extern void cfmakeraw(IntPtr termios);

        [DllImport("libc", EntryPoint = "read", SetLastError = true)]
        private static extern IntPtr libc_read(int fd, IntPtr buf, UIntPtr count);

        // Terminal size via ioctl (avoids System.Console which resets termios)
        private const int STDOUT_FILENO = 1;
        private static readonly uint TIOCGWINSZ = RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? 0x40087468u : 0x5413u;

        [StructLayout(LayoutKind.Sequential)]
        private struct WinSize
        {
            public ushort ws_row;
            public ushort ws_col;
            public ushort ws_xpixel;
            public ushort ws_ypixel;
        }

        [DllImport("libc", SetLastError = true)]
        private static extern int ioctl(int fd, uint request, out WinSize winSize);

        [DllImport("libc", EntryPoint = "strerror")]
        private static extern IntPtr strerror_raw(int errnum);

        private static string GetErrnoMessage(int errno)
        {
            IntPtr ptr = strerror_raw(errno);
            return ptr != IntPtr.Zero ? Marshal.PtrToStringAnsi(ptr) ?? $"Unknown error {errno}" : $"Unknown error {errno}";
        }

        #endregion
    }
}
