using System;
using System.Diagnostics;
using System.Threading;
using EntJoy.ECS;
using EntJoy.ECS.JobSystem;
using EntJoy.JobSystem;
using Xunit;

namespace EntJoy.ECS.Tests
{
    // ──────────────────── C 项：组合依赖句柄的确定性回收 ────────────────────
    // 现场：JobHandle 没有 Dispose/Release（只能靠终结器），而 SystemRunner 每个冲突 system
    // 每帧都会 CombineDependencies 出一个新的 native HandleState ⇒ 句柄抖动/内存抖动。
    // 修法：① deps.Count==1 不再组合（R11，已在）；② 运行器自己组合出来的句柄用后即释；
    //       ③ 依赖表覆盖旧值时，旧句柄**已完成**则确定性释放（未完成绝不能放，见安全用例）。
    //
    // 断言口径：native 侧"存活 HandleState 数"（CreateState - RecycleState，JobSystem_GetLiveHandleCount）。

    // 无操作 chunk job：只用于产生一个真实句柄（执行极快 ⇒ 会在同帧内被"剪枝即释放"回收）。
    public struct HandleProbeNoOpJob : IJobChunk
    {
        public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask) { }
    }

    // 短自旋 chunk job：保证 Schedule 返回后句柄仍在飞（后续 system 合并依赖时它仍是有效句柄）
    // —— 这是"2 依赖 ⇒ 组合句柄"路径能被真正走到的前提。
    public struct HandleProbeShortSpinJob : IJobChunk
    {
        public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
        {
            long until = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 2000;   // ~0.5 ms
            while (Stopwatch.GetTimestamp() < until) Thread.SpinWait(32);
        }
    }

    // 慢写 job：忙等制造确定性"在飞"窗口，结束后把 X 写成 42。
    public struct HandleProbeSlowWriteJob : IJobChunk
    {
        public static int Started;
        public static int Finished;

        public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
        {
            Interlocked.Increment(ref Started);
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 150) Thread.SpinWait(16);
            var span = chunk.GetComponentDataSpan<Position>();
            for (int i = 0; i < span.Length; i++) { span[i].X = 42f; span[i].Y = 42f; }
            Interlocked.Increment(ref Finished);
        }
    }

    // ── 帧首：把上一帧留的依赖全部等完。只用于让"句柄何时可确定性回收"的观测点确定化，
    //   本身不是被测语义（真实系统图里句柄随下一次 Complete/结构变更被回收）。
    [Read(typeof(Position))]
    [Read(typeof(Velocity))]
    [OrderBefore(typeof(HandleProbeWritePositionSystem))]
    [OrderBefore(typeof(HandleProbeWriteVelocitySystem))]
    [OrderBefore(typeof(HandleProbeTwoDepWriterSystem))]
    public struct HandleProbeCompleteDepsSystem : ISystem, ISystemWithState
    {
        public void OnUpdate(ref SystemState state) => state.Dependency.Complete();
    }

    // 短自旋（~0.5ms）：TwoDepWriter 合并入站依赖时这两份句柄仍在飞 ⇒ 走组合句柄路径。
    [Write(typeof(Position))]
    public struct HandleProbeWritePositionSystem : ISystem
    {
        public void OnUpdate() => new HandleProbeShortSpinJob().Schedule(new QueryBuilder().WithAll<Position>());
    }

    [Write(typeof(Velocity))]
    public struct HandleProbeWriteVelocitySystem : ISystem
    {
        public void OnUpdate() => new HandleProbeShortSpinJob().Schedule(new QueryBuilder().WithAll<Velocity>());
    }

    // 只读系统：调度的 Job 句柄**不会**进入写依赖表（没有 [Write]），帧末的依赖表清理够不到它；
    // 它只能在"剪枝（已完成）即释放"这条路径上被确定性回收。这里先等第一个 Job 真的完成，
    // 再调度第二个 —— 第二个 Job 的 TrackEntityJob 必然把第一个（已完成）剪掉，路径稳定可复现。
    [Read(typeof(Velocity))]
    [Order(-1)]
    [OrderAfter(typeof(HandleProbeCompleteDepsSystem))]
    public struct HandleProbeReadOnlyJobSystem : ISystem
    {
        public void OnUpdate()
        {
            var first = new HandleProbeNoOpJob().Schedule(new QueryBuilder().WithAll<Velocity>());
            var sw = Stopwatch.StartNew();
            while (!first.IsCompleted && sw.ElapsedMilliseconds < 5000) Thread.SpinWait(32);
            new HandleProbeNoOpJob().Schedule(new QueryBuilder().WithAll<Velocity>());
        }
    }

    // 入站依赖 = lastWrite[Velocity] + lastWrite[Position]（读一个、写一个）⇒ 2 个依赖走组合句柄。
    [Read(typeof(Velocity))]
    [Write(typeof(Position))]
    [OrderAfter(typeof(HandleProbeWritePositionSystem))]
    [OrderAfter(typeof(HandleProbeWriteVelocitySystem))]
    public struct HandleProbeTwoDepWriterSystem : ISystem
    {
        public void OnUpdate() => new HandleProbeNoOpJob().Schedule(new QueryBuilder().WithAll<Position>());
    }

    // ── 安全用例图：SlowWriter 起一个在飞慢 job；ImmediateOverwriter 紧随其后写同一组件，
    //   触发"依赖表覆盖旧值"路径（旧句柄此时仍在飞）。
    [Write(typeof(Position))]
    public struct HandleProbeSlowWriterSystem : ISystem
    {
        public static JobHandle LastHandle;

        public void OnUpdate() => LastHandle = new HandleProbeSlowWriteJob().Schedule(new QueryBuilder().WithAll<Position>());
    }

    [Write(typeof(Position))]
    [OrderAfter(typeof(HandleProbeSlowWriterSystem))]
    public struct HandleProbeImmediateOverwriterSystem : ISystem
    {
        public void OnUpdate() => new HandleProbeNoOpJob().Schedule(new QueryBuilder().WithAll<Position>());
    }

    // 同帧内、覆盖之后观察"被覆盖的那份在飞句柄"是否仍有效（必须在帧末 CompleteActiveJobs 之前）。
    [OrderAfter(typeof(HandleProbeImmediateOverwriterSystem))]
    public struct HandleProbeObserveSupersededSystem : ISystem
    {
        public static bool SupersededHandleWasDetached;

        public void OnUpdate() =>
            SupersededHandleWasDetached = HandleProbeSlowWriterSystem.LastHandle.IsNull;
    }

    public class CombinedDependencyHandleReleaseTests
    {
        static CombinedDependencyHandleReleaseTests()
        {
            TestBackend.EnsureInitialized(4);
            Thread.Sleep(300);   // 等 worker 线程进入循环，避免主线程 assist 掩盖并发窗口
        }

        private static long LiveHandleCount()
        {
            Assert.True(NativeJobCore.TryGetLiveHandleStateCount(out long live),
                "NativeDll.dll 缺少 JobSystem_GetLiveHandleCount 导出：请重建 tools/NativeDllCore，把 " +
                "build-ci\\Release\\NativeDll.dll 复制到测试输出目录（tests/EntJoy.ECS.Tests/bin/<config>/net8.0/）。");
            return live;
        }

        /// <summary>native 后端专属：托管后端没有 native HandleState，本用例无可观测对象。</summary>
        private static bool NativeAccountingAvailable() => TestBackend.IsNative;

        // ── 用例 1（交接文档验收项）：反复调度带 2 依赖的系统图，native 存活句柄数不增长 ──
        // 无 GC 区域是必要的：不设它的话，未被确定性释放的句柄会被终结器批量回收，掩盖"只靠终结器"
        // 的现状（这正是 C 项的现场），红/绿就不可复现。
        [Fact]
        public void TwoDependencyGraph_NativeHandlesDoNotGrowAcrossFrames()
        {
            if (!NativeAccountingAvailable()) return;

            var world = new World("HandleGrowth");
            world.EntityManager.NewEntity(typeof(Position), typeof(Velocity));

            var runner = new SystemRunner(world);
            runner.RegisterSystem<HandleProbeCompleteDepsSystem>();
            runner.RegisterSystem<HandleProbeWritePositionSystem>();
            runner.RegisterSystem<HandleProbeWriteVelocitySystem>();
            runner.RegisterSystem<HandleProbeReadOnlyJobSystem>();
            runner.RegisterSystem<HandleProbeTwoDepWriterSystem>();

            const int WarmupFrames = 64;
            const int MeasuredFrames = 1000;
            for (int i = 0; i < WarmupFrames; i++) runner.Update();
            long before = LiveHandleCount();

            long after;
            bool noGc = GC.TryStartNoGCRegion(512L * 1024 * 1024, disallowFullBlockingGC: true);
            try
            {
                for (int i = 0; i < MeasuredFrames; i++) runner.Update();
                after = LiveHandleCount();
            }
            finally
            {
                if (noGc) GC.EndNoGCRegion();
            }

            long growth = after - before;
            Assert.True(growth < 64,
                $"native 存活句柄数在 {MeasuredFrames} 帧内增长 {growth}（{before} → {after}）：" +
                "组合依赖句柄/被覆盖的写句柄没有被确定性回收（只靠终结器）。");

            world.Dispose();
        }

        // ── 用例 2（安全边界，交接文档要求"必须加断言测试证明"）：依赖表覆盖旧值时旧句柄**仍在飞**
        //   ⇒ 绝不能提前释放。所有 JobHandle 拷贝共享同一个 box，提前 Release = detach box ⇒ 该句柄
        //   （含 _activeJobs 记账项与用户手里的拷贝）的 Complete 全部退化成空操作：等待/结构变更
        //   屏障会静默消失，且原生 HandleState 可能在 Job 仍在飞时被回收复用。
        //   （若无条件 Release，本用例必红：Complete 立刻返回，慢 job 还没结束。）
        [Fact]
        public void SupersededInFlightHandle_RemainsWaitable()
        {
            if (!NativeAccountingAvailable()) return;

            var world = new World("InFlightSuperseded");
            world.EntityManager.NewEntity(typeof(Position));

            var runner = new SystemRunner(world);
            runner.RegisterSystem<HandleProbeSlowWriterSystem>();
            runner.RegisterSystem<HandleProbeImmediateOverwriterSystem>();
            runner.RegisterSystem<HandleProbeObserveSupersededSystem>();

            HandleProbeSlowWriteJob.Started = 0;
            HandleProbeSlowWriteJob.Finished = 0;
            HandleProbeSlowWriterSystem.LastHandle = default;
            HandleProbeObserveSupersededSystem.SupersededHandleWasDetached = false;

            runner.Update();   // SlowWriter 起慢 job；ImmediateOverwriter 覆盖依赖表（旧句柄在飞）

            Assert.Equal(1, Volatile.Read(ref HandleProbeSlowWriteJob.Started));
            Assert.False(HandleProbeObserveSupersededSystem.SupersededHandleWasDetached,
                "被覆盖的在飞句柄被提前释放（detach）：它的 Complete 已退化为空操作，等待屏障消失。");

            // 帧末 TempAllocator.Reset → CompleteActiveJobs 会等完所有记账 Job（含慢 job）。
            Assert.Equal(Volatile.Read(ref HandleProbeSlowWriteJob.Started),
                         Volatile.Read(ref HandleProbeSlowWriteJob.Finished));

            world.Dispose();
        }
    }
}
