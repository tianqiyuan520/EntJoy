using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using EntJoy.Collections;
using EntJoy.JobSystem;

namespace EntJoy.ECS
{
    public class SystemRunner
    {
        private readonly World _world;
        private readonly ScheduleGraph _graph = new();
        private readonly Dictionary<Type, ISystem> _systemInstances = new();
        private readonly EventCounter _eventCounter = new();
        private readonly Dictionary<Type, SystemTiming> _timings = new();
        private long _currentFrame;
        private double _lastFrameSystemMs;

        /// <summary>上一帧各 system <c>OnUpdate</c> 自身跨度的耗时之和（ms）。
        /// 诊断口径：<c>Update()</c> 的整步墙钟 − 本值 = **运行器簿记**（层遍历、<c>[RunWhen]</c> 反射、
        /// 入站依赖合并、<c>[Write]</c> 表写回、读依赖表维护、入站组合句柄释放、计时字典，
        /// 以及帧末 `CompletePendingNativeEvents`/事件交换/`TempAllocator.Reset`）。
        /// 显式按序直调各 system 的调用方（Unity 侧 M4，见其 `[M4-DISCLOSE] ⑧`）不付这一笔。
        /// 取证必须在 <c>Update()</c> 返回后**同帧**读取（跨线程读 <c>_timings</c> 会与窗口边界错配）。</summary>
        public double LastFrameSystemMsSum => _lastFrameSystemMs;

        // ★ A 项（口径 3）：系统间"读→写"依赖。默认**开启**：读系统结束后把 outgoing 写进读依赖表，
        //   写系统入站时合并读表 ⇒ [Read(X)] 的 Job 仍在飞时，后续 [Write(X)] 的 Job 必须等它
        //   （消掉唯一的静默竞态：撕裂/陈旧读）。读系统**不**合并读表 ⇒ 读读仍并行。
        //   `ENTJOY_SYSTEM_READ_WRITE_ORDER=0` 回退旧行为（读依赖表整体不参与）。
        private static readonly bool s_readWriteOrderFromEnv = ReadReadWriteOrderFromEnv();
        private bool _readWriteOrdering = s_readWriteOrderFromEnv;

        /// <summary>读→写依赖开关（默认安全；测试与嵌入方可用它显式切换，语义等同环境变量）。</summary>
        internal bool ReadWriteOrderingEnabled
        {
            get => _readWriteOrdering;
            set => _readWriteOrdering = value;
        }

        private static bool ReadReadWriteOrderFromEnv()
        {
            var v = Environment.GetEnvironmentVariable("ENTJOY_SYSTEM_READ_WRITE_ORDER");
            if (string.IsNullOrEmpty(v)) return true;
            v = v.Trim();
            return !(v == "0" || string.Equals(v, "false", StringComparison.OrdinalIgnoreCase));
        }

        public long CurrentFrame => _currentFrame;
        public EventCounter EventCounter => _eventCounter;

        public SystemRunner(World world)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
        }

        public void RegisterSystem<T>() where T : struct, ISystem
        {
            _graph.RegisterSystem<T>();
            _systemInstances[typeof(T)] = default(T);
        }

        public void PrintSchedule() => _graph.PrintSchedule();

        public void Update()
        {
            _currentFrame++;
            _world.CurrentFrame = _currentFrame;
            _lastFrameSystemMs = 0.0;

            var layers = _graph.GetLayers();
            foreach (var layer in layers)
            {
                foreach (var slot in layer)
                {
                    ExecuteSystem(slot);
                }
            }
            // 帧末屏障：先等所有 job 完成（含异步 SendEvent 的生产者），再交换事件双缓冲——
            // 否则 worker 仍在写 buffer 时 swap 会导致计数与实际数据串帧/并发读写同一数组。
            _world.CompletePendingNativeEvents();
            _world.NextFrameEvents();  // 帧末交换事件双缓冲
            _eventCounter.Reset();
            _world.RefreshEventCounter(_eventCounter);  // 本帧事件 → 计数器（下帧 RunWhen 门控）
            TempAllocator.Reset();     // 帧末回收 Temp 内存（未手动 Free 的块 + 安全句柄）
        }

        private void ExecuteSystem(SystemSlot slot)
        {
            var runWhenAttr = slot.SystemType.GetCustomAttribute<RunWhenAttribute>();
            if (runWhenAttr != null && _eventCounter.GetCount(runWhenAttr.EventType) == 0)
                return;

            // ISystem 以装箱引用存储：struct 系统的字段修改在 OnUpdate 内写入 box，天然跨帧持久。
            var system = _systemInstances[slot.SystemType];

            // 入站依赖：按 [Read]/[Write] 查 per-type 表合并冲突依赖（读等写、写等写，读读不冲突）。
            var incoming = ComputeIncomingDependency(slot, out bool incomingIsCombined);
            // 每帧重建 SystemState：Dependency 由系统可改写（ISystemWithState），无跨帧字段。
            var state = new SystemState { Dependency = incoming };

            // 多 World 隔离：临时指向所属 World（ThreadStatic DefaultWorld 每线程独立，执行后恢复）。
            var prevWorld = World.DefaultWorld;
            World.DefaultWorld = _world;
            SystemExecutionContext.IsActive = true;
            SystemExecutionContext.Dependency = incoming;
            long start = Stopwatch.GetTimestamp();
            try
            {
                if (system is ISystemWithState withState)
                {
                    withState.OnUpdate(ref state);
                    SystemExecutionContext.Dependency = state.Dependency;
                }
                else
                {
                    system.OnUpdate();
                }
            }
            finally
            {
                SystemExecutionContext.IsActive = false;
                World.DefaultWorld = prevWorld;
            }
            long end = Stopwatch.GetTimestamp();

            // 出站：按 [Write] 把本系统累积依赖写回 per-type 表（供后续系统合并）
            var outgoing = SystemExecutionContext.Dependency;
            foreach (var t in slot.WriteComponents)
                _world.EntityManager.SetLastWriter(ComponentTypeManager.GetComponentType(t), outgoing);

            // ★ A 项：读→写串行（读表只在开关打开时参与）
            if (_readWriteOrdering)
            {
                // 写系统：这次写已经等过所有读 ⇒ 清掉这些组件的读依赖
                foreach (var t in slot.WriteComponents)
                    _world.EntityManager.ClearLastReader(ComponentTypeManager.GetComponentType(t));
                // 读系统：把 outgoing 并入读依赖表（merge；同组件既读又写时写表已记录，无需重复）
                foreach (var t in slot.ReadComponents)
                    if (!slot.WriteComponents.Contains(t))
                        _world.EntityManager.SetLastReader(ComponentTypeManager.GetComponentType(t), outgoing);
            }

            // ★ C：本系统入站依赖是运行器自己组合出来的句柄时，用后即释。
            //   组合句柄只作为"入站依赖聚合器"存在：任何用它 Schedule 出去的 Job 在提交时都已
            //   RetainedNativeDependency 持引用（CombineDependencies 内部也对父依赖持引用，直到
            //   全部完成才 ReleaseState），故此处释放不会提前回收在飞依赖；系统若一个 Job 都没调度、
            //   又把入站句柄原样写回写表（adopted），所有权已转移给依赖表 ⇒ 不释放。
            //   不释放的后果：每个多依赖 system 每帧留一个 HandleState 到 GC 终结器成批回收。
            if (incomingIsCombined && !IsAdoptedByWriteTable(incoming, outgoing, slot))
                NativeJobScheduler.Release(incoming._nativeHandle);

            // 累计 System 耗时（性能分析器）
            double ms = (end - start) * 1000.0 / Stopwatch.Frequency;
            _lastFrameSystemMs += ms;
            if (!_timings.TryGetValue(slot.SystemType, out var timing))
                timing = new SystemTiming { SystemName = slot.SystemType.Name };
            timing.TotalMs += ms;
            timing.FrameCount++;
            if (ms > timing.MaxMs) timing.MaxMs = ms;
            timing.AvgMs = timing.TotalMs / timing.FrameCount;
            _timings[slot.SystemType] = timing;
        }

        private JobHandle ComputeIncomingDependency(SystemSlot slot, out bool combined)
        {
            combined = false;
            var deps = new List<JobHandle>();
            CollectConflictDependencies(slot.ReadComponents, deps);
            CollectConflictDependencies(slot.WriteComponents, deps);
            // ★ A 项：写系统还要等"读过这些组件的系统"留下的 Job（读表只在此处、且仅在写侧参与；
            //   读系统不合并读表 ⇒ 读读仍并行）。开关关闭时整表不参与，等价旧行为。
            if (_readWriteOrdering && slot.WriteComponents.Count > 0)
                CollectLastReaderDependencies(slot.WriteComponents, deps);
            // ★ R11：单个依赖直接返回，不构造组合句柄。
            //   CombineDependencies 会新建一个 native HandleState，而 JobHandle 没有 Dispose/Release
            //   （只能等终结器），所以"每个冲突 system 每帧都组合一次"会造成无谓的句柄抖动。
            //   只有 ≥2 个依赖时才值得组合；语义不变（组合单句柄 ≡ 直接返回该句柄）。
            if (deps.Count == 0) return default;
            if (deps.Count == 1) return deps[0];
            combined = true;
            return JobHandle.CombineDependencies(deps.ToArray());
        }

        /// <summary>入站组合句柄的所有权是否已转移给写表（系统没调度 Job、且把入站句柄原样写回）。</summary>
        private static bool IsAdoptedByWriteTable(JobHandle incoming, JobHandle outgoing, SystemSlot slot)
        {
            // 只有确实写表（有 [Write]）才可能转移所有权；句柄指针相同即"原样写回"。
            if (slot.WriteComponents.Count == 0) return false;
            IntPtr owned = incoming._nativeHandle.Handle;
            return owned != IntPtr.Zero && owned == outgoing._nativeHandle.Handle;
        }

        private void CollectConflictDependencies(HashSet<Type> componentTypes, List<JobHandle> deps)
        {
            foreach (var t in componentTypes)
            {
                var h = _world.EntityManager.GetLastWriter(ComponentTypeManager.GetComponentType(t));
                if (!h.IsNull) deps.Add(h);
            }
        }

        /// <summary>写系统入站：并入"最后读依赖"（A 项读→写串行）。</summary>
        private void CollectLastReaderDependencies(HashSet<Type> writeComponents, List<JobHandle> deps)
        {
            foreach (var t in writeComponents)
            {
                var h = _world.EntityManager.GetLastReader(ComponentTypeManager.GetComponentType(t));
                if (!h.IsNull) deps.Add(h);
            }
        }

        /// <summary>生成性能分析报告（System 耗时 + Job 调度 + slab 复用 + 内存布局）。</summary>
        public PerformanceReport GetPerformanceReport()
        {
            var (allocs, frees, hits, misses) = ChunkMemoryPool.GetStats();
            var report = new PerformanceReport
            {
                SystemTimings = new List<SystemTiming>(),
                JobStats = JobScheduler.IsNative ? NativeJobScheduler.GetStats() : default,
                ChunkPoolAllocs = allocs,
                ChunkPoolFrees = frees,
                ChunkPoolHits = hits,
                ChunkPoolMisses = misses,
                Memory = _world.GetMemoryReport(),
            };
            foreach (var kv in _timings)
                report.SystemTimings.Add(kv.Value);
            report.SystemTimings.Sort((a, b) => string.CompareOrdinal(a.SystemName, b.SystemName));
            return report;
        }
    }
}