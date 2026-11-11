using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using EntJoy.Collections;
using EntJoy.ECS.JobSystem;
using EntJoy.JobSystem;

namespace EntJoy.ECS
{
    public unsafe partial class EntityManager : IDisposable
    {
        private Dictionary<int, Archetype> archetypeMap; // 原型映射表（集合哈希 -> 原型；哈希碰撞时线性兜底）
        private Archetype[] allArchetypes;  // 所有原型数组

        private int archetypeCount;
        private int archetypeSetVersion;
        public int ArchetypeCount
        {
            get { return archetypeCount; }
            set
            {
                if (archetypeCount != value) archetypeSetVersion++;
                archetypeCount = value;
            }
        }

        /// <summary>
        /// Archetype 集合身份版本：新建、清空、恢复时递增。查询的增量刷新必须用它判「匹配集合能否复用」，
        /// 不能只比数量：数量相同而身份不同（Restore 后重建、或一增一删抵消）时会复用已释放的原型。
        /// </summary>
        internal int ArchetypeSetVersion => archetypeSetVersion;
        public ref readonly Archetype[] Archetypes => ref allArchetypes;

        /// <summary>实体回收队列（对象池）</summary>
        /// <summary>
        /// 实体 Id 回收栈（非托管 LIFO，最近销毁的 Id 优先复用），创建与销毁路径零托管分配。
        /// </summary>
        private unsafe Entity* _recycleStack;
        private int _recycleCount;
        private int _recycleCapacity;

        private unsafe bool TryPopRecycled(out Entity entity)
        {
            if (_recycleCount > 0)
            {
                entity = _recycleStack[--_recycleCount];
                return true;
            }
            entity = default;
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private unsafe void PushRecycled(Entity entity)
        {
            // 满容才走扩容：把分配/搬运隔离到不进内联的慢路径，热路径只剩比较加一次写。
            if (_recycleCount == _recycleCapacity) GrowRecycleStack(_recycleCount + 1);
            _recycleStack[_recycleCount++] = entity;
        }

        private void ClearRecycled() => _recycleCount = 0;

        [MethodImpl(MethodImplOptions.NoInlining)]
        private unsafe void GrowRecycleStack(int needed)
        {
            // 容量够就直接返回：预留路径（关系删除前一次性预留）不改动已分配块。
            if (needed <= _recycleCapacity) return;
            int newCapacity = _recycleCapacity > 0 ? _recycleCapacity : 1024;
            while (newCapacity < needed) newCapacity *= 2;
            var next = (Entity*)System.Runtime.InteropServices.Marshal.AllocHGlobal(newCapacity * sizeof(Entity));
            if (_recycleStack != null)
            {
                Buffer.MemoryCopy(_recycleStack, next, (long)newCapacity * sizeof(Entity), (long)_recycleCount * sizeof(Entity));
                System.Runtime.InteropServices.Marshal.FreeHGlobal((IntPtr)_recycleStack);
            }
            _recycleStack = next;
            _recycleCapacity = newCapacity;
        }

        /// <summary>实体索引数组（直接索引访问）</summary>
        private EntityIndexInWorld[] entities;  // 实体索引数组

        /// <summary>
        /// blittable 实体定位表（索引 = 实体 Id），与 <see cref="entities"/> 在同一点更新：
        /// 每项记录 chunk 基址 + 该原型的非托管列偏移表 + slot + version，供并行 job 与原生内核跨块随机访问。
        /// 容量随 <see cref="entities"/> 扩容（见 <see cref="EnsureLocateCapacity"/>）。
        /// </summary>
        private unsafe EntityLocateB* _locateB;

        /// <summary><see cref="_locateB"/> 的容量（= entities.Length；0 表示尚未分配）。</summary>
        private int _locateBCapacity;

        /// <summary>下一个 Archetype 编号（<see cref="Archetype.ArchetypeId"/> 的分配源）。</summary>
        private int _nextArchetypeId;

        /// <summary>当前已创建的实体总数</summary>
        private int entityCount;  // 实体计数器
        /// <summary>
        /// 语义是已发放的 id 计数（单调递增，`newEntity.Id = entityCount++`），不是存活实体数：
        /// Destroy 不会让它下降。存活数请看 <see cref="LiveEntityCount"/>。
        /// </summary>
        public int EntityCount => entityCount;

        /// <summary>当前存活实体数：按各 Archetype 的实际计数求和（Destroy 后会下降）。</summary>
        internal int LiveEntityCount
        {
            get
            {
                int total = 0;
                for (int i = 0; i < archetypeCount; i++)
                {
                    var a = allArchetypes[i];
                    if (a != null) total += a.EntityCount;
                }
                return total;
            }
        }
        private int structuralVersion;
        public int StructuralVersion => structuralVersion;
        private readonly QueryBorrowVersion _structuralQueryVersion = new();
        private readonly QueryBorrowVersion _relationshipQueryVersion = new();

        internal QueryBorrowVersion GetQueryBorrowVersion(bool relationships)
        {
            CheckDisposed();
            return relationships ? _relationshipQueryVersion : _structuralQueryVersion;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void InvalidateStructuralQueries()
        {
            structuralVersion++;
            _structuralQueryVersion.Invalidate();
            // 没有任何关系数据时关系查询结果不受影响，省掉一次令牌失效。
            if (!_relationIndex.IsEmpty) _relationshipQueryVersion.Invalidate();
        }

        private void InvalidateRelationshipQueries(int changes = 1)
        {
            if (changes == 0) return;
            _relationshipVersion += changes;
            _relationshipQueryVersion.Invalidate();
        }

        private bool _disposed;
        private readonly object _activeJobLock = new();
        private readonly List<JobHandle> _activeJobs = new();
        private readonly List<JobHandle> _completedJobs = new();
        // archetype 归属未知的 Job（无法证明它不触碰目标 archetype）， 结构性变更必须保守等待。
        // 现实来源：native entity-batch（IJobEntity/IJobChunk 的 raw 路径）注册时不带 archetypes。
        private readonly List<JobHandle> _unscopedJobs = new();
        private readonly Dictionary<int, List<JobHandle>> _archetypeJobs = new(); // Per-Archetype Job Tracking
        private readonly Dictionary<JobHandle, ComponentType[]> _jobWrittenComponents = new(); // Job → written components
        private readonly object _structuralLock = new();  // 结构变更互斥（Monitor 可重入，回调内嵌套也安全）

        private readonly struct StructuralScope : System.IDisposable
        {
            private readonly EntityManager _owner;
            public StructuralScope(EntityManager owner) { _owner = owner; }
            public void Dispose() { if (_owner != null) _owner.ExitStructural(); }
        }

        private StructuralScope EnterStructural()
        {
            System.Threading.Monitor.Enter(_structuralLock);
            return new StructuralScope(this);
        }

        private void ExitStructural() => System.Threading.Monitor.Exit(_structuralLock);

        // Native SendEvent 异步 drain 待处理列表（contextPtr, jobType, world）——记录 world 归属，
        // 避免多 World 下依赖全局 DefaultWorld 导致事件写错 EventStream 或丢失。
        internal readonly List<(IntPtr contextPtr, Type jobType, World world)> _pendingNativeEvents = new();
        // 1 = 队列可能非空：drain 先读它决定要不要取锁；0 = 确定为空。
        internal int _pendingNativeEventsPresent;
        internal readonly object _pendingNativeEventsLock = new();

        // 系统间依赖跟踪：组件类型 Id → 最后写入它的 Job（帧内跨系统传播，DOTS EntityDependencyManager 语义）。
        // 由 SystemRunner 在每个系统结束后按 [Write] 声明维护；CompleteActiveJobs（全量）后清空。
        private readonly Dictionary<int, JobHandle> _lastWritePerComponent = new();
        // 读依赖表（组件 → 最后读 Job，多读系统时组合）。写系统入站时合并它，
        //   使 [Read(X)] 的 Job 仍在飞时后续 [Write(X)] 必须等 —— 否则两者并发访问 X（撕裂/陈旧读）。
        //   读系统不合并本表 ， 读读仍然并行。ENTJOY_SYSTEM_READ_WRITE_ORDER=0 时整表不使用。
        private readonly Dictionary<int, JobHandle> _lastReadPerComponent = new();

        // 关系反向索引（target.Id → sources），Add/Remove/级联删除同步维护
        private RelationIndex _relationIndex = new();
        // 多实例关系（[MultiRelation]）正向存储（source → targets 列表）；反向复用 _relationIndex
        private RelationListStore _relationListStore = new();


        public EntityManager()
        {
            entities = new EntityIndexInWorld[32];  // 初始实体数组
            archetypeMap = new Dictionary<int, Archetype>(); // 初始化原型映射（哈希 -> 原型）
            allArchetypes = new Archetype[8];  // 初始原型数组
            _recycleStack = (Entity*)Marshal.AllocHGlobal(1024 * sizeof(Entity));
            _recycleCapacity = 1024;
            try
            {
                _locateB = NativeLocateStorage.AllocateLocate(entities.Length);
                _locateBCapacity = entities.Length;
            }
            catch { Marshal.FreeHGlobal((nint)_recycleStack); _recycleStack = null; throw; }
        }

        /// <summary>
        /// 根据给定的 <see cref="ComponentType"/> 数组 <paramref name="types"/> 获取或创建对应的 <see cref="Archetype"/>
        /// </summary>
        private Archetype GetOrCreateArchetype(Span<ComponentType> types)  // 获取或创建原型方法
        {
            // 查找按集合语义：与顺序无关的哈希 + 无序比较；哈希命中且集合相同才算命中。
            var hash = Utils.CalculateUnorderedHash(types);
            if (archetypeMap.TryGetValue(hash, out var cached) && Utils.SetEquals(cached.Types, types))
                return cached;
            // 哈希未命中或碰撞：原型数量少，线性兜底比对完整集合。
            for (int i = 0; i < archetypeCount; i++)
            {
                var existing = allArchetypes[i];
                if (existing != null && Utils.SetEquals(existing.Types, types))
                {
                    archetypeMap.TryAdd(hash, existing);
                    return existing;
                }
            }
            // 不存在匹配的原型：此时才规范化顺序，建原型用。
            Span<ComponentType> canonical = types.Length <= 16
                ? stackalloc ComponentType[types.Length]
                : new ComponentType[types.Length];
            types.CopyTo(canonical);
            Utils.SortByInsert(canonical);
            var archetype = new Archetype(canonical.ToArray()) { Owner = this };
            archetype.ArchetypeId = _nextArchetypeId++;
            if (!archetypeMap.ContainsKey(hash)) archetypeMap[hash] = archetype;
            //检查原型数组容量
            if (archetypeCount >= allArchetypes.Length)
            {
                Array.Resize(ref allArchetypes, allArchetypes.Length * 2);
            }
            // Todo: 如果有移除archetype的操作,空白的数组需要被填充
            allArchetypes[archetypeCount] = archetype;
            archetypeCount++;
            archetypeSetVersion++;   // 集合身份变化（见 ArchetypeSetVersion）
            return archetype;
        }
        /// <summary>
        /// 获取所有原型数组
        /// </summary>
        public Archetype[] GetAllArchetypes()
        {
            var result = new Archetype[archetypeCount];
            Array.Copy(allArchetypes, result, archetypeCount);
            return result;
        }

        public void Dispose()
        {
            if (_disposed) return;

            CompleteActiveJobs();
            _disposed = true;
            lock (QueryCacheLock) QueryCache.Clear();
            _structuralQueryVersion.Dispose();
            _relationshipQueryVersion.Dispose();

            var disposeErrors = DisposeArchetypes();
            archetypeMap.Clear();
            ClearRecycled();
            FreeRecycleStack();
            _lastChunkPerSharedValue.Clear();
            ChunkJobScheduler.ClearRawChunkScheduleCaches(this);
            _observers?.Clear();
            _observerCount = 0;
            // 上面已 CompleteActiveJobs()，无在飞 Job，记账表可整体清空，避免残留条目跨 World 存活。
            ClearJobTrackingNoLock();
            entities = Array.Empty<EntityIndexInWorld>();
            NativeLocateStorage.Free(_locateB);
            _locateB = null;
            _locateBCapacity = 0;
            allArchetypes = Array.Empty<Archetype>();
            archetypeCount = 0;
            archetypeSetVersion++;
            entityCount = 0;
            if (disposeErrors != null) throw new AggregateException("EntityManager disposal failed.", disposeErrors);
        }

        // 单个原型释放失败不中断整体清理：原生内存与定位表必须先全部归还，错误最后统一抛。
        private List<Exception> DisposeArchetypes()
        {
            List<Exception> errors = null;
            for (int i = 0; i < archetypeCount; i++)
            {
                try { allArchetypes[i]?.Dispose(); }
                catch (Exception ex) { (errors ??= new List<Exception>()).Add(ex); }
                allArchetypes[i] = null;
            }
            return errors;
        }

        private void FreeRecycleStack()
        {
            if (_recycleStack == null) return;
            System.Runtime.InteropServices.Marshal.FreeHGlobal((IntPtr)_recycleStack);
            _recycleStack = null;
            _recycleCapacity = 0;
            _recycleCount = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void CheckDisposed()
        {
            if (_disposed)
                ThrowDisposed();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ThrowDisposed() => throw new ObjectDisposedException(nameof(EntityManager));

        /// <summary>诊断/测试用：Job→写入组件 记账表条目数（守卫「不随调度次数无界增长」）。</summary>
        internal int JobWrittenComponentsCount
        {
            get { lock (_activeJobLock) return _jobWrittenComponents.Count; }
        }

        /// <summary>诊断/测试用：archetype 归属未知的在飞 Job 数（应随完成被清理）。</summary>
        internal int UnscopedJobCount
        {
            get { lock (_activeJobLock) return _unscopedJobs.Count; }
        }

        /// <summary>
        /// 创建新实体（基于组件类型）
        /// </summary>
        /// <summary>清除所有 archetype 所有 chunk 的变更标记（帧末调用）。</summary>
        public void ClearAllChangedBitMasks()
        {
            for (int i = 0; i < archetypeCount; i++)
                allArchetypes[i]?.ClearAllChangedBitMasks();
        }

    }
}
