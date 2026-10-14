using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using JeremyAnsel.ColorQuant;

namespace Iciclecreek.Avalonia.SixelBuffer.Rendering
{
    /// <summary>
    ///     A sixel image: a palette plus one palette index per pixel. Serialized with
    ///     <see cref="RenderTo"/>, which defines only the colors the image actually uses.
    /// </summary>
    public sealed class Sixel
    {
        /// <summary>Palette as BGRX, 4 bytes per entry.</summary>
        [SuppressMessage("Performance", "CA1819:Properties should not return arrays")]
        public byte[] Palette { get; }

        public int PaletteCount { get; }

        /// <summary>One palette index per pixel, row-major. May be longer than Width*Height (pooled).</summary>
        [SuppressMessage("Performance", "CA1819:Properties should not return arrays")]
        public byte[] Pixels { get; }

        public int Width { get; }
        public int Height { get; }
        public int CellWidth { get; }
        public int CellHeight { get; }
        public int CellsWidth => Width / CellWidth;
        public int CellsHeight => Height / CellHeight;

        public Sixel(byte[] palette, int paletteCount, byte[] pixels, int width, int height,
            int cellWidth, int cellHeight)
        {
            ArgumentNullException.ThrowIfNull(palette);
            ArgumentNullException.ThrowIfNull(pixels);
            if (paletteCount < 0 || paletteCount > 256 || palette.Length < paletteCount * 4)
                throw new ArgumentOutOfRangeException(nameof(paletteCount));
            if (width < 0 || height < 0 || pixels.Length < width * height)
                throw new ArgumentOutOfRangeException(nameof(pixels));

            Palette = palette;
            PaletteCount = paletteCount;
            Pixels = pixels;
            Width = width;
            Height = height;
            CellWidth = cellWidth;
            CellHeight = cellHeight;
        }

        /// <summary>Quantize a BGRX image and wrap it as a sixel.</summary>
        public static Sixel CreateFromBitmap(byte[] bgrx, int width, int height, int cellWidth, int cellHeight)
        {
            ArgumentNullException.ThrowIfNull(bgrx);
            int pixelCount = width * height;
            byte[] indexed = GC.AllocateUninitializedArray<byte>(pixelCount);
            Quantize(bgrx, pixelCount, indexed, out byte[] palette, out int paletteCount);
            return new Sixel(palette, paletteCount, indexed, width, height, cellWidth, cellHeight);
        }

        #region Quantization

        [ThreadStatic] private static WuColorQuantizer? _quantizer;
        [ThreadStatic] private static Dictionary<uint, byte>? _exactMap;
        [ThreadStatic] private static byte[]? _wuInput;

        /// <summary>
        ///     Quantize <paramref name="pixelCount"/> BGRX pixels to at most 256 colors, writing one
        ///     index per pixel into <paramref name="indexed"/>. Images with 256 or fewer distinct
        ///     colors are indexed exactly; anything else goes through Wu's quantizer.
        /// </summary>
        /// <remarks>
        ///     Wu has a large fixed cost however few pixels it is given, while the cells a UI change
        ///     touches hold a handful of flat colors. Exact indexing is cheaper for them and loses nothing.
        /// </remarks>
        public static bool Quantize(byte[] bgrx, int pixelCount, byte[] indexed,
            out byte[] palette, out int paletteCount)
        {
            if (TryQuantizeExact(bgrx, pixelCount, indexed, out palette, out paletteCount))
                return true;
            QuantizeWu(bgrx, pixelCount, indexed, out palette, out paletteCount);
            return false;
        }

        /// <summary>
        ///     Index the pixels exactly when they hold 256 or fewer distinct colors. Returns
        ///     false, leaving <paramref name="indexed"/> unusable, as soon as a 257th color appears.
        /// </summary>
        public static bool TryQuantizeExact(byte[] bgrx, int pixelCount, byte[] indexed,
            out byte[] palette, out int paletteCount)
        {
            Validate(bgrx, pixelCount, indexed);
            return TryIndexExactly(bgrx, pixelCount, indexed, out palette, out paletteCount);
        }

        /// <summary>
        ///     Quantize to at most 256 colors with Wu's algorithm. Handles any number of colors,
        ///     at a large fixed cost that does not shrink with the image.
        /// </summary>
        public static void QuantizeWu(byte[] bgrx, int pixelCount, byte[] indexed,
            out byte[] palette, out int paletteCount)
        {
            Validate(bgrx, pixelCount, indexed);

            // Wu reads the whole array it is given, so hand it exactly pixelCount pixels.
            byte[] input;
            if (bgrx.Length == pixelCount * 4)
            {
                input = bgrx;
            }
            else
            {
                if (_wuInput == null || _wuInput.Length != pixelCount * 4)
                    _wuInput = GC.AllocateUninitializedArray<byte>(pixelCount * 4);
                input = _wuInput;
                Buffer.BlockCopy(bgrx, 0, input, 0, pixelCount * 4);
            }

            WuColorQuantizer quantizer = _quantizer ??= new WuColorQuantizer();
            ColorQuantizerResult result = quantizer.Quantize(input, 256);
            palette = result.Palette;
            paletteCount = palette.Length / 4;
            Buffer.BlockCopy(result.Bytes, 0, indexed, 0, pixelCount);
        }

        private static void Validate(byte[] bgrx, int pixelCount, byte[] indexed)
        {
            ArgumentNullException.ThrowIfNull(bgrx);
            ArgumentNullException.ThrowIfNull(indexed);
            if (bgrx.Length < pixelCount * 4 || indexed.Length < pixelCount)
                throw new ArgumentOutOfRangeException(nameof(pixelCount));
        }

        private static bool TryIndexExactly(byte[] bgrx, int pixelCount, byte[] indexed,
            out byte[] palette, out int paletteCount)
        {
            Dictionary<uint, byte> map = _exactMap ??= new Dictionary<uint, byte>(256);
            map.Clear();

            uint lastColor = 0;
            byte lastIndex = 0;
            bool haveLast = false;

            ref byte src = ref MemoryMarshal.GetArrayDataReference(bgrx);
            for (int i = 0, off = 0; i < pixelCount; i++, off += 4)
            {
                uint color = Unsafe.ReadUnaligned<uint>(ref Unsafe.Add(ref src, off)) & 0x00FFFFFFu;
                if (haveLast && color == lastColor)
                {
                    indexed[i] = lastIndex;
                    continue;
                }

                if (!map.TryGetValue(color, out byte index))
                {
                    if (map.Count == 256)
                    {
                        palette = Array.Empty<byte>();
                        paletteCount = 0;
                        return false;
                    }

                    index = (byte)map.Count;
                    map.Add(color, index);
                }

                indexed[i] = index;
                lastColor = color;
                lastIndex = index;
                haveLast = true;
            }

            paletteCount = map.Count;
            palette = new byte[paletteCount * 4];
            foreach (KeyValuePair<uint, byte> entry in map)
            {
                int po = entry.Value * 4;
                palette[po] = (byte)entry.Key;
                palette[po + 1] = (byte)(entry.Key >> 8);
                palette[po + 2] = (byte)(entry.Key >> 16);
                palette[po + 3] = 0xFF;
            }

            return true;
        }

        #endregion

        #region Serialization

        [ThreadStatic] private static byte[]? _usedScratch;
        [ThreadStatic] private static byte[]? _rowScratch;
        private byte[]? _renderedBytes;

        /// <summary>Render to a fresh array (cached on this instance). Prefer <see cref="RenderTo"/>.</summary>
        public ReadOnlySpan<byte> Render()
        {
            if (_renderedBytes != null) return _renderedBytes;
            var writer = new ArrayBufferWriter<byte>(4096);
            RenderTo(writer);
            _renderedBytes = writer.WrittenSpan.ToArray();
            return _renderedBytes;
        }

        /// <summary>
        ///     Write the DCS sixel sequence for this image into <paramref name="output"/>.
        ///     Only palette entries that appear in the pixels are defined.
        /// </summary>
        public void RenderTo(IBufferWriter<byte> output)
        {
            ArgumentNullException.ThrowIfNull(output);

            int width = Width;
            int height = Height;
            byte[] palette = Palette;
            int paletteCount = PaletteCount;
            byte[] indexed = Pixels;
            int pixelCount = width * height;

            // Which palette entries are used anywhere in the image
            byte[] used = _usedScratch ??= new byte[256];
            Array.Clear(used, 0, 256);
            int usedCount = 0;
            ref byte idxRef = ref MemoryMarshal.GetArrayDataReference(indexed);
            for (int i = 0; i < pixelCount; i++)
            {
                byte c = Unsafe.Add(ref idxRef, i);
                if (used[c] == 0) { used[c] = 1; usedCount++; }
            }

            // Header + palette definitions
            {
                Span<byte> span = output.GetSpan(16 + usedCount * 20 + 24);
                int pos = 0;
                span[pos++] = 0x1B; span[pos++] = (byte)'P'; span[pos++] = (byte)'q';
                span[pos++] = (byte)'"'; span[pos++] = (byte)'1'; span[pos++] = (byte)';';
                span[pos++] = (byte)'1'; span[pos++] = (byte)';';
                pos = WriteInt(span, pos, width);
                span[pos++] = (byte)';';
                pos = WriteInt(span, pos, height);

                for (int i = 0; i < paletteCount; i++)
                {
                    if (used[i] == 0) continue;
                    // Round rather than truncate: 254 truncated to 99% reads back as 252
                    int r = (palette[i * 4 + 2] * 100 + 127) / 255;
                    int g = (palette[i * 4 + 1] * 100 + 127) / 255;
                    int b = (palette[i * 4] * 100 + 127) / 255;
                    span[pos++] = (byte)'#'; pos = WriteInt(span, pos, i);
                    span[pos++] = (byte)';'; span[pos++] = (byte)'2'; span[pos++] = (byte)';';
                    pos = WriteInt(span, pos, r); span[pos++] = (byte)';';
                    pos = WriteInt(span, pos, g); span[pos++] = (byte)';';
                    pos = WriteInt(span, pos, b);
                }

                output.Advance(pos);
            }

            // Bands of six rows
            int bandCount = (height + 5) / 6;
            Span<bool> colorPresent = stackalloc bool[256];
            byte[]? sixelRow = _rowScratch;
            if (sixelRow == null || sixelRow.Length < width)
                _rowScratch = sixelRow = GC.AllocateUninitializedArray<byte>(Math.Max(width, 256));

            for (int band = 0; band < bandCount; band++)
            {
                int yStart = band * 6;
                int bandRows = Math.Min(6, height - yStart);

                colorPresent.Clear();
                int bandColors = 0;
                int bandStart = yStart * width;
                int bandEnd = bandStart + bandRows * width;
                for (int i = bandStart; i < bandEnd; i++)
                {
                    byte c = Unsafe.Add(ref idxRef, i);
                    if (!colorPresent[c]) { colorPresent[c] = true; bandColors++; }
                }

                // Worst case: every present color writes a full row of literals plus its header
                Span<byte> span = output.GetSpan(bandColors * (width + 8) + 8);
                int pos = 0;
                bool anyColor = false;
                for (int color = 0; color < paletteCount; color++)
                {
                    if (!colorPresent[color]) continue;
                    BuildSixelRow(indexed, sixelRow, width, yStart, bandRows, (byte)color);
                    if (anyColor) span[pos++] = (byte)'$';
                    span[pos++] = (byte)'#'; pos = WriteInt(span, pos, color);
                    pos = WriteRle(span, pos, sixelRow, width);
                    anyColor = true;
                }

                if (band < bandCount - 1) span[pos++] = (byte)'-';
                output.Advance(pos);
            }

            // ST
            {
                Span<byte> span = output.GetSpan(2);
                span[0] = 0x1B; span[1] = (byte)'\\';
                output.Advance(2);
            }
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
        private static int WriteInt(Span<byte> buf, int pos, int value)
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
        private static int WriteRle(Span<byte> output, int pos, byte[] data, int length)
        {
            int i = 0;
            while (i < length)
            {
                byte ch = data[i];
                int run = 1;
                while (i + run < length && data[i + run] == ch) run++;
                if (run >= 4) { output[pos++] = (byte)'!'; pos = WriteInt(output, pos, run); output[pos++] = ch; }
                else { for (int j = 0; j < run; j++) output[pos++] = ch; }
                i += run;
            }
            return pos;
        }

        #endregion
    }
}
