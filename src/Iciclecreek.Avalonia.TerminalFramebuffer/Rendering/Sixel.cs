using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using JeremyAnsel.ColorQuant;

namespace Iciclecreek.Avalonia.TerminalFramebuffer.Rendering
{
    /// <summary>
    ///     Represents a sixel image with palette and indexed pixel data.
    ///     Supports composition via BitBlt and serialization via Render.
    /// </summary>
    public class Sixel
    {
        [SuppressMessage("Performance", "CA1819:Properties should not return arrays")]
        public byte[] Palette { get; }

        public int PaletteCount { get; }

        /// <summary>How many palette entries are frequency-reserved (first N slots).</summary>
        public int ReservedCount { get; }

        [SuppressMessage("Performance", "CA1819:Properties should not return arrays")]
        public byte[] Pixels { get; }

        public int Width { get; }
        public int Height { get; }
        public int CellWidth { get; }
        public int CellHeight { get; }
        public int CellsWidth => Width / CellWidth;
        public int CellsHeight => Height / CellHeight;

        public Sixel(byte[] palette, int paletteCount, int reservedCount, byte[] pixels, int width, int height,
            int cellWidth, int cellHeight)
        {
            Palette = palette;
            PaletteCount = paletteCount;
            ReservedCount = reservedCount;
            Pixels = pixels;
            Width = width;
            Height = height;
            CellWidth = cellWidth;
            CellHeight = cellHeight;
        }

        public static Sixel CreateFromBitmap(byte[] bgrx, int width, int height,
            int cellWidth, int cellHeight, byte[] palette = null, int reservedCount = 0)
        {
            // Only consider the visible pixels (width * height), not the full buffer
            int pixelCount = width * height;
            if (palette != null)
            {
                int paletteCount = palette.Length / 4;
                byte[] indexed = QuantizeWithPalette(bgrx, palette, paletteCount, pixelCount, reservedCount);
                return new Sixel(palette, paletteCount, reservedCount, indexed, width, height, cellWidth, cellHeight);
            }
            else
            {
                Quantize(bgrx, pixelCount, out byte[] newPalette, out int paletteCount,
                    out int newReservedCount, out byte[] indexed);
                return new Sixel(newPalette, paletteCount, newReservedCount, indexed,
                    width, height, cellWidth, cellHeight);
            }
        }

        /// <summary>
        ///     Quantize a frame and return only the palette (for palette refresh comparison).
        /// </summary>
        public static (byte[] palette, int count) QuantizeForPalette(byte[] bgrx, int pixelCount = 0)
        {
            if (pixelCount == 0) pixelCount = bgrx.Length / 4;
            Quantize(bgrx, pixelCount, out byte[] palette, out int count, out _, out _);
            return (palette, count);
        }

        /// <summary>
        ///     Returns a copy of this Sixel with reserved palette entries replaced by green.
        ///     Does not modify the original.
        /// </summary>
        internal Sixel CreateDebugCopy()
        {
            Debug.Print($"RESERVEDCOUNT={ReservedCount}");
            byte[] debugPalette = new byte[Palette.Length];
            Array.Copy(Palette, debugPalette, Palette.Length);
            for (int i = 0; i < ReservedCount; i++)
            {
                debugPalette[i * 4] = 0;       // B
                debugPalette[i * 4 + 1] = 255;  // G
                debugPalette[i * 4 + 2] = 0;    // R
                debugPalette[i * 4 + 3] = 0;
            }
            return new Sixel(debugPalette, PaletteCount, ReservedCount, Pixels,
                Width, Height, CellWidth, CellHeight);
        }

        public void BitBlt(Sixel source, int x, int y)
        {
            for (int row = 0; row < source.Height; row++)
            {
                int destY = y + row;
                if (destY < 0) continue;
                if (destY >= Height) break;

                int srcOffset = row * source.Width;
                int dstOffset = destY * Width + x;
                int srcX = 0;
                int dstX = x;

                if (dstX < 0) { srcX = -dstX; dstX = 0; dstOffset = destY * Width; }

                int copyLen = Math.Min(source.Width - srcX, Width - dstX);
                if (copyLen <= 0) continue;

                Array.Copy(source.Pixels, srcOffset + srcX, Pixels, dstOffset, copyLen);
            }

            _renderedBytes = null;
        }

        #region Serialization

        [ThreadStatic] private static byte[] _scratchRenderBuf;
        [ThreadStatic] private static WuColorQuantizer _quantizer;
        private static readonly ConditionalWeakTable<byte[], PaletteLookup> PaletteLookups = new();
        private byte[] _renderedBytes;

        public ReadOnlySpan<byte> Render()
        {
            if (_renderedBytes != null) return _renderedBytes;

            int width = Width;
            int height = Height;
            byte[] palette = Palette;
            int paletteCount = PaletteCount;
            byte[] indexed = Pixels;

            int maxOutput = 64 + paletteCount * 20 + width * ((height + 5) / 6) * 4 + 4096;
            var output = RentOrGrow(ref _scratchRenderBuf, maxOutput);
            int pos = 0;

            // DCS q
            output[pos++] = 0x1B; output[pos++] = (byte)'P'; output[pos++] = (byte)'q';

            // Raster attributes
            output[pos++] = (byte)'"'; output[pos++] = (byte)'1'; output[pos++] = (byte)';';
            output[pos++] = (byte)'1'; output[pos++] = (byte)';';
            pos = WriteIntBuf(output, pos, width);
            output[pos++] = (byte)';';
            pos = WriteIntBuf(output, pos, height);

            // Palette
            for (int i = 0; i < paletteCount; i++)
            {
                int r = palette[i * 4 + 2] * 100 / 255;
                int g = palette[i * 4 + 1] * 100 / 255;
                int b = palette[i * 4] * 100 / 255;
                output[pos++] = (byte)'#'; pos = WriteIntBuf(output, pos, i);
                output[pos++] = (byte)';'; output[pos++] = (byte)'2'; output[pos++] = (byte)';';
                pos = WriteIntBuf(output, pos, r); output[pos++] = (byte)';';
                pos = WriteIntBuf(output, pos, g); output[pos++] = (byte)';';
                pos = WriteIntBuf(output, pos, b);
            }

            // Bands
            int bandCount = (height + 5) / 6;
            Span<bool> colorPresent = stackalloc bool[paletteCount];
            var sixelRow = ArrayPool<byte>.Shared.Rent(width);

            try
            {
                for (int band = 0; band < bandCount; band++)
                {
                    int yStart = band * 6;
                    int bandRows = Math.Min(6, height - yStart);

                    colorPresent.Clear();
                    for (int row = 0; row < bandRows; row++)
                    {
                        int rowOff = (yStart + row) * width;
                        for (int x = 0; x < width; x++)
                            colorPresent[indexed[rowOff + x]] = true;
                    }

                    int bandWorstCase = paletteCount * (width + 20);
                    if (pos + bandWorstCase > output.Length)
                    {
                        int newLen = Math.Max(output.Length * 2, pos + bandWorstCase + 4096);
                        var newBuf = new byte[newLen];
                        output.AsSpan(0, pos).CopyTo(newBuf);
                        _scratchRenderBuf = newBuf;
                        output = newBuf;
                    }

                    bool anyColor = false;
                    for (int color = 0; color < paletteCount; color++)
                    {
                        if (!colorPresent[color]) continue;
                        BuildSixelRow(indexed, sixelRow, width, yStart, bandRows, (byte)color);
                        if (anyColor) output[pos++] = (byte)'$';
                        output[pos++] = (byte)'#'; pos = WriteIntBuf(output, pos, color);
                        pos = WriteRleBuf(output, pos, sixelRow, width);
                        anyColor = true;
                    }

                    if (band < bandCount - 1) output[pos++] = (byte)'-';
                }
            }
            finally { ArrayPool<byte>.Shared.Return(sixelRow); }

            // ST
            output[pos++] = 0x1B; output[pos++] = (byte)'\\';

            byte[] rendered = GC.AllocateUninitializedArray<byte>(pos);
            output.AsSpan(0, pos).CopyTo(rendered);
            _renderedBytes = rendered;
            return rendered;
        }

        #endregion

        #region Quantization

        private const int ReservedSlots = 32;

        private static void Quantize(byte[] bgrx, int pixelCount,
            out byte[] palette, out int paletteCount, out int reservedCount, out byte[] indexed)
        {
            // --- Pass 1: count frequency of each unique color ---
            var freq = new Dictionary<uint, int>();
            for (int i = 0, off = 0; i < pixelCount; i++, off += 4)
            {
                uint key = (uint)bgrx[off]
                    | ((uint)bgrx[off + 1] << 8)
                    | ((uint)bgrx[off + 2] << 16);
                if (freq.TryGetValue(key, out int c))
                    freq[key] = c + 1;
                else
                    freq[key] = 1;
            }

            // --- Build reserved palette from top-frequency colors ---
            var sorted = new List<KeyValuePair<uint, int>>(freq);
            sorted.Sort((a, b) => b.Value.CompareTo(a.Value));

            reservedCount = Math.Min(ReservedSlots, sorted.Count);

            var reservedMap = new Dictionary<uint, byte>(reservedCount);
            byte[] reservedPalette = new byte[reservedCount * 4];
            for (int i = 0; i < reservedCount; i++)
            {
                uint bgr = sorted[i].Key;
                reservedMap[bgr] = (byte)i;
                reservedPalette[i * 4] = (byte)bgr;
                reservedPalette[i * 4 + 1] = (byte)(bgr >> 8);
                reservedPalette[i * 4 + 2] = (byte)(bgr >> 16);
                reservedPalette[i * 4 + 3] = 0;
            }

            // --- Pass 2: build filtered pixel buffer (non-reserved pixels only) ---
            int filteredCount = 0;
            for (int i = 0, off = 0; i < pixelCount; i++, off += 4)
            {
                uint key = (uint)bgrx[off]
                    | ((uint)bgrx[off + 1] << 8)
                    | ((uint)bgrx[off + 2] << 16);
                if (!reservedMap.ContainsKey(key))
                    filteredCount++;
            }

            byte[] filteredBgrx;
            if (filteredCount > 0)
            {
                filteredBgrx = new byte[filteredCount * 4];
                int dst = 0;
                for (int i = 0, off = 0; i < pixelCount; i++, off += 4)
                {
                    uint key = (uint)bgrx[off]
                        | ((uint)bgrx[off + 1] << 8)
                        | ((uint)bgrx[off + 2] << 16);
                    if (!reservedMap.ContainsKey(key))
                    {
                        filteredBgrx[dst] = bgrx[off];
                        filteredBgrx[dst + 1] = bgrx[off + 1];
                        filteredBgrx[dst + 2] = bgrx[off + 2];
                        filteredBgrx[dst + 3] = bgrx[off + 3];
                        dst += 4;
                    }
                }
            }
            else
            {
                // All pixels are reserved colors — no dynamic quantization needed
                palette = reservedPalette;
                paletteCount = reservedCount;
                indexed = QuantizeWithPalette(bgrx, palette, paletteCount, pixelCount, reservedCount);
                return;
            }

            // --- Quantize only the filtered pixels into dynamic slots ---
            var wuQuantizer = _quantizer ??= new WuColorQuantizer();
            int dynamicSlots = Math.Max(1, 256 - reservedCount);
            var dynamicResult = wuQuantizer.Quantize(filteredBgrx, dynamicSlots);
            int dynamicCount = dynamicResult.Palette.Length / 4;

            // --- Build combined palette: reserved first, then dynamic ---
            paletteCount = reservedCount + dynamicCount;
            palette = new byte[paletteCount * 4];
            Array.Copy(reservedPalette, 0, palette, 0, reservedCount * 4);
            Array.Copy(dynamicResult.Palette, 0, palette, reservedCount * 4, dynamicCount * 4);

            // --- Re-index all pixels: exact match for reserved, approximate for rest ---
            indexed = QuantizeWithPalette(bgrx, palette, paletteCount, pixelCount, reservedCount);
        }


        private static byte[] QuantizeWithPalette(byte[] bgrx, byte[] palette, int paletteCount,
            int pixelCount = 0, int reservedCount = 0)
        {
            if (pixelCount == 0) pixelCount = bgrx.Length / 4;

            // Build exact-match map for reserved colors if present
            Dictionary<uint, byte>? reservedMap = null;
            if (reservedCount > 0)
            {
                reservedMap = new Dictionary<uint, byte>(reservedCount);
                for (int i = 0; i < reservedCount && i < paletteCount; i++)
                {
                    uint bgr = (uint)palette[i * 4]
                        | ((uint)palette[i * 4 + 1] << 8)
                        | ((uint)palette[i * 4 + 2] << 16);
                    reservedMap.TryAdd(bgr, (byte)i);
                }
            }

            byte[] indexed = GC.AllocateUninitializedArray<byte>(pixelCount);
            ReadOnlySpan<byte> bgrxSpan = bgrx;
            ReadOnlySpan<byte> paletteLookup = PaletteLookups.GetValue(palette,
                static currentPalette => new PaletteLookup(currentPalette)).Lookup;

            for (int i = 0, offset = 0; i < pixelCount; i++, offset += 4)
            {
                if (reservedMap != null)
                {
                    uint key = (uint)bgrxSpan[offset]
                        | ((uint)bgrxSpan[offset + 1] << 8)
                        | ((uint)bgrxSpan[offset + 2] << 16);
                    if (reservedMap.TryGetValue(key, out byte reservedIdx))
                    {
                        indexed[i] = reservedIdx;
                        continue;
                    }
                }

                int b = bgrxSpan[offset];
                int g = bgrxSpan[offset + 1];
                int r = bgrxSpan[offset + 2];
                int lookupIndex = ((r >> PaletteLookup.ChannelShift) << (PaletteLookup.ChannelBits * 2)) |
                    ((g >> PaletteLookup.ChannelShift) << PaletteLookup.ChannelBits) |
                    (b >> PaletteLookup.ChannelShift);
                indexed[i] = paletteLookup[lookupIndex];
            }

            return indexed;
        }

        #endregion

        #region SIMD helpers

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void BuildSixelRow(byte[] indexed, byte[] sixelRow, int width, int yStart, int bandRows, byte color)
        {
            ref byte rows0 = ref Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(indexed), yStart * width);
            ref byte outRef = ref MemoryMarshal.GetArrayDataReference(sixelRow);

            if (Vector256.IsHardwareAccelerated && width >= 32)
            {
                var vColor = Vector256.Create(color);
                var v63 = Vector256.Create((byte)63);
                int x = 0;
                for (; x + 32 <= width; x += 32)
                {
                    var bits = Vector256<byte>.Zero;
                    bits = Vector256.BitwiseOr(bits, Vector256.BitwiseAnd(Vector256.Equals(Vector256.LoadUnsafe(ref rows0, (nuint)x), vColor), Vector256.Create((byte)1)));
                    if (bandRows > 1) bits = Vector256.BitwiseOr(bits, Vector256.BitwiseAnd(Vector256.Equals(Vector256.LoadUnsafe(ref rows0, (nuint)(width + x)), vColor), Vector256.Create((byte)2)));
                    if (bandRows > 2) bits = Vector256.BitwiseOr(bits, Vector256.BitwiseAnd(Vector256.Equals(Vector256.LoadUnsafe(ref rows0, (nuint)(width * 2 + x)), vColor), Vector256.Create((byte)4)));
                    if (bandRows > 3) bits = Vector256.BitwiseOr(bits, Vector256.BitwiseAnd(Vector256.Equals(Vector256.LoadUnsafe(ref rows0, (nuint)(width * 3 + x)), vColor), Vector256.Create((byte)8)));
                    if (bandRows > 4) bits = Vector256.BitwiseOr(bits, Vector256.BitwiseAnd(Vector256.Equals(Vector256.LoadUnsafe(ref rows0, (nuint)(width * 4 + x)), vColor), Vector256.Create((byte)16)));
                    if (bandRows > 5) bits = Vector256.BitwiseOr(bits, Vector256.BitwiseAnd(Vector256.Equals(Vector256.LoadUnsafe(ref rows0, (nuint)(width * 5 + x)), vColor), Vector256.Create((byte)32)));
                    Vector256.Add(bits, v63).StoreUnsafe(ref outRef, (nuint)x);
                }
                for (; x < width; x++) Unsafe.Add(ref outRef, x) = BuildSixelScalar(ref rows0, x, width, bandRows, color);
            }
            else if (Vector128.IsHardwareAccelerated && width >= 16)
            {
                var vColor = Vector128.Create(color);
                var v63 = Vector128.Create((byte)63);
                int x = 0;
                for (; x + 16 <= width; x += 16)
                {
                    var bits = Vector128<byte>.Zero;
                    bits = Vector128.BitwiseOr(bits, Vector128.BitwiseAnd(Vector128.Equals(Vector128.LoadUnsafe(ref rows0, (nuint)x), vColor), Vector128.Create((byte)1)));
                    if (bandRows > 1) bits = Vector128.BitwiseOr(bits, Vector128.BitwiseAnd(Vector128.Equals(Vector128.LoadUnsafe(ref rows0, (nuint)(width + x)), vColor), Vector128.Create((byte)2)));
                    if (bandRows > 2) bits = Vector128.BitwiseOr(bits, Vector128.BitwiseAnd(Vector128.Equals(Vector128.LoadUnsafe(ref rows0, (nuint)(width * 2 + x)), vColor), Vector128.Create((byte)4)));
                    if (bandRows > 3) bits = Vector128.BitwiseOr(bits, Vector128.BitwiseAnd(Vector128.Equals(Vector128.LoadUnsafe(ref rows0, (nuint)(width * 3 + x)), vColor), Vector128.Create((byte)8)));
                    if (bandRows > 4) bits = Vector128.BitwiseOr(bits, Vector128.BitwiseAnd(Vector128.Equals(Vector128.LoadUnsafe(ref rows0, (nuint)(width * 4 + x)), vColor), Vector128.Create((byte)16)));
                    if (bandRows > 5) bits = Vector128.BitwiseOr(bits, Vector128.BitwiseAnd(Vector128.Equals(Vector128.LoadUnsafe(ref rows0, (nuint)(width * 5 + x)), vColor), Vector128.Create((byte)32)));
                    Vector128.Add(bits, v63).StoreUnsafe(ref outRef, (nuint)x);
                }
                for (; x < width; x++) Unsafe.Add(ref outRef, x) = BuildSixelScalar(ref rows0, x, width, bandRows, color);
            }
            else
            {
                for (int x = 0; x < width; x++) Unsafe.Add(ref outRef, x) = BuildSixelScalar(ref rows0, x, width, bandRows, color);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte BuildSixelScalar(ref byte rows0, int x, int width, int bandRows, byte color)
        {
            int bits = 0;
            if (Unsafe.Add(ref rows0, x) == color) bits |= 1;
            if (bandRows > 1 && Unsafe.Add(ref rows0, width + x) == color) bits |= 2;
            if (bandRows > 2 && Unsafe.Add(ref rows0, width * 2 + x) == color) bits |= 4;
            if (bandRows > 3 && Unsafe.Add(ref rows0, width * 3 + x) == color) bits |= 8;
            if (bandRows > 4 && Unsafe.Add(ref rows0, width * 4 + x) == color) bits |= 16;
            if (bandRows > 5 && Unsafe.Add(ref rows0, width * 5 + x) == color) bits |= 32;
            return (byte)(bits + 63);
        }

        #endregion

        #region Buffer helpers

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int WriteIntBuf(byte[] buf, int pos, int value)
        {
            if (value < 10) { buf[pos] = (byte)('0' + value); return pos + 1; }
            if (value < 100) { buf[pos] = (byte)('0' + value / 10); buf[pos + 1] = (byte)('0' + value % 10); return pos + 2; }
            int tmp = value, digits = 0;
            while (tmp > 0) { digits++; tmp /= 10; }
            pos += digits; int p = pos;
            while (value > 0) { buf[--p] = (byte)('0' + value % 10); value /= 10; }
            return pos;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int WriteRleBuf(byte[] output, int pos, byte[] data, int length)
        {
            int i = 0;
            while (i < length)
            {
                byte ch = data[i];
                int run = 1;
                while (i + run < length && data[i + run] == ch) run++;
                if (run >= 4) { output[pos++] = (byte)'!'; pos = WriteIntBuf(output, pos, run); output[pos++] = ch; }
                else { for (int j = 0; j < run; j++) output[pos++] = ch; }
                i += run;
            }
            return pos;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static T[] RentOrGrow<T>(ref T[] buf, int minSize)
        {
            if (buf == null || buf.Length < minSize)
                buf = GC.AllocateUninitializedArray<T>(Math.Max(minSize, 4096));
            return buf;
        }

        private sealed class PaletteLookup
        {
            public const int ChannelBits = 5;
            public const int ChannelShift = 8 - ChannelBits;
            private const int LookupSize = 1 << (ChannelBits * 3);

            public PaletteLookup(byte[] palette)
            {
                Lookup = GC.AllocateUninitializedArray<byte>(LookupSize);
                int paletteCount = palette.Length / 4;
                for (int index = 0; index < Lookup.Length; index++)
                {
                    int r = ((index >> (ChannelBits * 2)) & ((1 << ChannelBits) - 1)) << ChannelShift;
                    int g = ((index >> ChannelBits) & ((1 << ChannelBits) - 1)) << ChannelShift;
                    int b = (index & ((1 << ChannelBits) - 1)) << ChannelShift;
                    int bestIdx = 0, bestDist = int.MaxValue;
                    for (int pi = 0; pi < paletteCount; pi++)
                    {
                        int po = pi * 4;
                        int db = b - palette[po], dg = g - palette[po + 1], dr = r - palette[po + 2];
                        int dist = dr * dr + dg * dg + db * db;
                        if (dist < bestDist) { bestDist = dist; bestIdx = pi; }
                    }
                    Lookup[index] = (byte)bestIdx;
                }
            }

            public byte[] Lookup { get; }
        }

        #endregion
    }
}
