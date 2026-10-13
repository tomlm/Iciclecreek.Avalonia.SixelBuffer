namespace Iciclecreek.Avalonia.SixelBuffer.Terminal
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

        // Mouse tracking — SGR-Pixels mode (DECSET 1016): same SGR format, but
        // coordinates are reported in pixels instead of character cells.
        public const string EnablePixelMouseTracking = "\u001b[?1016h";
        public const string DisablePixelMouseTracking = "\u001b[?1016l";

        // Kitty graphics protocol. The query transmits a 1x1 RGB image (three zero bytes, "AAAA")
        // under id 31 with a=q, which asks the terminal to answer "APC _ G i=31;OK ST" and keep
        // nothing; terminals without the protocol ignore it. Delete-all frees every image.
        public const string KittyQuerySupport = "\u001b_Gi=31,s=1,v=1,a=q,t=d,f=24;AAAA\u001b\\";
        public const string KittyDeleteAllImages = "\u001b_Ga=d,d=A,q=2\u001b\\";

        // Synchronized output (DEC private mode 2026): the terminal holds the screen
        // until the end marker so a frame of many sixels paints atomically.
        public const string BeginSynchronizedUpdate = "\u001b[?2026h";
        public const string EndSynchronizedUpdate = "\u001b[?2026l";

        // Bracketed paste
        public const string EnableBracketedPasteMode = "\u001b[?2004h";
        public const string DisableBracketedPasteMode = "\u001b[?2004l";

        // Reset
        public const string Reset = "\u001b[0m";

        // Terminal queries
        public const string QueryCellPixelSize = "\u001b[16t";
        public const string QueryTextAreaPixelSize = "\u001b[14t";
        public const string QueryPrimaryDeviceAttributes = "\u001b[c";

        /// <summary>
        ///     DECRQM — request the state of a DEC private mode.
        ///     Response: CSI ? mode ; value $ y, where value is
        ///     0 = not recognized, 1 = set, 2 = reset, 3 = permanently set, 4 = permanently reset.
        /// </summary>
        public static string QueryPrivateMode(int mode)
        {
            return $"\u001b[?{mode}$p";
        }

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

        // Pointer shape (OSC 22) — kitty "pointer shapes" protocol, also honoured by
        // xterm/foot/ghostty for the set form. Shape names are CSS cursor names.
        // Query the current shape; a terminal that implements the protocol replies
        // with OSC 22 ; <shape-name> ST. Terminals that don't support it stay silent.
        public const string QueryPointerShape = "\u001b]22;?__current__\u001b\\";

        public static string SetPointerShape(string shape)
        {
            return $"\u001b]22;{shape}\u001b\\";
        }
    }
}
