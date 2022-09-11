using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Videcoder
{
    internal unsafe readonly struct Pointer<T> where T : unmanaged
    {
        internal readonly T* Ptr { get; init; }
        public Pointer(T* ptr)
        {
            Ptr = ptr;
        }

        public static implicit operator T*(Pointer<T> pointer)
        {
            return pointer.Ptr;
        }

        public static implicit operator Pointer<T>(T* ptr)
        {
            return new Pointer<T>(ptr);
        }

    }
}
