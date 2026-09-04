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
            TempAllocator.Reset();     // 帧末回收 Temp 内存（未手动 Free 的块 + 安全句柄）
        }

        private void ExecuteSystem(SystemSlot slot)
        {
            var runWhenAttr = slot.SystemType.GetCustomAttribute<RunWhenAttribute>();
            if (runWhenAttr != null && _eventCounter.GetCount(runWhenAttr.EventType) == 0)
                return;

            var system = _systemInstances[slot.SystemType];

            // 多 World 隔离：System 内通过 World.DefaultWorld 访问实体时，临时指向所属 World。
            // 保存旧值，执行后恢复（支持嵌套 World / 手动切换场景）。
            var prev = World.DefaultWorld;
            World.DefaultWorld = _world;
            long start = Stopwatch.GetTimestamp();
            try
            {
                system.OnUpdate();
            }
            finally
            {
                World.DefaultWorld = prev;
            }
            long end = Stopwatch.GetTimestamp();

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