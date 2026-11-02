using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using EntJoy.Collections;
using EntJoy.JobSystem;

namespace EntJoySample.Envelope
{
    // 同一份内存布局、同一份数据、同一段元素逻辑，只换「执行方式」，用来看
    // 逐元素成本里哪一部分是调度/分发、哪一部分是真实访存。
    //
    // 元素逻辑（所有臂逐字相同，与 MarkDead 同形：4 读 + 1 条件写）：
    //     if (HP[i] <= 0 && Alive[i] > 0) Vel[i] = State[i] + Anim[i];

    public struct MdCsJob : IJobParallelFor
    {
        public NativeArray<float> HP, Alive, State, Anim, Vel;
        public void Execute(int i)
        {
            if (HP[i] <= 0f && Alive[i] > 0f) Vel[i] = State[i] + Anim[i];
        }
    }

    [NativeTranspiler.NativeTranspile]
    public struct MdCppJob : IJobParallelFor
    {
        public NativeArray<float> HP, Alive, State, Anim, Vel;
        public void Execute(int i)
        {
            if (HP[i] <= 0f && Alive[i] > 0f) Vel[i] = State[i] + Anim[i];
        }
    }

    [NativeTranspiler.NativeTranspile(AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
    public struct MdSimdJob : IJobParallelFor
    {
        public NativeArray<float> HP, Alive, State, Anim, Vel;
        public void Execute(int i)
        {
            if (HP[i] <= 0f && Alive[i] > 0f) Vel[i] = State[i] + Anim[i];
        }
    }

    [NativeTranspiler.NativeTranspile]
    public struct MdCppK4Job : IJobParallelFor
    {
        public NativeArray<float> HP, Alive, State, Anim, Vel;
        public void Execute(int i)
        {
            int b = i * 4;
            for (int k = 0; k < 4; k++)
            {
                int j = b + k;
                if (HP[j] <= 0f && Alive[j] > 0f) Vel[j] = State[j] + Anim[j];
            }
        }
    }

    public struct MdEmptyJob : IJobParallelFor
    {
        public void Execute(int i) { }
    }

    public struct MdEmptyIJob : IJob
    {
        public void Execute() { }
    }

    // 下面三臂只用于归因托管侧成本：同一段元素逻辑，换成「每 tile 回调一次」或
    // 「整段一次」，以及把索引器换成裸指针，看 NativeArray 索引器与逐元素回调各占多少。

    public struct MdCsBatchJob : IJobParallelForBatch
    {
        public NativeArray<float> HP, Alive, State, Anim, Vel;
        public void Execute(int start, int count)
        {
            for (int i = start; i < start + count; i++)
            {
                if (HP[i] <= 0f && Alive[i] > 0f) Vel[i] = State[i] + Anim[i];
            }
        }
    }

    public struct MdCsSingleJob : IJob
    {
        public NativeArray<float> HP, Alive, State, Anim, Vel;
        public int N;
        public void Execute()
        {
            for (int i = 0; i < N; i++)
            {
                if (HP[i] <= 0f && Alive[i] > 0f) Vel[i] = State[i] + Anim[i];
            }
        }
    }

    public struct MdCsSinglePtrJob : IJob
    {
        public NativeArray<float> HP, Alive, State, Anim, Vel;
        public int N;
        public unsafe void Execute()
        {
            float* hp = (float*)HP.GetUnsafePtr(), alive = (float*)Alive.GetUnsafePtr();
            float* st = (float*)State.GetUnsafePtr(), an = (float*)Anim.GetUnsafePtr();
            float* vel = (float*)Vel.GetUnsafePtr();
            for (int i = 0; i < N; i++)
            {
                if (hp[i] <= 0f && alive[i] > 0f) vel[i] = st[i] + an[i];
            }
        }
    }

    public static class EnvelopeBench
    {
        private static int _n = 1_000_000;
        private static int _deadTarget = 250_000;
        private static NativeArray<float> _hp, _alive, _state, _anim, _vel;
        private static NativeArray<int> _hist, _histP;
        private static NativeArray<byte> _flush;
        private static bool _flushWrite;
        private static long _sink;

        // 行/列拆分的两种写法：cellsW 为运行时非 2 的幂（idiv）vs 2 的幂（移位）。
        // 除内存访问外两臂逐条相同，差值即一次整数除法的每元素成本。
        private static int CellIdiv(int cellsW)
        {
            int acc = 0;
            for (int i = 0; i < _n; i++)
            {
                int x = i % cellsW;
                int y = i / cellsW;
                acc += y * 3 + x;
            }
            return acc;
        }

        private static int CellShift(int shift)
        {
            int acc = 0;
            int mask = (1 << shift) - 1;
            for (int i = 0; i < _n; i++)
            {
                int x = i & mask;
                int y = i >> shift;
                acc += y * 3 + x;
            }
            return acc;
        }

        public static void Main(string[] args)
        {
            int workers = 8, rounds = 30, flushMb = 48;
            bool cold = false, park = false;
            string? only = null;
            for (int a = 0; a < args.Length; a++)
            {
                switch (args[a])
                {
                    case "--workers": workers = int.Parse(args[++a]); break;
                    case "--n": _n = int.Parse(args[++a]); break;
                    case "--rounds": rounds = int.Parse(args[++a]); break;
                    case "--flush-mb": flushMb = int.Parse(args[++a]); break;
                    case "--cold": cold = true; break;
                    case "--flush-write": _flushWrite = true; break;
                    case "--park": park = true; break;
                    case "--only": only = args[++a]; break;
                }
            }
            _deadTarget = _n / 4;

            Console.WriteLine($"logical_cpu={Environment.ProcessorCount} AVX2={Avx2.IsSupported} AVX512F={Avx512F.IsSupported} vec_width={Vector<float>.Count}");
            Console.WriteLine($"gc_server={System.Runtime.GCSettings.IsServerGC}");

            JobScheduler.Initialize(workers);
            bool assist = Environment.GetEnvironmentVariable("ENTJOY_BENCH_ASSIST") == "1";
            if (assist)
            {
                try { NativeAssistBridge.JobSystem_SetMainThreadAssist(1); }
                catch (Exception ex) { Console.WriteLine($"assist_failed={ex.GetType().Name}"); assist = false; }
            }
            Console.WriteLine($"assist={assist}");
            Console.WriteLine($"backend_native={JobScheduler.IsNative} workers={JobScheduler.WorkerCount} n={_n} rounds={rounds} cold={cold} flushWrite={_flushWrite} park={park}");

            Allocate(flushMb);
            try
            {
                var arms = BuildArms(only);
                var refSum = 0.0;
                bool refInit = false;
                Console.WriteLine();
                Console.WriteLine("arm                  median_ms      min_ms     ns/el   GB/s  checksum");

                foreach (var (name, body) in arms)
                {
                    string line = RunArm(name, body, rounds, cold, park);
                    double sum = CheckSum();
                    if (!refInit) { refSum = sum; refInit = true; }
                    string eq = Math.Abs(sum - refSum) <= Math.Abs(refSum) * 1e-6 + 1e-3 ? "ok" : "DIFF";
                    Console.WriteLine($"{name,-18} {line}  {eq}");
                }
                Console.WriteLine($"\nreference_checksum={refSum:G17}");
            }
            finally
            {
                JobScheduler.Shutdown();
            }
        }

        private static List<(string, Action)> BuildArms(string? only)
        {
            var list = new List<(string, Action)>();
            void Add(string n, Action b) { if (only == null || only == n) list.Add((n, b)); }

            Add("cs-ptr", () => KernelPtr());
            Add("cs-span", () => KernelSpan());
            Add("cs-vec", () => KernelVec());
            Add("job-cs", () =>
            {
                var job = new MdCsJob { HP = _hp, Alive = _alive, State = _state, Anim = _anim, Vel = _vel };
                job.Schedule(_n, 0).Complete();
            });
            Add("job-cpp", () =>
            {
                var job = new MdCppJob { HP = _hp, Alive = _alive, State = _state, Anim = _anim, Vel = _vel };
                job.Schedule(_n, 0).Complete();
            });
            Add("job-cpp-simd", () =>
            {
                var job = new MdSimdJob { HP = _hp, Alive = _alive, State = _state, Anim = _anim, Vel = _vel };
                job.Schedule(_n, 0).Complete();
            });
            Add("job-cpp-k4", () =>
            {
                var job = new MdCppK4Job { HP = _hp, Alive = _alive, State = _state, Anim = _anim, Vel = _vel };
                job.Schedule(_n / 4, 0).Complete();
            });
            Add("job-cpp-b1", () =>
            {
                var job = new MdCppJob { HP = _hp, Alive = _alive, State = _state, Anim = _anim, Vel = _vel };
                job.Schedule(_n, 1).Complete();
            });
            Add("job-empty", () =>
            {
                var job = new MdEmptyJob();
                job.Schedule(_n, 0).Complete();
            });
            Add("wave394", () =>
            {
                // Flow 每步 BFS 层数：394 次「派发 + 完成」，量单步的调度地板。
                for (int w = 0; w < 394; w++)
                {
                    var job = new MdEmptyJob();
                    job.Schedule(_n, 0).Complete();
                }
            });
            Add("wave3940", () =>
            {
                // 同形放大 10 倍，单轮误差按 √10 收敛，用于分辨 <100 ns 的改动。
                for (int w = 0; w < 3940; w++)
                {
                    var job = new MdEmptyJob();
                    job.Schedule(_n, 0).Complete();
                }
            });
            Add("wave394i", () =>
            {
                // 同 394 次，但走 IJob（无 tile 批）路径，分离批机器与基础派发的成本。
                for (int w = 0; w < 394; w++)
                {
                    var job = new MdEmptyIJob();
                    job.Schedule().Complete();
                }
            });            Add("wave394chain", () =>
            {
                // 同 394 层，但层间只用依赖句柄串起来，只在末尾 Complete 一次。
                JobHandle dep = default;
                for (int w = 0; w < 394; w++)
                {
                    var job = new MdEmptyJob();
                    dep = job.Schedule(_n, 0, dep);
                }
                dep.Complete();
            });
            Add("job-cs-batch", () =>
            {
                var job = new MdCsBatchJob { HP = _hp, Alive = _alive, State = _state, Anim = _anim, Vel = _vel };
                job.ScheduleBatch(_n, 512).Complete();
            });
            Add("job-cs1", () =>
            {
                var job = new MdCsSingleJob { HP = _hp, Alive = _alive, State = _state, Anim = _anim, Vel = _vel, N = _n };
                job.Schedule().Complete();
            });
            Add("job-cs1-ptr", () =>
            {
                var job = new MdCsSinglePtrJob { HP = _hp, Alive = _alive, State = _state, Anim = _anim, Vel = _vel, N = _n };
                job.Schedule().Complete();
            });
            // 纯读上限：与 JobSystem 无关，用来判断「冷态 8 核不涨」是硬件墙还是内核形态造成
            Add("cell-idiv", () => CellIdiv(1023));
            Add("cell-shift", () => CellShift(10));
            Add("st-sum", () => SumScalar());
            Add("barrier394", () => Barrier394());
            Add("disem394", () => Disem394());
            Add("st-sum-vec", () => SumVec());
            Add("mt-sum", () => SumParallel(Environment.ProcessorCount / 2));
            Add("hist-store", () => HistStore());
            Add("hist-atomic", () => HistAtomic());
            Add("hist-priv", () => HistPriv8());
            return list;
        }

        private static unsafe void KernelPtr()
        {
            float* hp = (float*)_hp.GetUnsafePtr(), alive = (float*)_alive.GetUnsafePtr();
            float* st = (float*)_state.GetUnsafePtr(), an = (float*)_anim.GetUnsafePtr();
            float* vel = (float*)_vel.GetUnsafePtr();
            int n = _n;
            for (int i = 0; i < n; i++)
            {
                if (hp[i] <= 0f && alive[i] > 0f) vel[i] = st[i] + an[i];
            }
        }

        private static void KernelSpan()
        {
            var hp = _hp.AsSpan();
            var alive = _alive.AsSpan();
            var st = _state.AsSpan();
            var an = _anim.AsSpan();
            var vel = _vel.AsSpan();
            int n = _n;
            for (int i = 0; i < n; i++)
            {
                if (hp[i] <= 0f && alive[i] > 0f) vel[i] = st[i] + an[i];
            }
        }

        private static void KernelVec()
        {
            int w = Vector<float>.Count;
            int n = _n;
            var hp = _hp.AsSpan();
            var alive = _alive.AsSpan();
            var st = _state.AsSpan();
            var an = _anim.AsSpan();
            var vel = _vel.AsSpan();
            int i = 0;
            for (; i + w <= n; i += w)
            {
                var hpv = new Vector<float>(hp.Slice(i, w));
                var alv = new Vector<float>(alive.Slice(i, w));
                Vector<int> mask = Vector.LessThanOrEqual(hpv, Vector<float>.Zero) & Vector.GreaterThan(alv, Vector<float>.Zero);
                if (Vector.EqualsAll<int>(mask, Vector<int>.Zero)) continue;
                var sv = new Vector<float>(st.Slice(i, w));
                var av = new Vector<float>(an.Slice(i, w));
                var vv = new Vector<float>(vel.Slice(i, w));
                Vector.ConditionalSelect(mask, sv + av, vv).CopyTo(vel.Slice(i, w));
            }
            for (; i < n; i++)
            {
                if (hp[i] <= 0f && alive[i] > 0f) vel[i] = st[i] + an[i];
            }
        }

        private static unsafe string RunArm(string name, Action body, int rounds, bool cold, bool park)
        {
            for (int i = 0; i < Math.Max(3, rounds / 4); i++) body();

            var ms = new List<double>(rounds);
            for (int r = 0; r < rounds; r++)
            {
                if (park) { System.Threading.Thread.Sleep(2); }
                if (cold) FlushCache();
                long t0 = Stopwatch.GetTimestamp();
                body();
                long t1 = Stopwatch.GetTimestamp();
                ms.Add((t1 - t0) * 1000.0 / Stopwatch.Frequency);
            }
            ms.Sort();
            double med = ms[ms.Count / 2];
            double min = ms[0];
            double nsEl = med * 1e6 / _n;
            double bytes = 4.0 * 4 * _n + 2.0 * 4 * _deadTarget;
            double gbs = bytes / (med * 1e6);
            return $"{med,11:F3} {min,11:F3} {nsEl,9:F3} {gbs,6:F1}";
        }

        // 纯读上限（与 JobSystem 无关）：4 路流 + 4 个独立累加器，绕开串行 FP 依赖
        private static unsafe void SumScalar()
        {
            float* hp = (float*)_hp.GetUnsafePtr();
            float* alive = (float*)_alive.GetUnsafePtr();
            float* st = (float*)_state.GetUnsafePtr();
            float* an = (float*)_anim.GetUnsafePtr();
            int n = _n;
            float s0 = 0, s1 = 0, s2 = 0, s3 = 0;
            for (int i = 0; i < n; i++) { s0 += hp[i]; s1 += alive[i]; s2 += st[i]; s3 += an[i]; }
            _sink += (long)(s0 + s1 + s2 + s3);
        }

        private static unsafe void SumVec()
        {
            float* hp = (float*)_hp.GetUnsafePtr();
            float* alive = (float*)_alive.GetUnsafePtr();
            float* st = (float*)_state.GetUnsafePtr();
            float* an = (float*)_anim.GetUnsafePtr();
            int n = _n;
            Vector256<float> a0 = Vector256<float>.Zero, a1 = a0, a2 = a0, a3 = a0;
            int i = 0;
            for (; i + Vector256<float>.Count <= n; i += Vector256<float>.Count)
            {
                a0 += Avx.LoadVector256(hp + i);
                a1 += Avx.LoadVector256(alive + i);
                a2 += Avx.LoadVector256(st + i);
                a3 += Avx.LoadVector256(an + i);
            }
            float s = Vector256.Sum(a0 + a1 + a2 + a3);
            for (; i < n; i++) s += hp[i] + alive[i] + st[i] + an[i];
            _sink += (long)s;
        }

        private static unsafe void SumParallel(int parts)
        {
            float* hp = (float*)_hp.GetUnsafePtr();
            float* alive = (float*)_alive.GetUnsafePtr();
            float* st = (float*)_state.GetUnsafePtr();
            float* an = (float*)_anim.GetUnsafePtr();
            int n = _n;
            long total = 0;
            System.Threading.Tasks.Parallel.For(0, parts, p =>
            {
                int b = (int)((long)n * p / parts), e = (int)((long)n * (p + 1) / parts);
                float s0 = 0, s1 = 0, s2 = 0, s3 = 0;
                for (int i = b; i < e; i++) { s0 += hp[i]; s1 += alive[i]; s2 += st[i]; s3 += an[i]; }
                System.Threading.Interlocked.Add(ref total, (long)(s0 + s1 + s2 + s3));
            });
            _sink += total;
        }

        private const int HistCells = 351_232;
        private const int HistParts = 8;

        private static uint HistHash(int i) => (uint)((uint)i * 2654435761u % (uint)HistCells);

        // 非原子累加：只测 RMW 访存代价，计数结果不正确（数据竞争），不参与结果校验
        private static unsafe void HistStore()
        {
            int* h = (int*)_hist.GetUnsafePtr();
            int n = _n;
            System.Threading.Tasks.Parallel.For(0, HistParts, p =>
            {
                int b = (int)((long)n * p / HistParts), e = (int)((long)n * (p + 1) / HistParts);
                for (int i = b; i < e; i++) h[HistHash(i)] += 1;
            });
            _sink += h[0];
        }

        private static unsafe void HistAtomic()
        {
            int* h = (int*)_hist.GetUnsafePtr();
            int n = _n;
            System.Threading.Tasks.Parallel.For(0, HistParts, p =>
            {
                int b = (int)((long)n * p / HistParts), e = (int)((long)n * (p + 1) / HistParts);
                for (int i = b; i < e; i++) System.Threading.Interlocked.Increment(ref h[HistHash(i)]);
            });
            _sink += h[0];
        }

        // 每分区私有直方图 + 并行合并：无原子操作，写入量与原子版相同，代价换成合并期的 8 倍读
        private static unsafe void HistPriv8()
        {
            int* hp = (int*)_histP.GetUnsafePtr();
            int* h = (int*)_hist.GetUnsafePtr();
            int n = _n;
            System.Threading.Tasks.Parallel.For(0, HistParts, p =>
            {
                int* t = hp + (long)p * HistCells;
                int b = (int)((long)n * p / HistParts), e = (int)((long)n * (p + 1) / HistParts);
                for (int i = b; i < e; i++) t[HistHash(i)] += 1;
            });
            System.Threading.Tasks.Parallel.For(0, HistCells, c =>
            {
                int s = 0;
                for (int p = 0; p < HistParts; p++) s += hp[(long)p * HistCells + c];
                h[c] = s;
            });
            _sink += h[0];
        }

        private static unsafe void FlushCache()
        {
            byte* p = (byte*)_flush.GetUnsafePtr();
            int len = _flush.Length;
            if (_flushWrite)
            {
                // 写：留下 48MB 脏行，回写流量会和被测内核抢内存控制器（游戏侧 FlushL3 同此）
                for (int i = 0; i < len; i += 64) p[i] = (byte)i;
                return;
            }
            // 读：只填充 L3 再丢弃，不产生脏行回写
            long acc = 0;
            for (int i = 0; i < len; i += 64) acc += p[i];
            _sink ^= acc;
        }

        private static double CheckSum()
        {
            var vel = _vel.AsSpan();
            double s = 0;
            for (int i = 0; i < vel.Length; i++) s += vel[i];
            return s;
        }

        private static void Allocate(int flushMb)
        {
            _hp = new NativeArray<float>(_n, Allocator.Persistent);
            _alive = new NativeArray<float>(_n, Allocator.Persistent);
            _state = new NativeArray<float>(_n, Allocator.Persistent);
            _anim = new NativeArray<float>(_n, Allocator.Persistent);
            _vel = new NativeArray<float>(_n, Allocator.Persistent);
            _flush = new NativeArray<byte>(flushMb * 1024 * 1024, Allocator.Persistent);
            _hist = new NativeArray<int>(HistCells, Allocator.Persistent);
            _histP = new NativeArray<int>(HistCells * HistParts, Allocator.Persistent);

            var rng = new Random(12345);
            int dead = 0;
            for (int i = 0; i < _n; i++)
            {
                bool isDead = dead < _deadTarget && rng.Next(4) == 0;
                if (isDead) dead++;
                _hp[i] = isDead ? -1f : 100f;
                _alive[i] = 1f;
                _state[i] = rng.Next(8);
                _anim[i] = rng.Next(16);
                _vel[i] = 0x5A5A;
            }
        }

        // 8 线程 394 轮 sense-reversing 屏障：给出「常驻 worker + 内核内屏障」方案每次同步的价码。
        private static double Barrier394()
        {
            const int parts = 8;
            const int rounds = 394;
            int arrive = 0;
            int sense = 0;
            long sink = 0;
            var threads = new Thread[parts];
            for (int t = 0; t < parts; t++)
            {
                int tid = t;
                threads[t] = new Thread(() =>
                {
                    int s = 0;
                    var spin = new SpinWait();
                    for (int r = 0; r < rounds; r++)
                    {
                        if (Interlocked.Increment(ref arrive) == parts)
                        {
                            Interlocked.Exchange(ref arrive, 0);
                            Volatile.Write(ref sense, 1 - s);
                        }
                        else
                        {
                            while (Volatile.Read(ref sense) == s) System.Threading.Thread.SpinWait(50);
                        }
                        s = 1 - s;
                        sink += tid;
                    }
                });
                threads[t].IsBackground = true;
                threads[t].Start();
            }
            var clock = Stopwatch.StartNew();
            for (int i = 0; i < parts; i++) threads[i].Join();
            clock.Stop();
            GC.KeepAlive(sink);
            return clock.Elapsed.TotalMilliseconds;
        }

        // 8 线程 394 轮 dissemination 屏障（3 跳）：无中心计数、每线程只写自己的槽。
        private static double Disem394()
        {
            const int parts = 8;
            const int rounds = 394;
            const int hops = 3;
            const int stride = 16;   // 每槽独占 64B，避免相邻槽共享缓存行
            var flags = new int[parts * hops * stride];
            long sink = 0;
            var threads = new Thread[parts];
            for (int t = 0; t < parts; t++)
            {
                int tid = t;
                threads[t] = new Thread(() =>
                {
                    for (int it = 1; it <= rounds; it++)
                    {
                        for (int r = 0; r < hops; r++)
                        {
                            int peer = (tid - (1 << r) + parts) % parts;
                            Volatile.Write(ref flags[(tid * hops + r) * stride], it);
                            while (Volatile.Read(ref flags[(peer * hops + r) * stride]) < it) Thread.SpinWait(50);
                        }
                        sink += tid;
                    }
                });
                threads[t].IsBackground = true;
                threads[t].Start();
            }
            var clock = Stopwatch.StartNew();
            for (int i = 0; i < parts; i++) threads[i].Join();
            clock.Stop();
            GC.KeepAlive(sink);
            return clock.Elapsed.TotalMilliseconds;
        }
    }
}

namespace EntJoySample.Envelope
{
    internal static class NativeAssistBridge
    {
        [System.Runtime.InteropServices.DllImport("NativeDll", EntryPoint = "JobSystem_SetMainThreadAssist")]
        internal static extern void JobSystem_SetMainThreadAssist(int enabled);
    }
}
