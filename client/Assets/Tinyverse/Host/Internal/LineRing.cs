using System;
using System.Collections.Generic;

namespace Tinyverse.Host.Internal
{
    /// <summary>A fixed-capacity ring of the most recent lines. Not thread-safe; the owner locks.</summary>
    internal sealed class LineRing
    {
        private readonly string[] _items;
        private int _start;
        private int _count;

        public LineRing(int capacity)
        {
            if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
            _items = new string[capacity];
        }

        public int Capacity => _items.Length;
        public int Count => _count;

        public void Add(string line)
        {
            if (_count < _items.Length)
            {
                _items[(_start + _count) % _items.Length] = line;
                _count++;
            }
            else
            {
                _items[_start] = line;
                _start = (_start + 1) % _items.Length;
            }
        }

        /// <summary>The last <paramref name="max"/> lines, oldest first.</summary>
        public List<string> Tail(int max)
        {
            int n = Math.Min(Math.Max(0, max), _count);
            var result = new List<string>(n);
            for (int i = _count - n; i < _count; i++)
                result.Add(_items[(_start + i) % _items.Length]);
            return result;
        }

        public void Clear()
        {
            Array.Clear(_items, 0, _items.Length);
            _start = 0;
            _count = 0;
        }
    }
}
