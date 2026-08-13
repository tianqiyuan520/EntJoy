using System;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using EntJoy;
using EntJoy.Mathematics;

namespace EntJoySample.HotFieldHandle
{
    // ═══════════════════════════════════════════════════════════════════
    // HotField 核心（重新设计,基于原始目标）:
    //   静态 pin 平铺字段级 SoA(指针稳定,无边界检查) + class 门面(int 索引)
    //   + free-list 生命周期 + System(IJobParallelFor)
    //   对应原始 HotField「指针指向静态 HotStore 数组,开销低」
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>标记一个普通 class 为 HotField 支持。</summary>
    [AttributeUsage(AttributeTargets.Class)]
    public sealed class HotFieldEntityAttribute : Attribute { }

    /// <summary>基线:纯 OOP class(数据跟随对象,AoS 散落堆对象)。</summary>
    public class ClassEntity
    {
        public float2 Pos;
        public float2 Vel;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Update(float dt) => Pos += Vel * dt;
    }

    /// <summary>静态 pin 平铺 SoA(pin 后指针稳定)+ free-list(稳定索引)+ 版本号(防悬垂)。
    /// 多 World:按类型静态(单 World 优先);多 World 可改为 World 作用域静态。</summary>
    public static unsafe class HotFieldStatic
    {
        public static float2* Positions;    // 平铺字段级 SoA(pin 后稳定,裸指针无边界检查)
        public static float2* Velocities;
        public static int[] Versions;       // 版本号:每次分配递增,防悬垂
        public static int[] NextFree;       // free-list:add/remove 不搬移他人索引
        public static int FreeHead = -1;
        public static int Count;

        public static void Init(float2[] pos, float2[] vel, int capacity)
        {
            Positions = (float2*)GCHandle.Alloc(pos, GCHandleType.Pinned).AddrOfPinnedObject();
            Velocities = (float2*)GCHandle.Alloc(vel, GCHandleType.Pinned).AddrOfPinnedObject();
            Versions = new int[capacity];
            NextFree = new int[capacity];
            for (int i = 0; i < capacity; i++) NextFree[i] = -1;
        }

        public static int Create(float2 pos, float2 vel)
        {
            int idx;
            if (FreeHead >= 0) { idx = FreeHead; FreeHead = NextFree[idx]; }
            else { idx = Count++; }
            Positions[idx] = pos;
            Velocities[idx] = vel;
            Versions[idx]++;
            return idx;
        }

        public static void Destroy(int idx)
        {
            NextFree[idx] = FreeHead;
            FreeHead = idx;
        }

        public static bool IsValid(int idx, int version) => idx >= 0 && idx < Count && Versions[idx] == version;
    }

    /// <summary>HotField 实体:普通 class 门面(int 索引 + ref 属性 → 静态平铺数组,裸指针无边界检查)。
    /// 数据在平铺 SoA(非 chunk,非对象)。Update 与 plain class 逐字节相同。</summary>
    [HotFieldEntity]
    public unsafe class Player
    {
        public int Index;   // 平铺数组下标(稳定 free-list 索引)

        public ref float2 Pos { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => ref HotFieldStatic.Positions[Index]; }
        public ref float2 Vel { [MethodImpl(MethodImplOptions.AggressiveInlining)] get => ref HotFieldStatic.Velocities[Index]; }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Update(float dt) => Pos += Vel * dt;
    }

    public class TestHotFieldCore
    {
        private const float Dt = 1f / 60f;

        private static int ReadEnv(string name, int fallback)
            => int.TryParse(Environment.GetEnvironmentVariable(name), out int v) && v > 0 ? v : fallback;

        private static double Print(string v, double[] samples)
        {
            Array.Sort(samples);
            double sum = 0; foreach (double s in samples) sum += s;
            double avg = sum / samples.Length;
            Console.WriteLine($"{v,-20}: avg={avg:F3} ms p50={samples[samples.Length / 2]:F3}");
            Console.WriteLine(FormattableString.Invariant($"BENCH|runtime=EntJoy|case=HC-{v}|frames={samples.Length}|avg={avg:F6}|p50={samples[samples.Length / 2]:F6}"));
            return avg;
        }

        public unsafe void Run()
        {
            int n = ReadEnv("ENTJOY_HF_ENTITIES", 1_000_000);
            int warmup = ReadEnv("ENTJOY_BENCH_WARMUP", 5);
            int measure = ReadEnv("ENTJOY_BENCH_FRAMES", 30);

            var rnd = new Random(1234);
            var initPos = new float2[n];
            var initVel = new float2[n];
            for (int i = 0; i < n; i++)
            {
                initPos[i] = new float2((float)(rnd.NextDouble() * 200 - 100), (float)(rnd.NextDouble() * 200 - 100));
                initVel[i] = new float2((float)(rnd.NextDouble() * 4 - 2), (float)(rnd.NextDouble() * 4 - 2));
            }

            Console.WriteLine($"=== HotField 核心:静态 pin 平铺 SoA + int 索引 + free-list + System | {n:N0} ===");

            // 基线:原始 class
            var classArr = new ClassEntity[n];
            for (int i = 0; i < n; i++) classArr[i] = new ClassEntity { Pos = initPos[i], Vel = initVel[i] };

            // HotField:静态 pin 平铺 SoA + class 门面(int 索引)
            var posArr = (float2[])initPos.Clone();
            var velArr = (float2[])initVel.Clone();
            HotFieldStatic.Init(posArr, velArr, n + 16);
            var players = new Player[n];
            for (int i = 0; i < n; i++) players[i] = new Player { Index = HotFieldStatic.Create(initPos[i], initVel[i]) };

            // 生命周期正确性:free-list 复用 + 版本号防悬垂
            {
                int a = HotFieldStatic.Create(new float2(1, 2), new float2(0, 0));
                int aVer = HotFieldStatic.Versions[a];
                HotFieldStatic.Destroy(a);
                int b = HotFieldStatic.Create(new float2(3, 4), new float2(0, 0));
                bool reused = b == a && HotFieldStatic.Versions[b] == aVer + 1;
                Console.WriteLine($"  [生命周期] Destroy 后 Create 复用同一索引 {b}=={a} 且版本 {aVer}→{HotFieldStatic.Versions[b]}: {(reused ? "PASS" : "FAIL")}");
            }

            // 乱序
            var shuffled = new int[n];
            for (int i = 0; i < n; i++) shuffled[i] = i;
            for (int i = n - 1; i > 0; i--)
            {
                int j = rnd.Next(i + 1);
                (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
            }

            double[] cls, hC, cR, hR, sys;
            {
                for (int w = 0; w < warmup; w++)
                {
                    foreach (var e in classArr) e.Update(Dt);
                    foreach (var p in players) p.Update(Dt);
                    for (int k = 0; k < n; k++) classArr[shuffled[k]].Pos += Dt;
                    for (int k = 0; k < n; k++) players[shuffled[k]].Update(Dt);
                    Parallel.For(0, n, k => HotFieldStatic.Positions[k] += HotFieldStatic.Velocities[k] * Dt);   // System:并行扫平铺数组
                }
                cls = new double[measure]; hC = new double[measure]; cR = new double[measure]; hR = new double[measure]; sys = new double[measure];
                for (int m = 0; m < measure; m++)
                {
                    long c0 = Stopwatch.GetTimestamp();
                    foreach (var e in classArr) e.Update(Dt);
                    long c1 = Stopwatch.GetTimestamp();
                    long h0 = Stopwatch.GetTimestamp();
                    foreach (var p in players) p.Update(Dt);
                    long h1 = Stopwatch.GetTimestamp();
                    long cr0 = Stopwatch.GetTimestamp();
                    for (int k = 0; k < n; k++) classArr[shuffled[k]].Pos += Dt;
                    long cr1 = Stopwatch.GetTimestamp();
                    long hr0 = Stopwatch.GetTimestamp();
                    for (int k = 0; k < n; k++) players[shuffled[k]].Update(Dt);
                    long hr1 = Stopwatch.GetTimestamp();
                    long s0 = Stopwatch.GetTimestamp();
                    Parallel.For(0, n, k => HotFieldStatic.Positions[k] += HotFieldStatic.Velocities[k] * Dt);
                    long s1 = Stopwatch.GetTimestamp();
                    cls[m] = (c1 - c0) * 1000.0 / Stopwatch.Frequency;
                    hC[m] = (h1 - h0) * 1000.0 / Stopwatch.Frequency;
                    cR[m] = (cr1 - cr0) * 1000.0 / Stopwatch.Frequency;
                    hR[m] = (hr1 - hr0) * 1000.0 / Stopwatch.Frequency;
                    sys[m] = (s1 - s0) * 1000.0 / Stopwatch.Frequency;
                }
            }

            double cAvg = Print("Class(连续)", cls);
            double hCAvg = Print("HotField(连续)", hC);
            double cRAvg = Print("Class(随机)", cR);
            double hRAvg = Print("HotField(随机)", hR);
            double sAvg = Print("System(并行平铺)", sys);

            Console.WriteLine();
            Console.WriteLine($"  [连续] HotField(int索引+静态平铺SoA)/Class={hCAvg / cAvg:F2}x");
            Console.WriteLine($"  [随机] HotField(int索引+静态平铺SoA)/Class={hRAvg / cRAvg:F2}x");
            Console.WriteLine($"  [System] 并行扫平铺数组/Class={cAvg / sAvg:F2}x");
            Console.WriteLine($"  [结论] HotField 核心:连续 {hCAvg / cAvg:F2}x / 随机 {hRAvg / cRAvg:F2}x / System {cAvg / sAvg:F2}x;free-list+版本号托管生命周期;数据在静态 pin 平铺 SoA(非 chunk),class 门面无感。");
        }
    }
}
