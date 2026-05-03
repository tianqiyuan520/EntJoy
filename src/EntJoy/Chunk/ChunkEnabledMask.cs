using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace EntJoy
{
    public unsafe ref struct ChunkEnabledMask
    {
        private readonly ulong* _bits;
        private readonly int _length;
        private readonly int _ulongCount;    // 位图占用的 ulong 数量

        internal ChunkEnabledMask(ulong* bits, int length)
        {
            _bits = bits;
            _length = length;
            _ulongCount = (length + 63) / 64;
        }

        public int Length => _length;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetNextRange(ref int start, out int rangeStart, out int rangeEnd)
        {
            if (start >= _length)
            {
                rangeStart = rangeEnd = 0;
                return false;
            }

            // 找到第一个启用的实体
            int pos = start;
            int ulongIdx = pos >> 6;
            int bitOffset = pos & 63;
            ulong current = _bits[ulongIdx] >> bitOffset;

            // 跳过当前 ulong 中从 pos 开始的连续禁用位
            if (current == 0)
            {
                // 当前剩余部分全为 0，直接跳到下一个 ulong
                pos = (ulongIdx + 1) * 64;
                ulongIdx++;
                while (ulongIdx < _ulongCount && _bits[ulongIdx] == 0)
                {
                    ulongIdx++;
                }
                if (ulongIdx >= _ulongCount)
                {
                    rangeStart = rangeEnd = 0;
                    return false;
                }
                pos = ulongIdx * 64;
                bitOffset = 0;
                current = _bits[ulongIdx];
            }

            // 找到第一个启用位的位置
            int firstOne = BitOperations.TrailingZeroCount(current);
            pos += firstOne;
            rangeStart = pos;

            // 找到连续启用的结束
            current >>= firstOne;
            int ones = BitOperations.TrailingZeroCount(~current);
            rangeEnd = pos + ones;

            start = rangeEnd;
            return true;
        }

        // 逐位访问
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool IsEnabled(int index)
        {
            if ((uint)index >= (uint)_length) throw new IndexOutOfRangeException();
            int ulongIdx = index >> 6;
            int bit = index & 63;
            return (_bits[ulongIdx] & 1UL << bit) != 0;
        }
    }
}