using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace EntJoy.ECS
{
    public unsafe ref struct ChunkEnabledMask
    {
        private readonly Span<ulong> _bits;
        private readonly int _length;
        private readonly int _ulongCount;    // 位图占用的 ulong 数量

        internal ChunkEnabledMask(Span<ulong> bits, int length)
        {
            _bits = bits;
            _length = length;
            _ulongCount = (length + 63) / 64;
        }

        public int Length => _length;

        /// <summary>组合位图 ulong 数量（实体数按 64 向上取整）。</summary>
        public int UlongCount => _ulongCount;

        internal bool HasMatches
        {
            get
            {
                if (_length == 0) return true;
                for (int i = 0; i < _ulongCount; i++)
                {
                    ulong word = _bits[i];
                    if (i == _ulongCount - 1 && (_length & 63) != 0) word &= (1UL << (_length & 63)) - 1;
                    if (word != 0) return true;
                }
                return false;
            }
        }

        /// <summary>
        /// 组合位图（每 bit 一个实体，位 0 = 实体 0）。
        /// 供生成代码/用户内联 BitOperations 遍历；无过滤时为 default（Length 为 0，应遍历全部实体）。
        /// </summary>
        public Span<ulong> Bits
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _bits;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryGetNextRange(ref int start, out int rangeStart, out int rangeEnd)
        {
            if (start < 0) throw new ArgumentOutOfRangeException(nameof(start));
            if (start >= _length || !TryFindEnabledBit(start, out int pos))
            {
                start = Math.Min(start, _length);
                rangeStart = rangeEnd = 0;
                return false;
            }
            rangeStart = pos;
            rangeEnd = Math.Min(pos + CountTrailingOnes(_bits[pos >> 6] >> (pos & 63)), _length);
            start = rangeEnd;
            return true;
        }

        // 从 pos 起找第一个启用位；跨过整字为 0 的区间，找不到时返回 false。
        private bool TryFindEnabledBit(int pos, out int found)
        {
            int ulongIdx = pos >> 6;
            ulong current = _bits[ulongIdx] >> (pos & 63);
            if (current == 0)
            {
                if (!TrySkipEmptyWords(ref ulongIdx, out current)) { found = 0; return false; }
                pos = ulongIdx * 64;
            }
            found = pos + BitOperations.TrailingZeroCount(current);
            return found < _length;
        }

        // 前进到下一个非零字；越过最后一个字表示后面没有任何启用位。
        private bool TrySkipEmptyWords(ref int ulongIdx, out ulong word)
        {
            ulongIdx++;
            while (ulongIdx < _ulongCount && _bits[ulongIdx] == 0) ulongIdx++;
            word = ulongIdx < _ulongCount ? _bits[ulongIdx] : 0;
            return ulongIdx < _ulongCount;
        }

        private static int CountTrailingOnes(ulong value)
            => BitOperations.TrailingZeroCount(~value);

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
