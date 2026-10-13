using System;
using System.Collections.Generic;
using System.IO.Hashing;

namespace Iciclecreek.Avalonia.SixelBuffer.Rendering
{
    /// <summary>Identifies a run of pixels by its content: an XXH3-128 digest plus its size.</summary>
    internal readonly record struct ContentKey(UInt128 Digest, int Width, int Height)
    {
        public static ContentKey Of(ReadOnlySpan<byte> bytes, int width, int height)
            => new(XxHash128.HashToUInt128(bytes), width, height);
    }

    /// <summary>
    ///     Least-recently-used cache of rendered sixel byte strings keyed by the pixels they
    ///     encode. Re-exposed content (a closed popup, a hover that reverts, a tab switch)
    ///     replays the bytes the terminal was already sent, with no quantizing or encoding.
    ///     Not thread-safe: the render thread owns it.
    /// </summary>
    internal sealed class ContentCache
    {
        private readonly Dictionary<ContentKey, LinkedListNode<Entry>> _map = new();
        private readonly LinkedList<Entry> _recency = new();
        private readonly long _budgetBytes;
        private long _usedBytes;

        public ContentCache(long budgetBytes) => _budgetBytes = budgetBytes;

        public int Count => _map.Count;
        public long UsedBytes => _usedBytes;

        public bool TryGet(in ContentKey key, out byte[] value)
        {
            if (_map.TryGetValue(key, out LinkedListNode<Entry>? node))
            {
                _recency.Remove(node);
                _recency.AddFirst(node);
                value = node.Value.Bytes;
                return true;
            }

            value = Array.Empty<byte>();
            return false;
        }

        public void Add(in ContentKey key, byte[] bytes)
        {
            if (_map.TryGetValue(key, out LinkedListNode<Entry>? existing))
            {
                _usedBytes -= existing.Value.Bytes.Length;
                _recency.Remove(existing);
                _map.Remove(key);
            }

            var node = new LinkedListNode<Entry>(new Entry(key, bytes));
            _recency.AddFirst(node);
            _map[key] = node;
            _usedBytes += bytes.Length;

            // Evict from the tail while over budget, never the entry just added
            while (_usedBytes > _budgetBytes && _recency.Count > 1)
            {
                LinkedListNode<Entry> last = _recency.Last!;
                _recency.RemoveLast();
                _map.Remove(last.Value.Key);
                _usedBytes -= last.Value.Bytes.Length;
            }
        }

        public void Clear()
        {
            _map.Clear();
            _recency.Clear();
            _usedBytes = 0;
        }

        private readonly record struct Entry(ContentKey Key, byte[] Bytes);
    }
}
