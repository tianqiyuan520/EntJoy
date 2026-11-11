using System.Runtime.CompilerServices;
using System;

namespace EntJoy.ECS
{
    public static partial class Utils
    {
        /// <summary>与顺序无关的集合哈希，只混入 Id（与非排序查找的相等判定保持一致）。
        /// 每项先过 splitmix32 终混再累加：直接累加 Id 会让 id 之和相等的集合恒碰撞（如 {1,4} 与 {2,3}）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int CalculateUnorderedHash(ReadOnlySpan<ComponentType> types)
        {
            uint sum = 2166136261u ^ (uint)types.Length;
            for (int i = 0; i < types.Length; i++)
                sum += Mix((uint)types[i].Id);
            return (int)(sum * 16777619u);
        }

        // splitmix32 终混：把相邻 Id 打散到整个 32 位空间，累加不再等价于累加 Id。
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static uint Mix(uint value)
        {
            value ^= value >> 16;
            value *= 0x7feb352du;
            value ^= value >> 15;
            value *= 0x846ca68bu;
            value ^= value >> 16;
            return value;
        }

        /// <summary>按 Id 做无序多重集比较（与 Archetype 存储顺序无关）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool SetEquals(ReadOnlySpan<ComponentType> a, ReadOnlySpan<ComponentType> b)
        {
            if (a.Length != b.Length) return false;
            // 常见情形组件很少：用寄存器里的位掩码记录已匹配项，不碰栈缓冲。
            if (b.Length <= 16)
            {
                uint used = 0;
                for (int i = 0; i < b.Length; i++)
                {
                    bool found = false;
                    for (int j = 0; j < a.Length; j++)
                    {
                        if ((used & (1u << j)) == 0 && a[j].Id == b[i].Id)
                        {
                            used |= 1u << j;
                            found = true;
                            break;
                        }
                    }
                    if (!found) return false;
                }
                return true;
            }
            Span<bool> taken = stackalloc bool[a.Length];
            for (int i = 0; i < b.Length; i++)
            {
                bool found = false;
                for (int j = 0; j < a.Length; j++)
                {
                    if (!taken[j] && a[j].Id == b[i].Id)
                    {
                        taken[j] = true;
                        found = true;
                        break;
                    }
                }
                if (!found) return false;
            }
            return true;
        }
        /// <summary>
        /// 插入排序 升序
        /// </summary>
        public static void SortByInsert(Span<ComponentType> span)
        {
            int length = span.Length;
            for (int i = 1; i < length; i++)
            {
                ComponentType cur = span[i];
                int j = i - 1;
                while (j >= 0 && span[j].Id > cur.Id)  // 若前者的ID大于后者，则 前移指针
                {
                    span[j + 1] = span[j]; // 元素后移
                    j--;
                }
                span[j + 1] = cur;  // 将当前元素放到正确位置
            }
        }
    }
}
