using System;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Input;
using Avalonia.Input.Platform;

namespace Iciclecreek.Avalonia.SixelBuffer.Platform
{
    /// <summary>
    /// Terminal clipboard implementation using OSC 52 escape sequences.
    /// Falls back to in-memory storage when OSC 52 read is not supported.
    /// </summary>
    internal sealed class TerminalClipboard : IClipboardImpl
    {
        private IAsyncDataTransfer? _stored;

        public Task SetDataAsync(IAsyncDataTransfer dataTransfer)
        {
            _stored = dataTransfer;

            // Try to push text to the system clipboard via OSC 52.
            _ = TryWriteOsc52Async(dataTransfer);

            return Task.CompletedTask;
        }

        public Task<IAsyncDataTransfer?> TryGetDataAsync()
        {
            // OSC 52 read is rarely supported by terminals, so return the
            // in-memory copy. This covers the common copy-then-paste-within-app flow.
            return Task.FromResult(_stored);
        }

        public Task ClearAsync()
        {
            _stored?.Dispose();
            _stored = null;

            // OSC 52 clear: set clipboard to empty string
            Console.Write("\x1b]52;c;\x07");

            return Task.CompletedTask;
        }

        private static async Task TryWriteOsc52Async(IAsyncDataTransfer dataTransfer)
        {
            try
            {
                // Extract text from the first item that supports the Text format.
                foreach (var item in dataTransfer.Items)
                {
                    foreach (var format in item.Formats)
                    {
                        if (DataFormat.Text.Equals(format))
                        {
                            var text = await item.TryGetValueAsync(DataFormat.Text);
                            if (text != null)
                            {
                                var base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
                                // OSC 52 ; c (clipboard) ; <base64-data> ST
                                Console.Write($"\x1b]52;c;{base64}\x07");
                            }
                            return;
                        }
                    }
                }
            }
            catch
            {
                // Terminal may not support OSC 52; silently ignore.
            }
        }
    }
}
