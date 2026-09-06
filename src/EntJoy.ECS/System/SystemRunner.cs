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
            var incoming = ComputeIncomingDependency(slot);
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

            // 累计 System 耗时（性能分析器）
            double ms = (end - start) * 1000.0 / Stopwatch.Frequency;
            if (!_timings.TryGetValue(slot.SystemType, out var timing))
                timing = new SystemTiming { SystemName = slot.SystemType.Name };
            timing.TotalMs += ms;
            timing.FrameCount++;
            if (ms > timing.MaxMs) timing.MaxMs = ms;
            timing.AvgMs = timing.TotalMs / timing.FrameCount;
            _timings[slot.SystemType] = timing;
        }

        private JobHandle ComputeIncomingDependency(SystemSlot slot)
        {
            var deps = new List<JobHandle>();
            CollectConflictDependencies(slot.ReadComponents, deps);
            CollectConflictDependencies(slot.WriteComponents, deps);
            return deps.Count > 0 ? JobHandle.CombineDependencies(deps.ToArray()) : default;
        }

        private void CollectConflictDependencies(HashSet<Type> componentTypes, List<JobHandle> deps)
        {
            foreach (var t in componentTypes)
            {
                var h = _world.EntityManager.GetLastWriter(ComponentTypeManager.GetComponentType(t));
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