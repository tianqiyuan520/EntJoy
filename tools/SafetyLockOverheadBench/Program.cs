using System;
using System.Diagnostics;
using System.Threading;
using EntJoy.Collections;
using EntJoy.JobSystem;

namespace SafetyLockOverheadBench
{
    // 与 SpritesRandomMove.NativeMoveJob 逐行同构（每元素 4 读 + 4 写）
    internal struct MoveIndexer : IJobParallelFor
    {
        public NativeArray<float> PositionsX;
        public NativeArray<float> PositionsY;
        public NativeArray<float> VelocitiesX;
        public NativeArray<float> VelocitiesY;
        public float Dt;
        public float ViewportWidth;
        public float ViewportHeight;

        public void Execute(int index)
        {
            float px = PositionsX[index];
            float py = PositionsY[index];
            float vx = VelocitiesX[index];
            float vy = VelocitiesY[index];

            px += vx * Dt;
            py += vy * Dt;

            if (px < 0f || px > ViewportWidth) vx = -vx;
            if (py < 0f || py > ViewportHeight) vy = -vy;

            PositionsX[index] = px;
            PositionsY[index] = py;
            VelocitiesX[index] = vx;
            VelocitiesY[index] = vy;
        }
    }

    // 同上，热循环外只取一次 Span
    internal struct MoveSpan : IJobParallelFor
    {
        public NativeArray<float> PositionsX;
        public NativeArray<float> PositionsY;
        public NativeArray<float> VelocitiesX;
        public NativeArray<float> VelocitiesY;
        public float Dt;
        public float ViewportWidth;
        public float ViewportHeight;

        public void Execute(int index)
        {
            Span<float> positionsX = PositionsX.AsSpan();
            Span<float> positionsY = PositionsY.AsSpan();
            Span<float> velocitiesX = VelocitiesX.AsSpan();
            Span<float> velocitiesY = VelocitiesY.AsSpan();

            float px = positionsX[index];
            float py = positionsY[index];
            float vx = velocitiesX[index];
            float vy = velocitiesY[index];

            px += vx * Dt;
            py += vy * Dt;

            if (px < 0f || px > ViewportWidth) vx = -vx;
            if (py < 0f || py > ViewportHeight) vy = -vy;

            positionsX[index] = px;
            positionsY[index] = py;
            velocitiesX[index] = vx;
            velocitiesY[index] = vy;
        }
    }

    // 同容器、同 job 内反复索引（读）——RegisterRead 快路径的目标形态
    internal struct RepeatIndexJob : IJobParallelFor
    {
        public NativeArray<float> Data;
        public int Reps;
        public void Execute(int index)
        {
            float acc = 0f;
            for (int r = 0; r < Reps; r++) acc += Data[index];
            Program.Sink((long)acc);
        }
    }

    // 同上走 Span —— 「检查开销全消」的上界参照
    internal struct RepeatSpanJob : IJobParallelFor
    {
        public NativeArray<float> Data;
        public int Reps;
        public void Execute(int index)
        {
            Span<float> d = Data.AsSpan();
            float acc = 0f;
            for (int r = 0; r < Reps; r++) acc += d[index];
            Program.Sink((long)acc);
        }
    }

    internal static class Program
    {
        private const int EntityCount = 1 << 20;   // 1,048,576
        private const int InnerBatch = 65536;      // 与 SpritesRandomMove 的 Schedule(EntityCount, 65536) 同构
        private const int Reps = 20;
        private const int Rounds = 5;
        private const int RepeatReps = 64;

        private static long _sink;
        internal static void Sink(long v) => Volatile.Write(ref _sink, v);

        private static void Main()
        {
            JobScheduler.Initialize();
            Console.WriteLine($"backend={(JobScheduler.IsNative ? "Native" : "Managed")} workers={JobScheduler.WorkerCount} entities={EntityCount} reps={Reps} batch={InnerBatch}");

            var px = new NativeArray<float>(EntityCount, Allocator.Persistent);
            var py = new NativeArray<float>(EntityCount, Allocator.Persistent);
            var vx = new NativeArray<float>(EntityCount, Allocator.Persistent);
            var vy = new NativeArray<float>(EntityCount, Allocator.Persistent);
            for (int i = 0; i < EntityCount; i++) { px[i] = 100f; py[i] = 100f; vx[i] = 1.5f; vy[i] = 1f; }

            for (int w = 0; w < 2; w++) { RunMove(px, py, vx, vy, false); RunMove(px, py, vx, vy, true); }

            var idx = new double[Rounds];
            var spn = new double[Rounds];
            for (int r = 0; r < Rounds; r++)
            {
                idx[r] = RunMove(px, py, vx, vy, false);
                spn[r] = RunMove(px, py, vx, vy, true);
            }
            Array.Sort(idx); Array.Sort(spn);

            double idxMed = idx[Rounds / 2];
            double spnMed = spn[Rounds / 2];
            long entities = (long)EntityCount * Reps;
            double accesses = entities * 8;

            Console.WriteLine();
            Console.WriteLine("=== NativeMoveJob 同构（每元素 2 读 2 写）===");
            Console.WriteLine("indexer ms: " + string.Join(", ", Array.ConvertAll(idx, x => x.ToString("F3"))));
            Console.WriteLine("span    ms: " + string.Join(", ", Array.ConvertAll(spn, x => x.ToString("F3"))));
            Console.WriteLine($"indexer 中位: {idxMed,9:F3} ms  ({idxMed * 1e6 / entities,7:F2} ns/实体, {idxMed * 1e6 / accesses,6:F2} ns/访问)");
            Console.WriteLine($"span    中位: {spnMed,9:F3} ms  ({spnMed * 1e6 / entities,7:F2} ns/实体, {spnMed * 1e6 / accesses,6:F2} ns/访问)");
            Console.WriteLine($"加速: {idxMed / Math.Max(spnMed, 1e-9),6:F2}x   每实体省 {(idxMed - spnMed) * 1e6 / entities,7:F2} ns   残余检查 {(idxMed - spnMed) * 1e6 / accesses,5:F2} ns/访问");

            for (int rep = 0; rep < 2; rep++) { RunRepeat(px, false); RunRepeat(px, true); }
            var ri = new double[Rounds];
            var rs = new double[Rounds];
            for (int r = 0; r < Rounds; r++)
            {
                ri[r] = RunRepeat(px, false);
                rs[r] = RunRepeat(px, true);
            }
            Array.Sort(ri); Array.Sort(rs);
            double riMed = ri[Rounds / 2];
            double rsMed = rs[Rounds / 2];
            double perAccess = (double)EntityCount * RepeatReps;

            Console.WriteLine();
            Console.WriteLine("=== 同容器反复索引（RegisterRead 快路径目标）===");
            Console.WriteLine("indexer ms: " + string.Join(", ", Array.ConvertAll(ri, x => x.ToString("F3"))));
            Console.WriteLine("span    ms: " + string.Join(", ", Array.ConvertAll(rs, x => x.ToString("F3"))));
            Console.WriteLine($"indexer 中位 {riMed,8:F3} ms = {riMed * 1e6 / perAccess,7:F2} ns/访问");
            Console.WriteLine($"span    中位 {rsMed,8:F3} ms = {rsMed * 1e6 / perAccess,7:F2} ns/访问");
            Console.WriteLine($"  ⟹ 每次索引残余检查开销 ≈ {(riMed - rsMed) * 1e6 / perAccess,6:F2} ns");

            Console.WriteLine();
            Console.WriteLine($"结果校验 px[0]={px[0]:F3} py[0]={py[0]:F3} vx[0]={vx[0]:F3} vy[0]={vy[0]:F3}");

            JobScheduler.Shutdown();
            px.Dispose(); py.Dispose(); vx.Dispose(); vy.Dispose();
        }

        private static double RunMove(NativeArray<float> px, NativeArray<float> py, NativeArray<float> vx, NativeArray<float> vy, bool useSpan)
        {
            double best = double.MaxValue;
            for (int r = 0; r < Reps; r++)
            {
                double ms;
                if (useSpan)
                {
                    var job = new MoveSpan
                    {
                        PositionsX = px, PositionsY = py, VelocitiesX = vx, VelocitiesY = vy,
                        Dt = 0.016f, ViewportWidth = 1e9f, ViewportHeight = 1e9f
                    };
                    ms = Time(ref job);
                }
                else
                {
                    var job = new MoveIndexer
                    {
                        PositionsX = px, PositionsY = py, VelocitiesX = vx, VelocitiesY = vy,
                        Dt = 0.016f, ViewportWidth = 1e9f, ViewportHeight = 1e9f
                    };
                    ms = Time(ref job);
                }
                best = Math.Min(best, ms);
            }
            return best;
        }

        private static double RunRepeat(NativeArray<float> data, bool useSpan)
        {
            double best = double.MaxValue;
            for (int r = 0; r < 3; r++)
            {
                double ms;
                if (useSpan)
                {
                    var j = new RepeatSpanJob { Data = data, Reps = RepeatReps };
                    ms = Time(ref j);
                }
                else
                {
                    var j = new RepeatIndexJob { Data = data, Reps = RepeatReps };
                    ms = Time(ref j);
                }
                best = Math.Min(best, ms);
            }
            return best;
        }

        private static double Time<T>(ref T job) where T : struct, IJobParallelFor
        {
            var h = JobScheduler.ScheduleParallelFor(ref job, EntityCount, InnerBatch);
            var sw = Stopwatch.StartNew();
            h.Complete();
            sw.Stop();
            return sw.Elapsed.TotalMilliseconds;
        }
    }
}
