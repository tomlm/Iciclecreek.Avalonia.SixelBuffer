namespace Iciclecreek.Avalonia.SixelBuffer.Terminal
{
    /// <summary>How frames are sent to the terminal.</summary>
    public enum ImageProtocol
    {
        /// <summary>Kitty graphics when the terminal answers the kitty query, otherwise Sixel.</summary>
        Auto,

        /// <summary>DEC Sixel (DCS q). Quantized to 256 colors per image.</summary>
        Sixel,

        /// <summary>Kitty graphics protocol (APC G). Exact colors, zlib-compressed RGB blocks.</summary>
        Kitty
    }
}
