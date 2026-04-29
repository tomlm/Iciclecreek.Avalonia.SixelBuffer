namespace Iciclecreek.Avalonia.SixelBuffer.Terminal
{
    public readonly record struct TerminalSize(ushort Columns, ushort Rows)
    {
        public bool IsEmpty => Columns == 0 && Rows == 0;
    }
}
