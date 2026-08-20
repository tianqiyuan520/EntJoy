//using System;
//using System.Diagnostics;
//using System.Runtime.CompilerServices;
//using EntJoy;
//using EntJoy.Mathematics;

//namespace EntJoySample.EntityRandomAccess
//{
//    // ═══════════════════════════════════════════════════════════════════
//    // 稀疏 Entity 随机访问开销量化（07_EntityRandomAccess）
//    // 对照 06-HotFieldHandle 的密集路径，本样例测「逐实体随机访问」：
//    //   ClassArray / StructArray（AoS 基线，乱序）
//    //   GetComponent（EntityManager，lock-free 读）
//    //   ComponentLookup / ComponentLookupUnsafe（缓存列索引+chunk 基址）
//    //   QueryDense（chunk 序，密集参考点）
//    // 用预先打乱的实体索引排列打破位置表/缓存局部性，逼近真实随机访问。
//    // ═══════════════════════════════════════════════════════════════════

//    public struct Position : IComponentData { public float2 Value; }
//    public struct Velocity : IComponentData { public float2 Value; }

//    // ── 基线:纯 OOP class（AoS 散落堆对象）──
//    public class ClassEntity
//    {
//        public float2 Pos;
//        public float2 Vel;

//        [MethodImpl(MethodImplOptions.AggressiveInlining)]
//        public void Update(float dt) => Pos += Vel * dt;
//    }

//    // ── 基线:AoS 值类型数组（连续内存）──
//    public struct StructEntity
//    {
//        public float2 Pos;
//        public float2 Vel;

//        [MethodImpl(MethodImplOptions.AggressiveInlining)]
//        public void Update(float dt) => Pos += Vel * dt;
//    }

//    public class TestEntityRandomAccess : IDisposable
//    {
//        private const int DefaultWarmup = 5;
//        private const int DefaultMeasure = 100;
//        private const float Dt = 1f / 60f;
//        private const int CorrectnessFrames = 60;

//        private static int ReadPositiveEnvironmentInt(string name, int fallback)
//        {
//            return int.TryParse(Environment.GetEnvironmentVariable(name), out int value) && value > 0
//                ? value
//                : fallback;
//        }

//        private static double Percentile(double[] sorted, double percentile)
//        {
//            if (sorted.Length == 0) return 0;
//            double position = (sorted.Length - 1) * percentile;
//            int lower = (int)Math.Floor(position);
//            int upper = (int)Math.Ceiling(position);
//            if (lower == upper) return sorted[lower];
//            double weight = position - lower;
//            return sorted[lower] * (1.0 - weight) + sorted[upper] * weight;
//        }

//        private static double PrintSummary(string variant, double[] samples)
//        {
//            var sorted = (double[])samples.Clone();
//            Array.Sort(sorted);
//            double sum = 0;
//            foreach (double s in samples) sum += s;
//            double avg = sum / samples.Length;
//            double p50 = Percentile(sorted, 0.50);
//            double p95 = Percentile(sorted, 0.95);
//            double p99 = Percentile(sorted, 0.99);
//            double max = sorted[samples.Length - 1];
//            Console.WriteLine($"{variant,-24}: avg={avg:F3} ms, p50={p50:F3} ms, p95={p95:F3} ms, p99={p99:F3} ms, max={max:F3} ms");
//            Console.WriteLine(FormattableString.Invariant(
//                $"BENCH|runtime=EntJoy|case=RA-{variant}|frames={samples.Length}|trace=0|avg={avg:F6}|p50={p50:F6}|p95={p95:F6}|p99={p99:F6}|max={max:F6}"));
//            return avg;
//        }

//        private static unsafe void QueryDenseUpdate(EntityManager em, float dt)
//        {
//            for (int a = 0; a < em.ArchetypeCount; a++)
//            {
//                var arch = em.Archetypes[a];
//                if (arch == null || !arch.Has(typeof(Position))) continue;
//                int idx = arch.GetComponentTypeIndex<Position>();
//                foreach (var chunk in arch.ChunkList)
//                {
//                    var span = new Span<Position>((Position*)chunk.GetComponentArrayPointer(idx), chunk.EntityCount);
//                    for (int i = 0; i < span.Length; i++) span[i].Value += dt;
//                }
//            }
//        }

//        public void Run()
//        {
//            int n = ReadPositiveEnvironmentInt("ENTJOY_RA_ENTITIES", 1_000_000);
//            int warmup = ReadPositiveEnvironmentInt("ENTJOY_BENCH_WARMUP", DefaultWarmup);
//            int measure = ReadPositiveEnvironmentInt("ENTJOY_BENCH_FRAMES", DefaultMeasure);

//            var rnd = new Random(1234);
//            var initPos = new float2[n];
//            var initVel = new float2[n];
//            for (int i = 0; i < n; i++)
//            {
//                initPos[i] = new float2((float)(rnd.NextDouble() * 200 - 100), (float)(rnd.NextDouble() * 200 - 100));
//                initVel[i] = new float2((float)(rnd.NextDouble() * 4 - 2), (float)(rnd.NextDouble() * 4 - 2));
//            }

//            // 打乱的实体索引排列（打破位置表/缓存局部性，逼近真实随机访问）
//            var shuffled = new int[n];
//            for (int i = 0; i < n; i++) shuffled[i] = i;
//            for (int i = n - 1; i > 0; i--)
//            {
//                int j = rnd.Next(i + 1);
//                (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
//            }

//            Console.WriteLine("=== EntJoy 稀疏 Entity 随机访问开销（07_EntityRandomAccess）===");
//            Console.WriteLine($"Entities: {n:N0}, Warmup: {warmup}, Measure: {measure}, Mode: 乱序随机访问");

//            using var world = new World("RA");
//            var em = world.EntityManager;
//            var entities = new Entity[n];
//            for (int i = 0; i < n; i++)
//            {
//                entities[i] = em.NewEntity(typeof(Position), typeof(Velocity));
//                em.Set(entities[i], new Position { Value = initPos[i] });
//                em.Set(entities[i], new Velocity { Value = initVel[i] });
//            }

//            var classArr = new ClassEntity[n];
//            for (int i = 0; i < n; i++) classArr[i] = new ClassEntity { Pos = initPos[i], Vel = initVel[i] };

//            var structArr = new StructEntity[n];
//            for (int i = 0; i < n; i++) structArr[i] = new StructEntity { Pos = initPos[i], Vel = initVel[i] };

//            var lookup = em.GetComponentLookup<Position>();

//            // ── 正确性门:lookup / UnsafeRef / GetComponent 指向同一内存,与 classArr 一致 ──
//            {
//                Console.WriteLine();
//                Console.WriteLine("--- 正确性(lookup == UnsafeRef == GetComponent == classArr)---");
//                bool ok = true;
//                for (int k = 0; k < n; k++)
//                {
//                    int i = shuffled[k];
//                    var e = entities[i];
//                    float2 lv = lookup[e].Value;
//                    float2 uv = lookup.UnsafeRef(e).Value;
//                    float2 gv = em.GetComponent<Position>(e).Value;
//                    float2 cv = classArr[i].Pos;
//                    if (lv.x != uv.x || lv.y != uv.y || lv.x != gv.x || lv.y != gv.y || lv.x != cv.x || lv.y != cv.y)
//                    {
//                        ok = false;
//                        break;
//                    }
//                }
//                Console.WriteLine(ok
//                    ? "  lookup == UnsafeRef == GetComponent == classArr: PASS"
//                    : "  lookup == UnsafeRef == GetComponent == classArr: FAIL");
//            }

//            Console.WriteLine();
//            Console.WriteLine("--- 连续稳态测量(轮转采样,ClassArray/StructArray/GetComponent/ComponentLookup/Unsafe/QueryDense 背靠背)---");

//            double[] classSamples, structSamples, getSamples, lookupSamples, unsafeSamples, denseSamples;
//            {
//                for (int w = 0; w < warmup; w++)
//                {
//                    for (int k = 0; k < n; k++) classArr[shuffled[k]].Pos += Dt;
//                    for (int k = 0; k < n; k++) structArr[shuffled[k]].Pos += Dt;
//                    for (int k = 0; k < n; k++) em.GetComponent<Position>(entities[shuffled[k]]).Value += Dt;
//                    for (int k = 0; k < n; k++) lookup[entities[shuffled[k]]].Value += Dt;
//                    for (int k = 0; k < n; k++) lookup.UnsafeRef(entities[shuffled[k]]).Value += Dt;
//                    QueryDenseUpdate(em, Dt);
//                }
//                classSamples = new double[measure];
//                structSamples = new double[measure];
//                getSamples = new double[measure];
//                lookupSamples = new double[measure];
//                unsafeSamples = new double[measure];
//                denseSamples = new double[measure];
//                for (int m = 0; m < measure; m++)
//                {
//                    long c0 = Stopwatch.GetTimestamp();
//                    for (int k = 0; k < n; k++) classArr[shuffled[k]].Pos += Dt;
//                    long c1 = Stopwatch.GetTimestamp();
//                    long s0 = Stopwatch.GetTimestamp();
//                    for (int k = 0; k < n; k++) structArr[shuffled[k]].Pos += Dt;
//                    long s1 = Stopwatch.GetTimestamp();
//                    long g0 = Stopwatch.GetTimestamp();
//                    for (int k = 0; k < n; k++) em.GetComponent<Position>(entities[shuffled[k]]).Value += Dt;
//                    long g1 = Stopwatch.GetTimestamp();
//                    long l0 = Stopwatch.GetTimestamp();
//                    for (int k = 0; k < n; k++) lookup[entities[shuffled[k]]].Value += Dt;
//                    long l1 = Stopwatch.GetTimestamp();
//                    long u0 = Stopwatch.GetTimestamp();
//                    for (int k = 0; k < n; k++) lookup.UnsafeRef(entities[shuffled[k]]).Value += Dt;
//                    long u1 = Stopwatch.GetTimestamp();
//                    long d0 = Stopwatch.GetTimestamp();
//                    QueryDenseUpdate(em, Dt);
//                    long d1 = Stopwatch.GetTimestamp();
//                    classSamples[m] = (c1 - c0) * 1000.0 / Stopwatch.Frequency;
//                    structSamples[m] = (s1 - s0) * 1000.0 / Stopwatch.Frequency;
//                    getSamples[m] = (g1 - g0) * 1000.0 / Stopwatch.Frequency;
//                    lookupSamples[m] = (l1 - l0) * 1000.0 / Stopwatch.Frequency;
//                    unsafeSamples[m] = (u1 - u0) * 1000.0 / Stopwatch.Frequency;
//                    denseSamples[m] = (d1 - d0) * 1000.0 / Stopwatch.Frequency;
//                }
//            }

//            double classAvg = PrintSummary("ClassArray", classSamples);
//            double structAvg = PrintSummary("StructArray", structSamples);
//            double getAvg = PrintSummary("GetComponent", getSamples);
//            double lookupAvg = PrintSummary("ComponentLookup", lookupSamples);
//            double unsafeAvg = PrintSummary("ComponentLookupUnsafe", unsafeSamples);
//            double denseAvg = PrintSummary("QueryDense", denseSamples);

//            Console.WriteLine();
//            double ratioStruct = structAvg / classAvg;
//            double ratioGet = getAvg / classAvg;
//            double ratioLookup = lookupAvg / classAvg;
//            double ratioUnsafe = unsafeAvg / classAvg;
//            double ratioDense = denseAvg / classAvg;

//            Console.WriteLine($"  [基线] StructArray(AoS 值类型数组)/Class = {ratioStruct:F2}x");
//            Console.WriteLine($"  [现状] GetComponent(lock-free 读)/Class = {ratioGet:F2}x");
//            Console.WriteLine($"  [B] ComponentLookup/Class = {ratioLookup:F2}x");
//            Console.WriteLine($"  [B] ComponentLookupUnsafe/Class = {ratioUnsafe:F2}x");
//            Console.WriteLine($"  [锚点] QueryDense(chunk 序)/Class = {ratioDense:F2}x");
//            Console.WriteLine($"  [结论] 随机访问链每少一层（锁→字典→List→chunk 解析），GetComponent {ratioGet:F2}x → ComponentLookup {ratioLookup:F2}x → Unsafe {ratioUnsafe:F2}x;密集顺序访问(QueryDense)是随机访问的下界参考 {ratioDense:F2}x。");
//        }

//        public void Dispose() { }
//    }
//}
