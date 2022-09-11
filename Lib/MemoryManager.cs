using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.ConstrainedExecution;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace Videcoder
{
    sealed internal class MemoryManager<T> : ArrayPool<T> where T : unmanaged 
    {
        private class GcCallback : CriticalFinalizerObject
        {
            private readonly Action callback;
            private GCHandle handle;
            public GcCallback(Action callback, object obj)
            {
                this.callback = callback;
                handle = GCHandle.Alloc(obj, GCHandleType.Weak);

            }
            
            public static void Register(Action callback, object obj)
            {
                _ = new GcCallback(callback, obj);
            }

            ~GcCallback() {
                if(handle.IsAllocated && handle.Target != null)
                {
                    callback();
                    GC.ReRegisterForFinalize(this);
                }
            }
        }
        private struct CacheArray
        {
            public T[] Array;
#if DEBUG
            internal int rentCount;
#endif
            public CacheArray(T[] array)
            {
                Array = array;
#if DEBUG
                rentCount = 0;
#endif
            }
        }

        private int index;
        
        [ThreadStatic]
        private CacheArray[] cachedArrays;

        public int MaxArrayCount { get; }

        public MemoryManager(int maxArrayCount = 6)
        {
            MaxArrayCount = maxArrayCount;
        }

        private void Reset()
        {
            cachedArrays = new CacheArray[MaxArrayCount];
        }


        public override T[] Rent(int minimumLength)
        {
            if (cachedArrays == null)
            {
                cachedArrays = new CacheArray[MaxArrayCount];
                GcCallback.Register(Reset, this);
            }

            if (index >= MaxArrayCount)
            {
                index = 0;
            }
            if (cachedArrays[index].Array == null || cachedArrays[index].Array.Length < minimumLength)
            {
                cachedArrays[index].Array = new T[minimumLength];
            }
#if DEBUG
            cachedArrays[index].rentCount++;
#endif
            return cachedArrays[index++].Array;
        }

        public override void Return(T[] array, bool clearArray = false)
        {
            if(clearArray)
            {
                for (int i = 0; i < array.Length; i++)
                    array[i] = default;
            }
        }
    }

}
