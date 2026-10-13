using System;
using System.Buffers;
using System.Buffers.Text;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text.Unicode;

namespace Iciclecreek.Avalonia.SixelBuffer.Rendering
{
    /// <summary>
    ///     Kitty graphics protocol output: a block of pixels transmitted and shown in one command,
    ///     under an image id. Re-transmitting an id replaces the image the terminal holds and every
    ///     placement of it, which is what lets a screen be kept as a fixed grid of blocks that are
    ///     simply sent again when they change.
    /// </summary>
    internal static class KittyGraphics
    {
        // The protocol caps a chunk's base64 payload at 4096 characters; whole 3-byte groups so
        // every chunk's base64 stands on its own.
        private const int MaxChunkBase64 = 4096;
        private const int RawChunkSize = MaxChunkBase64 / 4 * 3;

        /// <summary>
        ///     Write the transmit-and-display command(s) for an RGB image compressed with zlib.
        ///     q=2 asks for no reply, C=1 leaves the cursor where it is: the caller positions it
        ///     before the image and the terminal does not answer every block of a frame.
        /// </summary>
        public static void WriteTransmit(IBufferWriter<byte> output, int imageId, int width, int height, ReadOnlySpan<byte> zlibRgb)
        {
            if (zlibRgb.Length == 0) return;

            int offset = 0;
            bool first = true;
            while (offset < zlibRgb.Length)
            {
                int chunk = Math.Min(RawChunkSize, zlibRgb.Length - offset);
                bool last = offset + chunk >= zlibRgb.Length;

                Span<byte> span = output.GetSpan(96 + (chunk + 2) / 3 * 4 + 8);
                int pos = 0;
                "\u001b_G"u8.CopyTo(span);
                pos += 3;

                if (first)
                {
                    Utf8.TryWrite(span[pos..], CultureInfo.InvariantCulture,
                        $"a=T,f=24,o=z,q=2,C=1,i={imageId},s={width},v={height},", out int headerLength);
                    pos += headerLength;
                    first = false;
                }

                (last ? "m=0;"u8 : "m=1;"u8).CopyTo(span[pos..]);
                pos += 4;

                Base64.EncodeToUtf8(zlibRgb.Slice(offset, chunk), span[pos..], out _, out int encoded);
                pos += encoded;

                "\u001b\\"u8.CopyTo(span[pos..]);
                pos += 2;

                output.Advance(pos);
                offset += chunk;
            }
        }

        /// <summary>
        ///     Compress a BGRA block as zlib RGB. UI content is mostly flat runs of color, which
        ///     deflate at its fastest level packs to a small fraction of the raw bytes.
        /// </summary>
        public static byte[] CompressRgb(ReadOnlySpan<byte> bgra, int width, int height, ref byte[]? rgbScratch, MemoryStream zlibScratch)
        {
            int pixelCount = width * height;
            int rgbLength = pixelCount * 3;
            if (rgbScratch == null || rgbScratch.Length < rgbLength)
                rgbScratch = GC.AllocateUninitializedArray<byte>(rgbLength);

            byte[] rgb = rgbScratch;
            for (int i = 0, src = 0, dst = 0; i < pixelCount; i++, src += 4, dst += 3)
            {
                rgb[dst] = bgra[src + 2];
                rgb[dst + 1] = bgra[src + 1];
                rgb[dst + 2] = bgra[src];
            }

            zlibScratch.SetLength(0);
            using (var zlib = new ZLibStream(zlibScratch, CompressionLevel.Fastest, leaveOpen: true))
                zlib.Write(rgb, 0, rgbLength);

            return zlibScratch.ToArray();
        }
    }
}
