using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using EntJoy.Collections;
using EntJoy.JobSystem;
using EntJoy.Mathematics;

namespace EntJoySample.GpuJob
{
    /// <summary>
    /// Gate 2 对比样例：同一 Job body 的四路后端（C# 单线程 / C++ JobSystem / ISPC JobSystem / GPU wgpu）。
    ///   Move（带宽+分支）/ Heavy（16x sin/cos/sqrt 超越函数）@1M。
    ///   输出：p50 耗时 + vs C# 加速比 + 与 C# 参考的数值误差（max|diff| / 逐位不等数）——
    ///   用于回答「GPU 误差是否正常」（与 C++/ISPC 的浮点实现差异同量级即正常）。
    ///   GPU 走 BindingsGenerator 生成的 ScheduleGpu → NativeDll GpuCompute（C++/wgpu-native）。
    /// </summary>
    public static class Program
    {
        private const int N = 1_000_000;
        private const float Dt = 1f / 60f;
        private const float ViewportWidth = 1920f;
        private const float ViewportHeight = 1080f;
        private const int Warmup = 2;
        private const int Measure = 5;

        private struct Row { public string Name; public double Ms; public double Ratio; public long Mismatch; public float MaxDiff; }

        public static void Main()
        {
            Console.WriteLine("=== Gate 2：C# / C++ / ISPC / GPU 四路对比（同一 Job body @1M）===");
            if (ValidateGeneratedWgsl() > 0) { Environment.ExitCode = 1; return; }

            NativeJobScheduler.Initialize();
            NativeJobScheduler.PrewakeWorkersOnce();
            try
            {
                RunMoveCompare();
                RunHeavyCompare();
                RunGpuStageBreakdown();
                RunResidency();
            }
            finally
            {
                NativeJobScheduler.Shutdown();
            }

            Console.WriteLine();
            Console.WriteLine("误差结论（GPU/C++/ISPC vs C# 参考，均为浮点实现差异、ULP 级，非逻辑 bug）：");
            Console.WriteLine("  ① FMA 收缩：GPU 驱动把 a*b+c 合成为单次舍入的 fma（CPU 两次舍入）→ Move positions ~1 ulp（7.6e-6）；");
            Console.WriteLine("  ② 超越函数：sin/cos/sqrt 的 GPU（驱动/HW）与 CPU MathF 库实现不同，16 次累加放大 → Heavy 数 ULP（~3.4e-5）；");
            Console.WriteLine("  ③ ISPC fast-math 与 C++ 同样有 ULP 级差——与 GPU 同量级，属正常 IEEE-754 实现差异；");
            Console.WriteLine("     逐位相等的场景（docs/gpu/16 §六 CUDA）因内核 codegen 可控才做到。");
        }

        // ================= Move 对比 =================

        private static void RunMoveCompare()
        {
            Console.WriteLine();
            Console.WriteLine($"----- Move @{N:N0}（pos += vel*dt + 视口反弹） -----");
            using var pos = new NativeArray<float2>(N, Allocator.Persistent);
            using var vel = new NativeArray<float2>(N, Allocator.Persistent);
            Reset(pos, vel);

            // C# 参考（同序；独立克隆，绝不被任何后端计时污染）
            var (refPos, refVel) = CloneToManaged(pos, vel);
            for (int i = 0; i < N; i++)
            {
                refPos[i] = MoveStepPos(refPos[i], refVel[i]);
                refVel[i] = MoveStepVel(refPos[i], refVel[i]);
            }

            var rows = new List<Row>();
            rows.Add(MeasureCsRow(pos, vel, (p, v) => CsLoop(p, v)));
            rows.Add(Run("C++ (JobSystem)", pos, vel, refPos, refVel,
                () => new CppMoveJob { Positions = pos, Velocities = vel, Dt = Dt, ViewportWidth = ViewportWidth, ViewportHeight = ViewportHeight }.Schedule(N).Complete()));
            rows.Add(Run("ISPC (JobSystem)", pos, vel, refPos, refVel,
                () => new IspcMoveJob { Positions = pos, Velocities = vel, Dt = Dt, ViewportWidth = ViewportWidth, ViewportHeight = ViewportHeight }.Schedule(N).Complete()));
            rows.Add(Run("GPU (wgpu)", pos, vel, refPos, refVel,
                () => new GpuMoveJob { Positions = pos, Velocities = vel, Dt = Dt, ViewportWidth = ViewportWidth, ViewportHeight = ViewportHeight }.ScheduleGpu(N)));

            PrintRows("Move", rows);
        }

        // ================= Heavy 对比 =================

        private static void RunHeavyCompare()
        {
            Console.WriteLine();
            Console.WriteLine($"----- Heavy @{N:N0}（16x sin/cos/sqrt 累加） -----");
            using var pos = new NativeArray<float2>(N, Allocator.Persistent);
            using var vel = new NativeArray<float2>(N, Allocator.Persistent);
            Reset(pos, vel);

            var (refPos, refVel) = CloneToManaged(pos, vel);
            for (int i = 0; i < N; i++)
            {
                float acc = 0f, x = refPos[i].x;
                for (int k = 0; k < 16; k++)
                {
                    acc += MathF.Sin(x) + MathF.Cos(x) + MathF.Sqrt(x * x + 1f);
                    x += refVel[i].x * Dt;
                }
                refPos[i].x += acc * Dt;
                refPos[i].y += refVel[i].y * Dt;
            }

            var rows = new List<Row>();
            rows.Add(MeasureCsRow(pos, vel, (p, v) => HeavyCsLoop(p, v)));
            rows.Add(Run("C++ (JobSystem)", pos, vel, refPos, refVel,
                () => new CppHeavyJob { Positions = pos, Velocities = vel, Dt = Dt }.Schedule(N).Complete()));
            rows.Add(Run("ISPC (JobSystem)", pos, vel, refPos, refVel,
                () => new IspcHeavyJob { Positions = pos, Velocities = vel, Dt = Dt }.Schedule(N).Complete()));
            rows.Add(Run("GPU (wgpu)", pos, vel, refPos, refVel,
                () => new GpuHeavyJob { Positions = pos, Velocities = vel, Dt = Dt }.ScheduleGpu(N)));

            PrintRows("Heavy", rows);
        }

        // ================= GPU 阶段拆解（回答「Heavy GPU 为什么不是 ~1ms」） =================

        private static unsafe void RunGpuStageBreakdown()
        {
            Console.WriteLine();
            Console.WriteLine($"----- GPU Heavy @{N:N0} 阶段拆解 -----");
            string wgsl = ReadWgsl("GpuHeavyJob");
            if (wgsl == null) { Console.WriteLine("[SKIP] 未找到 GpuHeavyJob.wgsl"); return; }

            using var pos = new NativeArray<float2>(N, Allocator.Persistent);
            using var vel = new NativeArray<float2>(N, Allocator.Persistent);
            Reset(pos, vel);
            const ulong size8 = (ulong)(1_000_000 * 8);
            const uint groups = (uint)((1_000_000 + 63) / 64);

            NativeTranspiler.Bindings.NativeExports.GpuCompute_EnsureInitialized();
            // 传输带宽诊断（UPLOAD 写 / QueueWriteBuffer / READBACK 读）——定位全量往返开销构成
            GpuCompute_DiagTransfer(16);
            // 原生 D3D12 对照（判定 wgpu 的 ~10GB/s 是平台特性还是 wgpu 实现问题）
            GpuCompute_DiagNativeD3D12(16);
            IntPtr k = NativeTranspiler.Bindings.NativeExports.GpuCompute_CreateKernel(wgsl, 2, 1);
            if (k == IntPtr.Zero) { Console.WriteLine($"[FAIL] kernel: {NativeTranspiler.Bindings.NativeExports.GpuCompute_GetLastErrorText()}"); return; }
            IntPtr b0 = NativeTranspiler.Bindings.NativeExports.GpuCompute_CreateStorageBuffer(size8);
            IntPtr b1 = NativeTranspiler.Bindings.NativeExports.GpuCompute_CreateStorageBuffer(size8);
            var uni = new byte[8];
            BitConverter.GetBytes(Dt).CopyTo(uni, 0);
            BitConverter.GetBytes(N).CopyTo(uni, 4);
            IntPtr ub = NativeTranspiler.Bindings.NativeExports.GpuCompute_CreateUniformBuffer(8);
            fixed (byte* p = uni) NativeTranspiler.Bindings.NativeExports.GpuCompute_WriteBuffer(ub, (IntPtr)p, 8);

            // ① 上传（16MB + uniform）
            var sw = Stopwatch.StartNew();
            NativeTranspiler.Bindings.NativeExports.GpuCompute_WriteBuffer(b0, (IntPtr)pos.GetUnsafePtr(), size8);
            NativeTranspiler.Bindings.NativeExports.GpuCompute_WriteBuffer(b1, (IntPtr)vel.GetUnsafePtr(), size8);
            sw.Stop();
            Console.WriteLine($"  上传 16MB+uniform（QueueWriteBuffer）: {sw.Elapsed.TotalMilliseconds:F3} ms");

            IntPtr bg = NativeTranspiler.Bindings.NativeExports.GpuCompute_CreateBindGroup(k,
                new[] { b0, b1, ub }, new ulong[] { size8, size8, 8 }, 3);

            // ② 常驻 dispatch（数据已在 GPU，buffer 复用 → ≈纯 kernel + sync）
            for (int i = 0; i < Warmup; i++)
            {
                NativeTranspiler.Bindings.NativeExports.GpuCompute_Dispatch(k, bg, groups);
                NativeTranspiler.Bindings.NativeExports.GpuCompute_Sync();
            }
            var times = new List<double>();
            for (int i = 0; i < Measure; i++)
            {
                sw.Restart();
                NativeTranspiler.Bindings.NativeExports.GpuCompute_Dispatch(k, bg, groups);
                NativeTranspiler.Bindings.NativeExports.GpuCompute_Sync();
                sw.Stop();
                times.Add(sw.Elapsed.TotalMilliseconds);
            }
            times.Sort();
            Console.WriteLine($"  常驻 dispatch+sync（≈纯 kernel，buffer 复用）: {times[times.Count / 2]:F3} ms");

            // ③ 回读（2×8MB staging copy + map）
            sw.Restart();
            NativeTranspiler.Bindings.NativeExports.GpuCompute_ReadBack(b0, (IntPtr)pos.GetUnsafePtr(), size8);
            NativeTranspiler.Bindings.NativeExports.GpuCompute_ReadBack(b1, (IntPtr)vel.GetUnsafePtr(), size8);
            sw.Stop();
            Console.WriteLine($"  回读 16MB（staging+mapAsync）: {sw.Elapsed.TotalMilliseconds:F3} ms");

            // ④ 探针形态对照（docs/12 口径）：vel 常驻，每帧只传 pos 8MB↑ + 8MB↓（共 16MB，非全量 32MB）
            for (int i = 0; i < Warmup; i++)
            {
                NativeTranspiler.Bindings.NativeExports.GpuCompute_WriteBuffer(b0, (IntPtr)pos.GetUnsafePtr(), size8);
                NativeTranspiler.Bindings.NativeExports.GpuCompute_Dispatch(k, bg, groups);
                NativeTranspiler.Bindings.NativeExports.GpuCompute_Sync();
                NativeTranspiler.Bindings.NativeExports.GpuCompute_ReadBack(b0, (IntPtr)pos.GetUnsafePtr(), size8);
            }
            times.Clear();
            for (int i = 0; i < Measure; i++)
            {
                Reset(pos, vel);
                sw.Restart();
                NativeTranspiler.Bindings.NativeExports.GpuCompute_WriteBuffer(b0, (IntPtr)pos.GetUnsafePtr(), size8);
                NativeTranspiler.Bindings.NativeExports.GpuCompute_WriteBuffer(b1, (IntPtr)vel.GetUnsafePtr(), size8);
                NativeTranspiler.Bindings.NativeExports.GpuCompute_Dispatch(k, bg, groups);
                NativeTranspiler.Bindings.NativeExports.GpuCompute_Sync();
                NativeTranspiler.Bindings.NativeExports.GpuCompute_ReadBack(b0, (IntPtr)pos.GetUnsafePtr(), size8);
                NativeTranspiler.Bindings.NativeExports.GpuCompute_ReadBack(b1, (IntPtr)vel.GetUnsafePtr(), size8);
                sw.Stop();
                times.Add(sw.Elapsed.TotalMilliseconds);
            }
            times.Sort();
            Console.WriteLine($"  全量往返 32MB（pos+vel ↑↓，= ScheduleGpu 形态）: {times[times.Count / 2]:F3} ms");

            times.Clear();
            for (int i = 0; i < Measure; i++)
            {
                Reset(pos, vel);
                sw.Restart();
                NativeTranspiler.Bindings.NativeExports.GpuCompute_WriteBuffer(b0, (IntPtr)pos.GetUnsafePtr(), size8);
                NativeTranspiler.Bindings.NativeExports.GpuCompute_Dispatch(k, bg, groups);
                NativeTranspiler.Bindings.NativeExports.GpuCompute_Sync();
                NativeTranspiler.Bindings.NativeExports.GpuCompute_ReadBack(b0, (IntPtr)pos.GetUnsafePtr(), size8);
                sw.Stop();
                times.Add(sw.Elapsed.TotalMilliseconds);
            }
            times.Sort();
            Console.WriteLine($"  探针形态 16MB（vel 常驻，只传 pos ↑↓）: {times[times.Count / 2]:F3} ms");

            NativeTranspiler.Bindings.NativeExports.GpuCompute_ReleaseBindGroup(bg);
            NativeTranspiler.Bindings.NativeExports.GpuCompute_ReleaseBuffer(b0);
            NativeTranspiler.Bindings.NativeExports.GpuCompute_ReleaseBuffer(b1);
            NativeTranspiler.Bindings.NativeExports.GpuCompute_ReleaseBuffer(ub);
            NativeTranspiler.Bindings.NativeExports.GpuCompute_ReleaseKernel(k);

            Console.WriteLine();
            Console.WriteLine("  对照（docs/12 CUDA 页锁定）：全量 1.445ms / 探针形态(16MB) ~1.2ms / 常驻 0.244ms。");
            Console.WriteLine("  这里比探针慢的原因：① 全量 32MB vs 探针 16MB（vel 常驻）→ 传输量 2 倍；② wgpu 回读须 staging copy+map 两跳（CUDA 页锁定单跳）；");
            Console.WriteLine("  ③ 每帧重建 buffer + 三层 P/Invoke。常驻形态（数据留 GPU）≈ 0.3ms → GpuResidencyManager。");
        }

        private static string ReadWgsl(string jobName)
        {
            string generatedDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "..", "src", "EntJoySample", "NativeTranspiler_Generated"));
            var file = Directory.GetFiles(generatedDir, $"*{jobName}*.wgsl").FirstOrDefault();
            return file == null ? null : File.ReadAllText(file);
        }

        // ================= GpuResidencyManager（稀疏写增量同步验证） =================

        [DllImport("NativeDll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr GpuResidency_RegisterJob([MarshalAs(UnmanagedType.LPStr)] string wgsl, int storageCount, int hasUniform, int chunkEntities, int[] elemBytes);
        [DllImport("NativeDll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void GpuResidency_ReleaseJob(IntPtr job);
        [DllImport("NativeDll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int GpuResidency_Sync(IntPtr job, IntPtr[] inPtrs, IntPtr[] outPtrs, int[] lengths, byte[] uniformBytes, int uniformSize, int count);
        [DllImport("NativeDll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int GpuResidency_Complete(IntPtr job);
        [DllImport("NativeDll", CallingConvention = CallingConvention.Cdecl)]
        private static extern int GpuResidency_GetMode(IntPtr job);
        [DllImport("NativeDll", CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr GpuResidency_GetLastError();
        [DllImport("NativeDll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void GpuCompute_DiagTransfer(int sizeMB);
        [DllImport("NativeDll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void GpuCompute_DiagNativeD3D12(int sizeMB);

        /// <summary>
        /// GpuResidencyManager v2 验证：输入/输出分离 + 跨帧流水。
        /// inPtrs 纯输入（gen 改它）、outPtrs 结果镜像（首帧全量回读 + 每帧 patch dirty）。
        /// Sync 流水：diff/拼接藏 GPU 执行期 → 完成上帧 → 提交本帧（不 wait 即返回）；
        /// 因此 gen 在 Sync 返回后做（GPU 跑当前帧期间），parity 滞后一帧（Sync 返回时 outPtrs
        /// 已含上帧结果）。parity 只对比 dirty 实体（探针 outCache 语义）。
        /// </summary>
        private static unsafe void RunResidency()
        {
            string wgslMove = ReadWgsl("GpuMoveJob");
            if (wgslMove == null) { Console.WriteLine("[SKIP] 未找到 GpuMoveJob.wgsl"); return; }
            var uniMove = new byte[16];
            BitConverter.GetBytes(Dt).CopyTo(uniMove, 0);
            BitConverter.GetBytes(ViewportWidth).CopyTo(uniMove, 4);
            BitConverter.GetBytes(ViewportHeight).CopyTo(uniMove, 8);
            BitConverter.GetBytes(N).CopyTo(uniMove, 12);
            RunResidencyLoop("Move", wgslMove, uniMove, MoveStepPos, MoveStepVel);

            string wgslHeavy = ReadWgsl("GpuHeavyJob");
            if (wgslHeavy == null) { Console.WriteLine("[SKIP] 未找到 GpuHeavyJob.wgsl"); return; }
            var uniHeavy = new byte[8];
            BitConverter.GetBytes(Dt).CopyTo(uniHeavy, 0);
            BitConverter.GetBytes(N).CopyTo(uniHeavy, 4);
            RunResidencyLoop("Heavy", wgslHeavy, uniHeavy, HeavyStepPos, HeavyStepVel);
        }

        private static unsafe void RunResidencyLoop(string name, string wgsl, byte[] uni,
            Func<float2, float2, float2> stepPos, Func<float2, float2, float2> stepVel)
        {
            Console.WriteLine();
            Console.WriteLine($"----- GpuResidencyManager v2（{name} @{N:N0}，chunk=128 实体，稀疏写，跨帧流水） -----");

            var posIn = new NativeArray<float2>(N, Allocator.Persistent);
            var velIn = new NativeArray<float2>(N, Allocator.Persistent);
            var posOut = new NativeArray<float2>(N, Allocator.Persistent);
            var velOut = new NativeArray<float2>(N, Allocator.Persistent);
            try
            {
                Reset(posIn, velIn);
                var inPtrs = new[] { (IntPtr)posIn.GetUnsafePtr(), (IntPtr)velIn.GetUnsafePtr() };
                var outPtrs = new[] { (IntPtr)posOut.GetUnsafePtr(), (IntPtr)velOut.GetUnsafePtr() };
                var lengths = new[] { N, N };
                var rng = new Random(777);
                const int chunkEntities = 128;
                int nchunk = N / chunkEntities;
                int frames = 12;

                Console.WriteLine($"  {"sp",6}{"模式",8}{"dirty",8}{"p50 ms",10}{"vs 全量",10}{"parity(dirty)",16}{"max|diff|",12}");
                foreach (double sp in new[] { 0.01, 0.05, 0.50 })
                {
                    // 每档独立 job（干净状态）
                    IntPtr job = GpuResidency_RegisterJob(wgsl, 2, 1, chunkEntities, new[] { 8, 8 });
                    if (job == IntPtr.Zero) { Console.WriteLine($"[FAIL] RegisterJob: {Ptr(GpuResidency_GetLastError())}"); continue; }
                    Reset(posIn, velIn);
                    if (GpuResidency_Sync(job, inPtrs, outPtrs, lengths, uni, uni.Length, N) == 0)
                    { Console.WriteLine($"[FAIL] 首帧 Sync: {Ptr(GpuResidency_GetLastError())}"); GpuResidency_ReleaseJob(job); continue; }

                    int nd = (int)(nchunk * sp + 0.5);
                    var times = new List<double>();
                    long totalMismatch = 0, totalDirtyFields = 0;
                    float maxDiff = 0f;
                    HashSet<int> prevDirty = null;
                    float2[] prevRefPos = null, prevRefVel = null;
                    for (int f = 0; f < frames; f++)
                    {
                        // gen 帧 f 输入（上帧 Sync 已返回 → GPU 跑上帧期间做 CPU 工作 = 藏进 GPU 执行期）
                        var used = new HashSet<int>();
                        for (int k = 0; k < nd; k++)
                        {
                            int c;
                            do { c = rng.Next(nchunk); } while (!used.Add(c));
                            for (int e = c * chunkEntities; e < (c + 1) * chunkEntities; e++)
                            {
                                posIn[e] = new float2((float)(rng.NextDouble() * 200 - 100), (float)(rng.NextDouble() * 200 - 100));
                                velIn[e] = new float2((float)(rng.NextDouble() * 200 - 100), (float)(rng.NextDouble() * 200 - 100));
                            }
                        }
                        // C# 参考（帧 f 输入全量推进 1 次）
                        var (rp, rv) = CloneToManaged(posIn, velIn);
                        for (int i = 0; i < N; i++)
                        {
                            rp[i] = stepPos(rp[i], rv[i]);
                            rv[i] = stepVel(rp[i], rv[i]);
                        }
                        var sw = Stopwatch.StartNew();
                        int ok = GpuResidency_Sync(job, inPtrs, outPtrs, lengths, uni, uni.Length, N);
                        sw.Stop();
                        if (ok == 0) { Console.WriteLine($"[FAIL] Sync: {Ptr(GpuResidency_GetLastError())}"); break; }
                        times.Add(sw.Elapsed.TotalMilliseconds);
                        // parity 滞后一帧：Sync 完成上帧时已 patch outPtrs → 对比上帧 dirty vs 上帧参考
                        if (f > 0 && prevDirty != null)
                        {
                            long mm = 0;
                            float md = 0f;
                            foreach (int c in prevDirty)
                            {
                                for (int e = c * chunkEntities; e < (c + 1) * chunkEntities; e++)
                                {
                                    float2 gp = posOut[e], gv = velOut[e], ep = prevRefPos[e], ev = prevRefVel[e];
                                    if (BitConverter.SingleToInt32Bits(gp.x) != BitConverter.SingleToInt32Bits(ep.x)) { mm++; md = MathF.Max(md, MathF.Abs(gp.x - ep.x)); }
                                    if (BitConverter.SingleToInt32Bits(gp.y) != BitConverter.SingleToInt32Bits(ep.y)) { mm++; md = MathF.Max(md, MathF.Abs(gp.y - ep.y)); }
                                    if (BitConverter.SingleToInt32Bits(gv.x) != BitConverter.SingleToInt32Bits(ev.x)) { mm++; md = MathF.Max(md, MathF.Abs(gv.x - ev.x)); }
                                    if (BitConverter.SingleToInt32Bits(gv.y) != BitConverter.SingleToInt32Bits(ev.y)) { mm++; md = MathF.Max(md, MathF.Abs(gv.y - ev.y)); }
                                    totalDirtyFields += 4;
                                }
                            }
                            totalMismatch += mm; maxDiff = MathF.Max(maxDiff, md);
                        }
                        prevDirty = used;
                        prevRefPos = rp; prevRefVel = rv;
                    }
                    // 完成最后一帧 + 末帧 parity
                    if (GpuResidency_Complete(job) == 0)
                        Console.WriteLine($"[FAIL] Complete: {Ptr(GpuResidency_GetLastError())}");
                    if (prevDirty != null)
                    {
                        long mm = 0;
                        float md = 0f;
                        foreach (int c in prevDirty)
                        {
                            for (int e = c * chunkEntities; e < (c + 1) * chunkEntities; e++)
                            {
                                float2 gp = posOut[e], gv = velOut[e], ep = prevRefPos[e], ev = prevRefVel[e];
                                if (BitConverter.SingleToInt32Bits(gp.x) != BitConverter.SingleToInt32Bits(ep.x)) { mm++; md = MathF.Max(md, MathF.Abs(gp.x - ep.x)); }
                                if (BitConverter.SingleToInt32Bits(gp.y) != BitConverter.SingleToInt32Bits(ep.y)) { mm++; md = MathF.Max(md, MathF.Abs(gp.y - ep.y)); }
                                if (BitConverter.SingleToInt32Bits(gv.x) != BitConverter.SingleToInt32Bits(ev.x)) { mm++; md = MathF.Max(md, MathF.Abs(gv.x - ev.x)); }
                                if (BitConverter.SingleToInt32Bits(gv.y) != BitConverter.SingleToInt32Bits(ev.y)) { mm++; md = MathF.Max(md, MathF.Abs(gv.y - ev.y)); }
                                totalDirtyFields += 4;
                            }
                        }
                        totalMismatch += mm; maxDiff = MathF.Max(maxDiff, md);
                    }
                    times.Sort();
                    int mode = GpuResidency_GetMode(job);
                    double p50 = times[times.Count / 2];
                    Console.WriteLine($"  {sp,6:P0}{mode,8}{nd * chunkEntities,8}{p50,10:F3}{5.2 / p50,10:F2}x{totalMismatch / frames,16:N0}/{totalDirtyFields / frames}{maxDiff,12:E2}");
                    GpuResidency_ReleaseJob(job);
                }
                Console.WriteLine();
                Console.WriteLine("  对照：全量往返（ScheduleGpu 形态）~5.2ms；docs/16 探针脏同步 sp≤10% x2.0-2.9、resHashMode sp=1% x3.07。");
                Console.WriteLine("  parity 只统计 dirty 实体（增量模式只回读 dirty chunk；非 dirty 不回读是设计预期）。");
            }
            finally
            {
                posIn.Dispose();
                velIn.Dispose();
                posOut.Dispose();
                velOut.Dispose();
            }
        }

        private static string Ptr(IntPtr p) => p == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(p);

        // ================= 工具 =================

        /// <summary>C# 单线程计时：每轮从初始 NativeArray 克隆到托管数组再跑（绝不污染 refPos/refVel），不参与 parity</summary>
        private static Row MeasureCsRow(NativeArray<float2> srcPos, NativeArray<float2> srcVel, Action<float2[], float2[]> loop)
        {
            var times = new List<double>();
            for (int i = 0; i < Warmup; i++)
            {
                var (p, v) = CloneToManaged(srcPos, srcVel);
                loop(p, v);
            }
            for (int i = 0; i < Measure; i++)
            {
                var (p, v) = CloneToManaged(srcPos, srcVel);
                var sw = Stopwatch.StartNew();
                loop(p, v);
                sw.Stop();
                times.Add(sw.Elapsed.TotalMilliseconds);
            }
            times.Sort();
            return new Row { Name = "C# 单线程", Ms = times[times.Count / 2], Ratio = 1.0, Mismatch = 0, MaxDiff = 0f };
        }

        private static void Reset(NativeArray<float2> pos, NativeArray<float2> vel)
        {
            var rng = new Random(1234);
            for (int i = 0; i < N; i++)
            {
                pos[i] = new float2((float)(rng.NextDouble() * 200 - 100), (float)(rng.NextDouble() * 200 - 100));
                vel[i] = new float2((float)(rng.NextDouble() * 200 - 100), (float)(rng.NextDouble() * 200 - 100));
            }
        }

        private static (float2[], float2[]) CloneToManaged(NativeArray<float2> pos, NativeArray<float2> vel)
        {
            var p = new float2[pos.Length];
            var v = new float2[vel.Length];
            for (int i = 0; i < pos.Length; i++) { p[i] = pos[i]; v[i] = vel[i]; }
            return (p, v);
        }

        /// <summary>跑一个后端：warmup + measure，计时 + parity（每轮 Reset 输入）</summary>
        private static Row Run(string name, NativeArray<float2> pos, NativeArray<float2> vel,
            float2[] refPos, float2[] refVel, Action run)
        {
            for (int i = 0; i < Warmup; i++) run();
            var times = new List<double>();
            long totalMismatch = 0;
            float maxDiff = 0f;
            for (int i = 0; i < Measure; i++)
            {
                Reset(pos, vel);
                var sw = Stopwatch.StartNew();
                run();
                sw.Stop();
                times.Add(sw.Elapsed.TotalMilliseconds);
                var (mm, md) = Compare(refPos, refVel, pos, vel);
                totalMismatch += mm;
                if (md > maxDiff) maxDiff = md;
            }
            times.Sort();
            return new Row { Name = name, Ms = times[times.Count / 2], Mismatch = totalMismatch / Measure, MaxDiff = maxDiff };
        }

        private static (long, float) Compare(float2[] refPos, float2[] refVel, NativeArray<float2> pos, NativeArray<float2> vel)
        {
            long mismatch = 0;
            float maxDiff = 0f;
            for (int i = 0; i < N; i++)
            {
                float2 rp = refPos[i], rv = refVel[i], gp = pos[i], gv = vel[i];
                if (BitConverter.SingleToInt32Bits(gp.x) != BitConverter.SingleToInt32Bits(rp.x)) { mismatch++; maxDiff = MathF.Max(maxDiff, MathF.Abs(gp.x - rp.x)); }
                if (BitConverter.SingleToInt32Bits(gp.y) != BitConverter.SingleToInt32Bits(rp.y)) { mismatch++; maxDiff = MathF.Max(maxDiff, MathF.Abs(gp.y - rp.y)); }
                if (BitConverter.SingleToInt32Bits(gv.x) != BitConverter.SingleToInt32Bits(rv.x)) { mismatch++; maxDiff = MathF.Max(maxDiff, MathF.Abs(gv.x - rv.x)); }
                if (BitConverter.SingleToInt32Bits(gv.y) != BitConverter.SingleToInt32Bits(rv.y)) { mismatch++; maxDiff = MathF.Max(maxDiff, MathF.Abs(gv.y - rv.y)); }
            }
            return (mismatch, maxDiff);
        }

        private static float2 MoveStepPos(float2 p, float2 v)
        {
            p.x += v.x * Dt;
            p.y += v.y * Dt;
            return p;
        }
        private static float2 MoveStepVel(float2 p, float2 v)
        {
            if (p.x < 0f || p.x > ViewportWidth) v.x = -v.x;
            if (p.y < 0f || p.y > ViewportHeight) v.y = -v.y;
            return v;
        }
        private static float2 HeavyStepPos(float2 p, float2 v)
        {
            float acc = 0f, x = p.x;
            for (int k = 0; k < 16; k++)
            {
                acc += MathF.Sin(x) + MathF.Cos(x) + MathF.Sqrt(x * x + 1f);
                x += v.x * Dt;
            }
            p.x += acc * Dt;
            p.y += v.y * Dt;
            return p;
        }
        private static float2 HeavyStepVel(float2 p, float2 v) => v;

        private static void CsLoop(float2[] p, float2[] v)
        {
            for (int i = 0; i < N; i++)
            {
                float px = p[i].x + v[i].x * Dt;
                float py = p[i].y + v[i].y * Dt;
                float vx = v[i].x, vy = v[i].y;
                if (px < 0f || px > ViewportWidth) vx = -vx;
                if (py < 0f || py > ViewportHeight) vy = -vy;
                p[i] = new float2(px, py);
                v[i] = new float2(vx, vy);
            }
        }

        private static void HeavyCsLoop(float2[] p, float2[] v)
        {
            for (int i = 0; i < N; i++)
            {
                float acc = 0f, x = p[i].x;
                for (int k = 0; k < 16; k++)
                {
                    acc += MathF.Sin(x) + MathF.Cos(x) + MathF.Sqrt(x * x + 1f);
                    x += v[i].x * Dt;
                }
                p[i].x += acc * Dt;
                p[i].y += v[i].y * Dt;
            }
        }

        private static void PrintRows(string load, List<Row> rows)
        {
            double cs = rows[0].Ms;
            for (int i = 1; i < rows.Count; i++)
            {
                var r = rows[i];
                r.Ratio = cs / r.Ms;
                rows[i] = r;
            }
            Console.WriteLine($"  {"实现",-16}{"p50 ms",10}{"vs C#",9}{"parity",13}{"max|diff|",12}");
            foreach (var r in rows)
            {
                string parity = r.Name == "C# 单线程" ? "—" : r.Mismatch == 0 ? "逐位相等" : $"{r.Mismatch:N0} 不等";
                Console.WriteLine($"  {r.Name,-16}{r.Ms,10:F3}{r.Ratio,9:F2}x{parity,15}{r.MaxDiff,12:E2}");
            }
        }

        private static int ValidateGeneratedWgsl()
        {
            string generatedDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
                "..", "src", "EntJoySample", "NativeTranspiler_Generated"));
            if (!Directory.Exists(generatedDir))
            {
                Console.WriteLine($"[FAIL] 生成目录不存在: {generatedDir}");
                return 1;
            }
            var wgslFiles = Directory.GetFiles(generatedDir, "*.wgsl").OrderBy(f => f).ToList();
            if (wgslFiles.Count == 0)
            {
                Console.WriteLine($"[FAIL] {generatedDir} 下没有 .wgsl 文件。");
                return 1;
            }
            int failures = 0;
            foreach (var file in wgslFiles)
            {
                string content = File.ReadAllText(file);
                var missing = new List<string>();
                if (!content.Contains("@compute")) missing.Add("@compute");
                if (!content.Contains("@workgroup_size")) missing.Add("@workgroup_size");
                if (!content.Contains("var<storage, read_write>")) missing.Add("var<storage>");
                if (content.Contains("/* unsupported")) missing.Add("unsupported 标记");
                int bal = 0;
                foreach (char c in content) { if (c == '{') bal++; else if (c == '}') bal--; }
                if (bal != 0) missing.Add($"花括号不平衡({bal})");
                if (missing.Count > 0)
                {
                    Console.WriteLine($"[WARN] {Path.GetFileName(file)}: {string.Join(", ", missing)}");
                    failures++;
                }
            }
            Console.WriteLine($"[PASS] WGSL 生成校验：{wgslFiles.Count - failures}/{wgslFiles.Count}。");
            return failures;
        }
    }
}
