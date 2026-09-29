using System;
using System.Diagnostics;
using System.Threading;
using EntJoy.ECS;
using EntJoy.ECS.JobSystem;
using EntJoy.JobSystem;
using Xunit;

namespace EntJoy.ECS.Tests
{
    // ─────────────────────────── A 项：系统间"读→写"串行（口径 3） ───────────────────────────
    // 现场：出站只对 [Write] 调 SetLastWriter ⇒ [Read(X)] 系统结束后不给后续 [Write(X)] 留依赖，
    // 前一系统的 Job 仍在飞时后一系统可并发访问 X（撕裂/陈旧读）—— 唯一的**静默**竞态。
    // 修法：per-type 表拆两份（lastWrite / lastRead），只有写系统入站时合并 lastRead。
    //   · 读读：不合并 ⇒ 必须仍然并行（反向守卫，见下）
    //   · 读→写：合并 ⇒ 写系统等所有读 Job（红→绿主用例）
    //   · 开关：ENTJOY_SYSTEM_READ_WRITE_ORDER=0（或 runner.ReadWriteOrderingEnabled=false）回退旧行为

    // 慢读 job：忙等制造确定性"在飞"窗口，结束后把 readerDone 置 1（并把 Position.X 写成 42）。
    public struct OrderProbeSlowReadJob : IJobChunk
    {
        public static int Started;
        public static int Finished;

        public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
        {
            Interlocked.Increment(ref Started);
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 100) Thread.SpinWait(16);
            var span = chunk.GetComponentDataSpan<Position>();
            for (int i = 0; i < span.Length; i++) { span[i].X = 42f; span[i].Y = 42f; }
            Interlocked.Increment(ref Finished);
        }
    }

    // 写 job：若在慢读 job 完成前执行（= 依赖缺失），记一次违规；同时检查读 job 写下的值。
    public struct OrderProbeWriteCheckJob : IJobChunk
    {
        public static int Violations;

        public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
        {
            if (Volatile.Read(ref OrderProbeSlowReadJob.Finished) < Volatile.Read(ref OrderProbeSlowReadJob.Started))
                Interlocked.Increment(ref Violations);
            var span = chunk.GetComponentDataSpan<Position>();
            for (int i = 0; i < span.Length; i++)
                if (span[i].X != 42f) Interlocked.Increment(ref Violations);
        }
    }

    // 重叠观测 job：记录并发执行的最大数量（读读必须并行 ⇒ max ≥ 2）。
    public struct OrderProbeOverlapJob : IJobChunk
    {
        public static int Active;
        public static int MaxActive;

        public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
        {
            int active = Interlocked.Increment(ref Active);
            int seen;
            while (active > (seen = Volatile.Read(ref MaxActive)))
                Interlocked.CompareExchange(ref MaxActive, active, seen);
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 60) Thread.SpinWait(16);
            Interlocked.Decrement(ref Active);
        }
    }

    // ── 主用例图：读系统排在写系统之前（[OrderBefore] 覆盖"写者优先"的自动分层）──
    [Read(typeof(Position))]
    [OrderBefore(typeof(OrderProbeWriterSystem))]
    public struct OrderProbeReaderSystem : ISystem
    {
        public void OnUpdate() => new OrderProbeSlowReadJob().Schedule(new QueryBuilder().WithAll<Position>());
    }

    [Write(typeof(Position))]
    public struct OrderProbeWriterSystem : ISystem
    {
        /// <summary>写系统入站时是否看到读依赖记录（读表的写入发生在**同帧更早**的读系统之后）。</summary>
        public static bool SawReadDependency;

        public void OnUpdate()
        {
            var em = World.DefaultWorld.EntityManager;
            SawReadDependency = !em.GetLastReader(ComponentTypeManager.GetComponentType(typeof(Position))).IsNull;
            new OrderProbeWriteCheckJob().Schedule(new QueryBuilder().WithAll<Position>());
        }
    }

    /// <summary>同帧内、写系统之后观察读表（写系统出站时会清空它）。</summary>
    [OrderAfter(typeof(OrderProbeWriterSystem))]
    public struct OrderProbeObserveSystem : ISystem
    {
        public static bool ReadTableEmptyAfterWriter;

        public void OnUpdate() =>
            ReadTableEmptyAfterWriter = World.DefaultWorld.EntityManager
                .GetLastReader(ComponentTypeManager.GetComponentType(typeof(Position))).IsNull;
    }

    // ── 反向守卫图：两个纯读系统（读读必须并行）──
    [Read(typeof(Position))]
    public struct OrderProbeReaderASystem : ISystem
    {
        public void OnUpdate() => new OrderProbeOverlapJob().Schedule(new QueryBuilder().WithAll<Position>());
    }

    [Read(typeof(Position))]
    public struct OrderProbeReaderBSystem : ISystem
    {
        public void OnUpdate() => new OrderProbeOverlapJob().Schedule(new QueryBuilder().WithAll<Position>());
    }

    public class SystemReadWriteOrderTests
    {
        static SystemReadWriteOrderTests()
        {
            TestBackend.EnsureInitialized(4);
            Thread.Sleep(300);   // 等 worker 进入循环，避免主线程 assist 掩盖并发窗口
        }

        private static Type PositionType => typeof(Position);

        /// <summary>读→写：读系统的 Job 仍在飞时，写系统的 Job **必须**等它（旧代码必红）。</summary>
        [Fact]
        public void WriterSystem_WaitsForReaderSystemJob()
        {
            var world = new World("RWOrder");
            world.EntityManager.NewEntity(typeof(Position));

            var runner = new SystemRunner(world);
            Assert.True(runner.ReadWriteOrderingEnabled, "默认必须是安全口径（读→写串行开启）");
            runner.RegisterSystem<OrderProbeReaderSystem>();
            runner.RegisterSystem<OrderProbeWriterSystem>();

            OrderProbeSlowReadJob.Started = 0;
            OrderProbeSlowReadJob.Finished = 0;
            OrderProbeWriteCheckJob.Violations = 0;
            OrderProbeWriterSystem.SawReadDependency = false;
            OrderProbeObserveSystem.ReadTableEmptyAfterWriter = false;

            runner.Update();

            Assert.Equal(0, OrderProbeWriteCheckJob.Violations);
            Assert.True(OrderProbeWriterSystem.SawReadDependency,
                "写系统入站时应合并到读系统留下的依赖（lastRead 表）");

            world.Dispose();
        }

        /// <summary>反向守卫：两个 [Read(X)] 系统**必须**并行（读表不得把读读也串行化）。</summary>
        [Fact]
        public void TwoReaderSystems_StayParallel()
        {
            var world = new World("RWParallel");
            world.EntityManager.NewEntity(typeof(Position));

            var runner = new SystemRunner(world);
            Assert.True(runner.ReadWriteOrderingEnabled);
            runner.RegisterSystem<OrderProbeReaderASystem>();
            runner.RegisterSystem<OrderProbeReaderBSystem>();

            OrderProbeOverlapJob.Active = 0;
            OrderProbeOverlapJob.MaxActive = 0;

            runner.Update();   // 第 1 帧：读表为空
            runner.Update();   // 第 2 帧：读表里已有 A 的句柄——若实现把读表也并入**读**系统入站，B 会被串行化

            Assert.True(Volatile.Read(ref OrderProbeOverlapJob.MaxActive) >= 2,
                $"两个读系统的 Job 必须并行（实测最大并发 {OrderProbeOverlapJob.MaxActive}）：" +
                "读→写串行不得把读读也串行化。");

            world.Dispose();
        }

        /// <summary>口径 3 回退：开关关闭 ⇒ 旧行为（读系统的 Job 不被写系统等待，读表不参与）。</summary>
        [Fact]
        public void ReadWriteOrderingDisabled_FallsBackToLegacyBehaviour()
        {
            var world = new World("RWLegacy");
            var em = world.EntityManager;
            em.NewEntity(typeof(Position));

            var runner = new SystemRunner(world) { ReadWriteOrderingEnabled = false };
            runner.RegisterSystem<OrderProbeReaderSystem>();
            runner.RegisterSystem<OrderProbeWriterSystem>();

            OrderProbeSlowReadJob.Started = 0;
            OrderProbeSlowReadJob.Finished = 0;
            OrderProbeWriteCheckJob.Violations = 0;
            OrderProbeWriterSystem.SawReadDependency = true;

            runner.Update();

            // 读表整体不参与（确定性）：读系统不写回读依赖，写系统入站也看不到读表 ⇒ 不等读 Job。
            Assert.False(OrderProbeWriterSystem.SawReadDependency,
                "关闭开关后读系统不应写入读依赖表");
            Assert.True(OrderProbeWriteCheckJob.Violations > 0,
                "关闭开关后写系统不应等待读系统的 Job（旧行为：并发访问 X）");

            world.Dispose();
        }

        /// <summary>读表生命周期：同帧内读系统写入 → 写系统入站可见 → 写系统出站清空。</summary>
        [Fact]
        public void ReaderSystem_RecordsReadDependency_AndWriterClearsIt()
        {
            var world = new World("RWTable");
            var em = world.EntityManager;
            em.NewEntity(typeof(Position));

            var runner = new SystemRunner(world);
            runner.RegisterSystem<OrderProbeReaderSystem>();
            runner.RegisterSystem<OrderProbeWriterSystem>();
            runner.RegisterSystem<OrderProbeObserveSystem>();

            OrderProbeWriterSystem.SawReadDependency = false;
            OrderProbeObserveSystem.ReadTableEmptyAfterWriter = false;

            runner.Update();

            Assert.True(OrderProbeWriterSystem.SawReadDependency, "读系统结束后写系统应看到 lastRead 记录");
            Assert.True(OrderProbeObserveSystem.ReadTableEmptyAfterWriter,
                "写系统完成后应清空 lastRead 表（这次写已等过所有读）");

            world.Dispose();
        }
    }
}
