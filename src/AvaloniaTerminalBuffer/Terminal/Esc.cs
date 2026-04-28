namespace Avalonia.Terminal.Terminal
{
    public static class Esc
    {
        // Screen buffer
        public const string EnableAlternateBuffer = "\u001b[?1049h";
        public const string DisableAlternateBuffer = "\u001b[?1049l";
        public const string ClearScreen = "\u001b[2J";

        // Cursor visibility
        public const string HideCursor = "\u001b[?25l";
        public const string ShowCursor = "\u001b[?25h";

        // Mouse tracking — SGR extended mode (works on all modern terminals)
        public const string EnableAllMouseEvents = "\u001b[?1003h";
        public const string DisableAllMouseEvents = "\u001b[?1003l";
        public const string EnableExtendedMouseTracking = "\u001b[?1006h";
        public const string DisableExtendedMouseTracking = "\u001b[?1006l";

        // Bracketed paste
        public const string EnableBracketedPasteMode = "\u001b[?2004h";
        public const string DisableBracketedPasteMode = "\u001b[?2004l";

        // Reset
        public const string Reset = "\u001b[0m";

        // Terminal queries
        public const string QueryCellPixelSize = "\u001b[16t";
        public const string QueryTextAreaPixelSize = "\u001b[14t";

        public static string SetCursorPosition(int x, int y)
        {
            return $"\u001b[{y + 1};{x + 1}f";
        }

        // Scroll region (DECSTBM)
        public static string SetScrollRegion(int top, int bottom)
        {
            return $"\u001b[{top};{bottom}r";
        }

        public const string ResetScrollRegion = "\u001b[r";

        public static string SetWindowTitle(string title)
        {
            return $"\u001b]0;{title}\u0007";
        }
    }
}
