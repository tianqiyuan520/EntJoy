using System;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace EntJoy.ECS
{
    /// <summary>供运行时和生成的查询共用：返回当前启用位连续区间的结束位置。</summary>
    public static unsafe class QueryEntityRange
    {
        /// <summary>逐字合并启用条件；枚举器独立保存当前字，不分配组合位图。</summary>
        public static ulong EnabledWord(Chunk chunk, ComponentType[] types, int wordIndex)
        {
            ulong result = ulong.MaxValue;
            bool found = false;
            foreach (var type in types)
            {
                int index = chunk.Archetype.GetComponentTypeIndex(type);
                if (index < 0) continue;
                ulong* bitmap = chunk.GetEnableBitMapPointer(index);
                if (bitmap == null) continue;
                result &= bitmap[wordIndex];
                found = true;
                if (result == 0) break;
            }
            return found ? result : 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int End(ulong mask, int start, int count)
        {
            int offset = start & 63;
            int length = Math.Min(64 - offset, count - start);
            return start + Math.Min(length, BitOperations.TrailingZeroCount(~(mask >> offset)));
        }
    }
}
