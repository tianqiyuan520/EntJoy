using System;
using System.Runtime.CompilerServices;

namespace EntJoy
{
    public unsafe ref struct BitMask
    {
        private readonly ulong* _bits;      // 指向 ulong 位图数组的指针
        private readonly int _length;        // 实体数量

        internal BitMask(ulong* bits, int length)
        {
            _bits = bits;
            _length = length;
        }

        public int Length => _length;

        public bool this[int index]
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                if ((uint)index >= (uint)_length) throw new IndexOutOfRangeException();
                int ulongIndex = index >> 6;
                int bitOffset = index & 63;
                return (_bits[ulongIndex] & (1UL << bitOffset)) != 0;
            }
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            set
            {
                if ((uint)index >= (uint)_length) throw new IndexOutOfRangeException();
                int ulongIndex = index >> 6;
                int bitOffset = index & 63;
                if (value)
                    _bits[ulongIndex] |= 1UL << bitOffset;
                else
                    _bits[ulongIndex] &= ~(1UL << bitOffset);
            }
        }

        public ulong* UnsafePtr => _bits;
    }
}