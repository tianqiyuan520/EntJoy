using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using EntJoy.Collections;
using EntJoy.ECS;
using EntJoy.ECS.JobSystem;
using EntJoy.JobSystem;

namespace EntJoySample.ManyJobsBenchTest
{
    // ============================================================
    // ManyJobsBench —— 几百个 Job / 帧：纯调度流程开销定位
    //
    // 规则（按需求收敛）：
    //   - 只有 4 种类型：IJob / IJobFor / IJobParallelFor / IJobChunk
    //   - Job 体全部为空：不关心运行内容，只关心「调度流程」本身
    //   - 每帧 Schedule×N + Complete×N，量：墙钟 + 调度/完成分段 + 主线程分配
    //     + native 侧批次统计（唤醒/执行跨度/完成）。
    //
    // 测量（对齐 IJobChunkMoveCompareTest 口径）：
    //   - Pass A（timing OFF，100 帧）：干净墙钟 + Schedule/Complete 分段 + 全局计数器
    //   - Pass B（timing ON，帧数按采样上限自适应）：batchTotal / submit2first /
    //     spread / execSpan / maxRange 批次分布
    //
    // 环境变量：
    //   ENTJOY_JOB_WORKERS      worker 数（全局）
    //   ENTJOY_BENCH_WARMUP     预热帧（默认 5）
    //   ENTJOY_BENCH_FRAMES     Pass A 帧数（默认 100）
    // ============================================================

    public struct BenchData : IComponentData { public long Value; }

    // ---- 4 种类型，空体 ----
    public struct EmptyJob : IJob
    {
        public void Execute() { }
    }

    public struct EmptyForJob : IJobFor
    {
        public void Execute(int index) { }
    }

    public struct EmptyParJob : IJobParallelFor
    {
        public void Execute(int index) { }
    }

    public struct EmptyChunkJob : IJobChunk
    {
        public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask) { }
    }

    public sealed unsafe class ManyJobsBenchSample : IDisposable
    {
        private static int ReadPositiveEnvironmentInt(string name, int fallback)
        {
            return int.TryParse(Environment.GetEnvironmentVariable(name), out int value) && value > 0
                ? value
                : fallback;
        }

        private const int ChunkWorldEntitiesSmall = 16_384;  // ~8 chunks → 少量 tile
        private const int ChunkWorldEntitiesBig = 1_000_000; // ~488 chunks → ~224 tiles（对齐 100w 基准）

        private readonly int _warmupFrames = ReadPositiveEnvironmentInt("ENTJOY_BENCH_WARMUP", 5);
        private readonly int _measureFrames = ReadPositiveEnvironmentInt("ENTJOY_BENCH_FRAMES", 100);

        private readonly World _chunkSmallWorld;
        private readonly World _chunkBigWorld;
        private readonly QueryBuilder _chunkQuery;

        public ManyJobsBenchSample()
        {
            _chunkSmallWorld = new World("ManyJobs_ChunkSmall");
            _chunkBigWorld = new World("ManyJobs_ChunkBig");
            _chunkQuery = new QueryBuilder().WithAll<BenchData>();

            Console.WriteLine($" Preparing chunk worlds: {ChunkWorldEntitiesSmall:N0} + {ChunkWorldEntitiesBig:N0} entities (空体 job)");
            CreateChunkWorld(_chunkSmallWorld, ChunkWorldEntitiesSmall);
            CreateChunkWorld(_chunkBigWorld, ChunkWorldEntitiesBig);
        }

        public void Dispose()
        {
            _chunkSmallWorld.Dispose();
            _chunkBigWorld.Dispose();
        }

        private static void CreateChunkWorld(World world, int entityCount)
        {
            var entityManager = world.EntityManager;
            for (int i = 0; i < entityCount; i++)
                entityManager.NewEntity(typeof(BenchData));
        }

        // ============================================================
        // Case 定义：只有调度动作，Job 体为空
        // ============================================================
        private sealed class BenchCase
        {
            public string Label = "";
            public int N;
            public Action<JobHandle[]> Schedule = null!; // 填 handles[0..N)
            public Action<JobHandle[]> Complete = null!;
        }

        private static void CompleteAll(JobHandle[] handles, int count)
        {
            for (int i = 0; i < count; i++)
                handles[i].Complete();
        }

        private static BenchCase Case(string label, int n, Action<JobHandle[]> schedule)
        {
            var c = new BenchCase { Label = label, N = n, Schedule = schedule };
            int count = n;
            c.Complete = h => CompleteAll(h, count);
            return c;
        }

        private List<BenchCase> BuildCases()
        {
            var cases = new List<BenchCase>();
            var emptyJob = new EmptyJob();
            var emptyFor = new EmptyForJob();
            var emptyPar = new EmptyParJob();
            var emptyChunk = new EmptyChunkJob();

            // ---- IJob（2026-08-30 起全异步：Schedule 一律提交 worker，无 inline） ----
            cases.Add(Case("IJob x50 (async)", 50, h =>
            {
                for (int i = 0; i < 50; i++) h[i] = emptyJob.Schedule();
            }));
            cases.Add(Case("IJob x200 (async)", 200, h =>
            {
                for (int i = 0; i < 200; i++) h[i] = emptyJob.Schedule();
            }));

            // ---- IJobFor：全异步（≤64 池任务；>64 单 worker 异步任务） ----
            cases.Add(Case("IJobFor·1K x100 (async)", 100, h =>
            {
                for (int i = 0; i < 100; i++) h[i] = emptyFor.Schedule(1024);
            }));
            cases.Add(Case("IJobFor·100K x100 (async单任务)", 100, h =>
            {
                for (int i = 0; i < 100; i++) h[i] = emptyFor.Schedule(100_000);
            }));

            // ---- IJobParallelFor：tile 路径 ----
            cases.Add(Case("IJobParallelFor·8K x100", 100, h =>
            {
                for (int i = 0; i < 100; i++) h[i] = emptyPar.Schedule(8192, 0);
            }));
            cases.Add(Case("IJobParallelFor·8K x400", 400, h =>
            {
                for (int i = 0; i < 400; i++) h[i] = emptyPar.Schedule(8192, 0);
            }));
            cases.Add(Case("IJobParallelFor·8K batch256 x100", 100, h =>
            {
                for (int i = 0; i < 100; i++) h[i] = emptyPar.Schedule(8192, 256);
            }));
            cases.Add(Case("IJobParallelFor·64K x100", 100, h =>
            {
                for (int i = 0; i < 100; i++) h[i] = emptyPar.Schedule(65_536, 0);
            }));
            cases.Add(Case("IJobParallelFor·1M x20", 20, h =>
            {
                for (int i = 0; i < 20; i++) h[i] = emptyPar.Schedule(1_000_000, 0);
            }));

            // ---- IJobChunk：小世界（少量 tile）与大世界（~224 tile） ----
            cases.Add(Case("IJobChunk·16K x100 (少量tile)", 100, h =>
            {
                World.DefaultWorld = _chunkSmallWorld;
                for (int i = 0; i < 100; i++) h[i] = emptyChunk.Schedule(_chunkQuery);
            }));
            cases.Add(Case("IJobChunk·1M x50 (224 tile)", 50, h =>
            {
                World.DefaultWorld = _chunkBigWorld;
                for (int i = 0; i < 50; i++) h[i] = emptyChunk.Schedule(_chunkQuery);
            }));

            // ---- IJob 依赖链 ×200：async 分池路径（每 job 一个 worker 任务） ----
            cases.Add(Case("IJob chain x200 (async)", 200, h =>
            {
                h[0] = emptyJob.Schedule();
                for (int i = 1; i < 200; i++)
                    h[i] = emptyJob.Schedule(h[i - 1]);
            }));

            // ---- 显式批（BatchScope）对照：同场景走批提交，与逐 job 基线同场对比 ----
            cases.Add(Case("Batch·IJob x200", 200, h =>
            {
                using var b = new BatchScope();
                for (int i = 0; i < 200; i++) b.Add(ref emptyJob);
                var hs = b.Submit();
                for (int i = 0; i < hs.Length; i++) h[i] = hs[i];
            }));
            cases.Add(Case("Batch·IJobFor·1K x100", 100, h =>
            {
                using var b = new BatchScope();
                for (int i = 0; i < 100; i++) b.AddFor(ref emptyFor, 1024);
                var hs = b.Submit();
                for (int i = 0; i < hs.Length; i++) h[i] = hs[i];
            }));
            cases.Add(Case("Batch·IJobFor·100K x100 (async单任务)", 100, h =>
            {
                using var b = new BatchScope();
                for (int i = 0; i < 100; i++) b.AddFor(ref emptyFor, 100_000);
                var hs = b.Submit();
                for (int i = 0; i < hs.Length; i++) h[i] = hs[i];
            }));
            cases.Add(Case("Batch·IJobParallelFor·8K x100", 100, h =>
            {
                using var b = new BatchScope();
                for (int i = 0; i < 100; i++) b.AddParallelFor(ref emptyPar, 8192, 0);
                var hs = b.Submit();
                for (int i = 0; i < hs.Length; i++) h[i] = hs[i];
            }));
            cases.Add(Case("Batch·Mixed(100+50+50) x200", 200, h =>
            {
                using var b = new BatchScope();
                for (int i = 0; i < 100; i++) b.Add(ref emptyJob);
                for (int i = 0; i < 50; i++) b.AddFor(ref emptyFor, 1024);
                for (int i = 0; i < 50; i++) b.AddParallelFor(ref emptyPar, 8192, 0);
                var hs = b.Submit();
                for (int i = 0; i < hs.Length; i++) h[i] = hs[i];
            }));

            // ---- 隐式批：C# 全局收集（Add 零 P/Invoke）+ 帧末 EndFrame 一次提交（P/Invoke 200→1） ----
            cases.Add(Case("Implicit·Mixed(100+50+50) x200", 200, h =>
            {
                ImplicitBatch.SetEnabled(true);
                try
                {
                    for (int i = 0; i < 100; i++) ImplicitBatch.Add(ref emptyJob);
                    for (int i = 100; i < 150; i++) ImplicitBatch.AddFor(ref emptyFor, 1024);
                    for (int i = 150; i < 200; i++) ImplicitBatch.AddParallelFor(ref emptyPar, 8192, 0);
                    ImplicitBatch.EndFrame();   // 帧 barrier：一次 ScheduleBatch 提交 + 统一唤醒
                    for (int i = 0; i < 200; i++) h[i] = ImplicitBatch.Handle(i);
                }
                finally
                {
                    ImplicitBatch.SetEnabled(false); // 防影响其他 case
                }
            }));

            // ---- Native 隐式批：透明收集（SetImplicitBatchEnabled(true) + Schedule 照旧 + EndFrame）。
            //     仅 tile 路径 job（ParallelFor）进 native pending；IJob/IJobFor 不收集（即时提交），不聚合。 ----
            cases.Add(Case("NativeImplicit·Mixed(100+50+50) x200", 200, h =>
            {
                NativeJobScheduler.SetImplicitBatchEnabled(true);
                try
                {
                    for (int i = 0; i < 100; i++) h[i] = emptyJob.Schedule();
                    for (int i = 100; i < 150; i++) h[i] = emptyFor.Schedule(1024);
                    for (int i = 150; i < 200; i++) h[i] = emptyPar.Schedule(8192, 0);
                    NativeJobScheduler.EndFrame();   // native force point：pending 统一提交 + 单次唤醒
                }
                finally
                {
                    NativeJobScheduler.SetImplicitBatchEnabled(false); // 关 native（排空）→ 转接 C# 层，防影响其他 case
                    ImplicitBatch.SetEnabled(false);                    // 再关 C# 层，保持基准环境干净
                }
            }));

            return cases;
        }

        // ============================================================
        // 测量
        // ============================================================
        private sealed class CaseResult
        {
            public string Label = "";
            public int N;
            public double AvgFrameMs, P50FrameMs, P95FrameMs, MaxFrameMs;
            public double AvgScheduleMs, AvgCompleteMs;
            public double AllocBytesPerJob;
            public NativeJobSystemStats Clean;
            public NativeJobSystemStats Timing;
        }

        private static double Percentile(double[] sorted, double p)
        {
            if (sorted.Length == 0) return 0;
            double idx = (sorted.Length - 1) * p;
            int lo = (int)Math.Floor(idx);
            int hi = (int)Math.Ceiling(idx);
            return sorted[lo] + (sorted[hi] - sorted[lo]) * (idx - lo);
        }

        private void RunFrames(BenchCase c, int warmup, int measure, bool timingEnabled,
            double[]? totalSamples, double[]? scheduleSamples, double[]? completeSamples)
        {
            if (timingEnabled)
                NativeJobScheduler.SetTimingDiagnosticsEnabled(true);
            var handles = new JobHandle[Math.Max(16, c.N + 1)];

            for (int frame = 0; frame < warmup; frame++)
            {
                c.Schedule(handles);
                c.Complete(handles);
            }

            for (int frame = 0; frame < measure; frame++)
            {
                long t0 = Stopwatch.GetTimestamp();
                c.Schedule(handles);
                long t1 = Stopwatch.GetTimestamp();
                c.Complete(handles);
                long t2 = Stopwatch.GetTimestamp();

                if (totalSamples != null) totalSamples[frame] = (t2 - t0) * 1000.0 / Stopwatch.Frequency;
                if (scheduleSamples != null) scheduleSamples[frame] = (t1 - t0) * 1000.0 / Stopwatch.Frequency;
                if (completeSamples != null) completeSamples[frame] = (t2 - t1) * 1000.0 / Stopwatch.Frequency;
            }

            if (timingEnabled)
                NativeJobScheduler.SetTimingDiagnosticsEnabled(false);
        }

        private CaseResult MeasureCase(BenchCase c)
        {
            var result = new CaseResult { Label = c.Label, N = c.N };

            // ---- Pass A：干净墙钟（timing OFF） ----
            NativeJobScheduler.ResetStats();
            long allocBefore = GC.GetAllocatedBytesForCurrentThread();
            var total = new double[_measureFrames];
            var sched = new double[_measureFrames];
            var compl = new double[_measureFrames];
            RunFrames(c, _warmupFrames, _measureFrames, timingEnabled: false, total, sched, compl);
            long allocAfter = GC.GetAllocatedBytesForCurrentThread();
            result.Clean = NativeJobScheduler.GetStats();
            result.AllocBytesPerJob = (allocAfter - allocBefore) / (double)(_measureFrames * c.N);

            Array.Sort(total);
            Array.Sort(sched);
            Array.Sort(compl);
            result.AvgFrameMs = total.Average();
            result.P50FrameMs = Percentile(total, 0.50);
            result.P95FrameMs = Percentile(total, 0.95);
            result.MaxFrameMs = total[^1];
            result.AvgScheduleMs = sched.Average();
            result.AvgCompleteMs = compl.Average();

            // ---- Pass B：timing ON（execSpan/maxRange 依赖诊断），帧数按 2048 采样上限自适应 ----
            int timingFrames = Math.Clamp(2048 / Math.Max(1, c.N), 10, 40);
            NativeJobScheduler.ResetStats();
            RunFrames(c, 2, timingFrames, timingEnabled: true, null, null, null);
            result.Timing = NativeJobScheduler.GetStats();

            PrintCase(result);
            return result;
        }

        private void PrintCase(CaseResult r)
        {
            var s = r.Clean;
            var t = r.Timing;
            double perJobUs = r.AvgFrameMs * 1000.0 / r.N;
            double schedPerJobUs = r.AvgScheduleMs * 1000.0 / r.N;
            double complPerJobUs = r.AvgCompleteMs * 1000.0 / r.N;

            Console.WriteLine();
            Console.WriteLine($"== [{r.Label}] N={r.N} ==");
            Console.WriteLine($" frame avg={r.AvgFrameMs:F3} ms  p50={r.P50FrameMs:F3}  p95={r.P95FrameMs:F3}  max={r.MaxFrameMs:F3}   per-job={perJobUs:F1} us");
            Console.WriteLine($"   schedule={r.AvgScheduleMs:F3} ms ({schedPerJobUs:F1} us/job)   complete={r.AvgCompleteMs:F3} ms ({complPerJobUs:F1} us/job)   alloc={r.AllocBytesPerJob:F1} B/job(主线程)");
            ulong frames = (ulong)Math.Max(1, _measureFrames);
            Console.WriteLine($"   [native] batches={s.PublishedJobs / frames}/frame  tiles={(double)s.TotalTilesPublished / frames / r.N:F1}/job  participants={(double)s.FrameTasksSubmitted / frames / r.N:F1}/job");
            Console.WriteLine($"   [native] workersPeak={s.ActiveWorkersPeak}  waitFallbacks={s.WaitFallbacks}  hotSpin={s.HotSpinHits}  parkWake={s.ParkWakeCount}(字段未维护)  notified={s.NotifiedWorkers}(字段未维护)");
            Console.WriteLine($"   [timing] batchTotal P50/P95/Max={t.BatchTotalP50Ns / 1000.0:F1}/{t.BatchTotalP95Ns / 1000.0:F1}/{t.BatchTotalMaxNs / 1000.0:F1} us  (samples={t.TimingSampleCount}, dropped={t.TimingSamplesDropped})");
            Console.WriteLine($"   [timing] submit2First P50={t.SubmitToFirstWorkerP50Ns / 1000.0:F1}  spread P50={t.WorkerStartSpreadP50Ns / 1000.0:F1}  execSpan P50/P95={t.ExecutionSpanP50Ns / 1000.0:F1}/{t.ExecutionSpanP95Ns / 1000.0:F1}  maxRange P50={t.MaxRangeP50Ns / 1000.0:F1} us");
            if (t.SlowBatchId != 0)
                Console.WriteLine($"   [timing] slowBatch#{t.SlowBatchId} total={t.SlowBatchTotalNs / 1000.0:F1} submit2First={t.SlowSubmitToFirstWorkerNs / 1000.0:F1} spread={t.SlowWorkerStartSpreadNs / 1000.0:F1} execSpan={t.SlowExecutionSpanNs / 1000.0:F1} maxRange={t.SlowMaxRangeNs / 1000.0:F1} us");
        }

        private void PrintSummary(IReadOnlyList<CaseResult> results)
        {
            Console.WriteLine();
            Console.WriteLine("===== 汇总：每 job 调度流程成本归因 =====");
            Console.WriteLine($"{"case",-32}{"per-job",9}{"sched",9}{"compl",8}{"batchP50",9}{"wakeP50",9}{"execP50",9}{"tiles",7}{"parts",7}");
            foreach (var r in results)
            {
                var t = r.Timing;
                Console.WriteLine($"{r.Label,-32}{r.AvgFrameMs * 1000.0 / r.N,7:F1}us{r.AvgScheduleMs * 1000.0 / r.N,7:F1}us{r.AvgCompleteMs * 1000.0 / r.N,7:F1}us{t.BatchTotalP50Ns / 1000.0,8:F1}us{t.SubmitToFirstWorkerP50Ns / 1000.0,8:F1}us{t.ExecutionSpanP50Ns / 1000.0,8:F1}us{(double)r.Clean.TotalTilesPublished / _measureFrames / r.N,6:F1}{(double)r.Clean.FrameTasksSubmitted / _measureFrames / r.N,6:F1}");
            }
        }

        // === 仅 Managed 后端（NativeDll 缺失回退）的批烟测 ===
    public struct ManagedTouchJob : IJob
    {
        public NativeArray<long> Data;
        public int Index;
        public void Execute() => Data[Index] = Data[Index] + 1;
    }

    internal static void ManagedBatchSmokeTest()
    {
        Console.WriteLine(" Managed 后端批烟测：BatchScope(Add/Submit/CompleteAll) + ImplicitBatch(Add/EndFrame)");
        var data = new NativeArray<long>(64, Allocator.Persistent);

        // BatchScope：Managed 后端 Add 即调度存句柄，Submit 立即返回
        using (var b = new BatchScope())
        {
            for (int i = 0; i < 64; i++)
            {
                var job = new ManagedTouchJob { Data = data, Index = i };
                b.Add(ref job);
            }
            var hs = b.Submit();
            foreach (var h in hs) h.Complete();
            bool ok = hs.Length == 64;
            for (int i = 0; i < 64; i++)
                if (data[i] != 1) { ok = false; break; }
            Console.WriteLine($" {"BatchScope.Managed":-24}: {(ok ? "OK (64/64 执行+校验通过)" : "ERROR")}");
        }

        // ImplicitBatch：Managed 下 Add → EndFrame 取句柄 → CompleteAll
        data[0] = 0;
        ImplicitBatch.SetEnabled(true);
        try
        {
            for (int i = 0; i < 64; i++)
            {
                var job = new ManagedTouchJob { Data = data, Index = i };
                ImplicitBatch.Add(ref job);
            }
            ImplicitBatch.EndFrame();
            ImplicitBatch.CompleteAll();
            bool ok2 = ImplicitBatch.HandleCount == 64;
            for (int i = 0; i < 64; i++)
                if (data[i] != (i == 0 ? 1 : 2)) { ok2 = false; break; }
            Console.WriteLine($" {"ImplicitBatch.Managed":-24}: {(ok2 ? "OK" : "ERROR")}");
        }
        finally { ImplicitBatch.SetEnabled(false); }
        data.Dispose();
        Console.WriteLine(" Managed 后端批烟测完成");
    }

    // ============================================================
    // 主流程
    // ============================================================
        public void Run()
        {
            NativeJobScheduler.PrewakeWorkersOnce();

            Console.WriteLine();
            Console.WriteLine("=== ManyJobsBench：几百个 Job/帧 纯调度流程开销 ===");
            if (!JobScheduler.IsNative)
            {
                // NativeDll 缺失（Managed 回退后端）：只做批烟测，跳过原生 N 项对比。
                // （必须先于任何 NativeJobScheduler 访问——其 getter 在 Managed 下会抛。）
                Console.WriteLine(" Workers=Managed（NativeDll 缺失回退）");
                ManagedBatchSmokeTest();
                return;
            }
            Console.WriteLine($" Workers={NativeJobScheduler.JobWorkerCount}, Warmup={_warmupFrames}, Measure={_measureFrames}");
            Console.WriteLine(" 说明：4 类型空体；每帧 Schedule×N + Complete×N；Pass A 墙钟(timing off)，Pass B 批次分布(timing on)");
            Console.WriteLine(" 路径：全部 Schedule 一律异步（2026-08-30 起）；IJob=池任务、IJobFor≤64=池任务/>64=单 worker 异步、IJobParallelFor=tile 路径、IJobChunk=ECS tile 路径；Run 直执(ImmediateNative)为唯一同步路径。");

            var cases = BuildCases();
            var results = new List<CaseResult>();
            foreach (var c in cases)
                results.Add(MeasureCase(c));

            PrintSummary(results);
        }
    }
}
