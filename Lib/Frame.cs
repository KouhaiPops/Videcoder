using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Videcoder
{
    public readonly struct Frame
    {
        public readonly ReadOnlyMemory<byte> data;
        public readonly double timestamp;
        public readonly int height;
        public readonly int width;

        public Frame(ReadOnlyMemory<byte> data, double timestamp, int height, int width)
        {
            this.data = data;
            this.timestamp = timestamp;
            this.height = height;
            this.width = width;
        }
        
        /// <summary>
        /// Get an array reference from the underlying <see cref="ReadOnlyMemory{T}"/><br/>
        /// The array shouldn't be treated as readonly
        /// </summary>
        /// <returns></returns>
        public byte[] DataArray()
        {
            MemoryMarshal.TryGetArray(data, out var segment);
            return segment.Array;
        }
    }

}
