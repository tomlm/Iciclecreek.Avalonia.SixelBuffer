using Avalonia.Input;

namespace Iciclecreek.Avalonia.SixelBuffer.Platform
{
    /// <summary>
    ///     Maps Avalonia cursor types to the CSS cursor names used by the OSC 22
    ///     pointer shape protocol (kitty, foot, ghostty, xterm).
    /// </summary>
    internal static class PointerShapes
    {
        public static string ToOsc22(StandardCursorType type)
        {
            return type switch
            {
                StandardCursorType.Arrow => "default",
                StandardCursorType.Ibeam => "text",
                StandardCursorType.Wait => "wait",
                StandardCursorType.Cross => "crosshair",
                StandardCursorType.UpArrow => "default",
                StandardCursorType.SizeWestEast => "ew-resize",
                StandardCursorType.SizeNorthSouth => "ns-resize",
                StandardCursorType.SizeAll => "move",
                StandardCursorType.No => "not-allowed",
                StandardCursorType.Hand => "pointer",
                StandardCursorType.AppStarting => "progress",
                StandardCursorType.Help => "help",
                StandardCursorType.TopSide => "n-resize",
                StandardCursorType.BottomSide => "s-resize",
                StandardCursorType.LeftSide => "w-resize",
                StandardCursorType.RightSide => "e-resize",
                StandardCursorType.TopLeftCorner => "nw-resize",
                StandardCursorType.TopRightCorner => "ne-resize",
                StandardCursorType.BottomLeftCorner => "sw-resize",
                StandardCursorType.BottomRightCorner => "se-resize",
                StandardCursorType.DragMove => "move",
                StandardCursorType.DragCopy => "copy",
                StandardCursorType.DragLink => "alias",
                // OSC 22 has no "hidden" shape; fall back to the default pointer
                StandardCursorType.None => "default",
                _ => "default"
            };
        }
    }
}
