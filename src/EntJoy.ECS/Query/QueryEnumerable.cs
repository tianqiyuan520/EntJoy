using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using EntJoy.ECS.JobSystem;
using EntJoy.JobSystem;

namespace EntJoy.ECS
{
    public unsafe ref struct QueryEnumerable<T0, T1> where T0 : struct where T1 : struct
    {
        private readonly EntityManager _entityManager;
        private readonly QueryBuilder _builder;

        internal QueryEnumerable(EntityManager entityManager, QueryBuilder builder)
        {
            _entityManager = entityManager;
            _builder = builder;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public QueryEnumerator<T0, T1> GetEnumerator()
            => new QueryEnumerator<T0, T1>(_entityManager, _builder);
    }

    // 实体级迭代：archetype → chunk → slot。每 chunk 提升组件基址一次（等价 IJobChunk 循环形态），
    // 循环内逐 slot 产出 ref 组件对，密集 OOD 访问零间接、与 class 持平。
    // 支持 AllEnabled 过滤（.WithEnabled<T1>()）：构建时计算组合位图，MoveNext 跳过禁用 slot。
    public unsafe ref struct QueryEnumerator<T0, T1>
        where T0 : struct
        where T1 : struct
    {
        private readonly EntityManager _entityManager;
        private readonly QueryBuilder _builder;
        private readonly ComponentType[] _allEnabledTypes;
        private int _archIndex;
        private int _chunkIndex;
        private int _slotIndex;
        private int _count;
        private Archetype _currentArch;
        private Chunk _currentChunk;
        private int _t0Idx;
        private int _t1Idx;
        private T0* _t0Base;
        private T1* _t1Base;

        // AllEnabled 组合位图（无过滤时为 null）
        private ulong* _combinedMask;
        private int _ulongCount;

        internal QueryEnumerator(EntityManager entityManager, QueryBuilder builder)
        {
            _entityManager = entityManager;
            _builder = builder;
            _allEnabledTypes = builder.AllEnabled;
            _archIndex = 0;
            _chunkIndex = 0;
            _slotIndex = 0;
            _count = 0;
            _currentArch = null;
            _currentChunk = default;
            _t0Idx = -1;
            _t1Idx = -1;
            _t0Base = null;
            _t1Base = null;
            _combinedMask = null;
            _ulongCount = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool MoveNextArchetype()
        {
            while (_archIndex < _entityManager.ArchetypeCount)
            {
                var arch = _entityManager.Archetypes[_archIndex];
                _archIndex++;
                if (arch != null && arch.IsMatch(_builder))
                {
                    _currentArch = arch;
                    _t0Idx = arch.GetComponentTypeIndex<T0>();
                    _t1Idx = arch.GetComponentTypeIndex<T1>();
                    _chunkIndex = 0;
                    return MoveNextChunk();
                }
            }
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool MoveNextChunk()
        {
            if (_currentArch == null) return false;
            var chunks = _currentArch.ChunkList;
            while (_chunkIndex < chunks.Count)
            {
                var chunk = chunks[_chunkIndex];
                _chunkIndex++;
                if (chunk.EntityCount > 0)
                {
                    _currentChunk = chunk;
                    _count = chunk.EntityCount;
                    _t0Base = (T0*)chunk.GetComponentArrayPointer(_t0Idx);
                    _t1Base = (T1*)chunk.GetComponentArrayPointer(_t1Idx);
                    _slotIndex = 0;

                    // 有 AllEnabled 过滤：计算组合位图；无交集则跳过此 Chunk
                    if (_allEnabledTypes != null && _allEnabledTypes.Length > 0)
                    {
                        if (!ComputeCombinedMask(chunk))
                        {
                            _combinedMask = null;
                            continue;
                        }
                    }
                    else
                    {
                        _combinedMask = null;
                    }

                    return true;
                }
            }
            _currentChunk = default;
            return MoveNextArchetype();
        }

        /// <summary>
        /// 计算 AllEnabled 组合位图（SIMD AND + 提前退出）。
        /// 返回 false 表示无交集（Chunk 中没有任何实体同时启用所有 AllEnabled 组件）。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool ComputeCombinedMask(Chunk chunk)
        {
            _ulongCount = (chunk.EntityCount + 63) / 64;
            ulong* combinedMask = TempBuffer.GetBuffer(_ulongCount);
            _combinedMask = combinedMask;
            for (int i = 0; i < _ulongCount; i++) combinedMask[i] = 0;

            bool firstFound = false;
            var archetype = chunk.Archetype;
            foreach (var type in _allEnabledTypes)
            {
                int componentIndex = archetype.GetComponentTypeIndex(type);
                if (componentIndex < 0) continue;
                ulong* bitmap = chunk.GetEnableBitMapPointer(componentIndex);
                if (bitmap == null) continue;

                if (!firstFound)
                {
                    for (int i = 0; i < _ulongCount; i++)
                        combinedMask[i] = bitmap[i];
                    firstFound = true;
                }
                else
                {
                    // SIMD 批量 AND + 提前退出
                    if (Avx2.IsSupported && _ulongCount >= 4)
                    {
                        int i = 0;
                        var orResult = Vector256<ulong>.Zero;

                        for (; i <= _ulongCount - 4; i += 4)
                        {
                            var a = Avx.LoadVector256(combinedMask + i);
                            var b = Avx.LoadVector256(bitmap + i);
                            var andResult = Avx2.And(a, b);
                            orResult = Avx2.Or(orResult, andResult);
                        }

                        bool hasIntersection = !Avx.TestZ(orResult, orResult);

                        for (; i < _ulongCount && !hasIntersection; i++)
                        {
                            combinedMask[i] &= bitmap[i];
                            hasIntersection = combinedMask[i] != 0;
                        }

                        if (hasIntersection)
                        {
                            for (; i < _ulongCount; i++)
                                combinedMask[i] &= bitmap[i];
                        }
                        else
                        {
                            return false;
                        }
                    }
                    else
                    {
                        bool hasAny = false;
                        for (int i = 0; i < _ulongCount; i++)
                        {
                            combinedMask[i] &= bitmap[i];
                            if (combinedMask[i] != 0) hasAny = true;
                        }
                        if (!hasAny) return false;
                    }
                }
            }

            return firstFound;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext()
        {
            // 已有当前 Chunk：从 slot+1 继续；否则从位置 0 开始
            if (_currentChunk.MemoryBlock != nint.Zero)
                _slotIndex++;

            return Advance();
        }

        /// <summary>
        /// 从当前 _slotIndex 起查找下一个满足条件的实体；跨 Chunk/Archetype 自动推进。
        /// 有 AllEnabled 过滤时每个 Chunk 的 slot 0 都经过位图检查（无漏查）。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool Advance()
        {
            while (true)
            {
                // 定位到有效 Chunk
                if (_currentArch == null)
                {
                    if (!MoveNextArchetype())
                        return false;
                }
                else if (_currentChunk.MemoryBlock == nint.Zero)
                {
                    if (!MoveNextChunk())
                        return false;
                }

                if (_combinedMask != null)
                {
                    // 启用过滤：位图遍历
                    while (_slotIndex < _count)
                    {
                        int ulongIdx = _slotIndex >> 6;
                        if (ulongIdx >= _ulongCount)
                        {
                            _slotIndex = _count;
                            break;
                        }

                        int bitOffset = _slotIndex & 63;
                        ulong mask = _combinedMask[ulongIdx] >> bitOffset;

                        if (mask != 0)
                        {
                            int bitIndex = BitOperations.TrailingZeroCount(mask);
                            _slotIndex += bitIndex;
                            if (_slotIndex < _count)
                                return true;
                            break;
                        }

                        _slotIndex = (ulongIdx + 1) << 6;
                    }
                }
                else
                {
                    // 无过滤：普通遍历
                    if (_slotIndex < _count)
                        return true;
                }

                // 当前 Chunk 耗尽 → 下一 Chunk（MoveNextChunk 重置 _slotIndex=0 并重新检查）
                _currentChunk = default;
            }
        }

        public EntityQueryResult<T0, T1> Current
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                if (_currentChunk.MemoryBlock == nint.Zero) throw new InvalidOperationException();
                return new EntityQueryResult<T0, T1>(_t0Base + _slotIndex, _t1Base + _slotIndex);
            }
        }
    }

    // 直接持有裸指针(非 ref 字段):避免 readonly ref struct 的 ref 存活跟踪,
    // 让 JIT 能把这 2 个指针放进寄存器、彻底内联进 foreach 循环体。
    public unsafe readonly struct EntityQueryResult<T0, T1> where T0 : struct where T1 : struct
    {
        private readonly T0* _c0;
        private readonly T1* _c1;

        public EntityQueryResult(T0* c0, T1* c1)
        {
            _c0 = c0;
            _c1 = c1;
        }

        public ref T0 Comp0
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => ref *_c0;
        }

        public ref T1 Comp1
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => ref *_c1;
        }
    }

}