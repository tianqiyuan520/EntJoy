using EntJoy;
using EntJoy.Collections;
using EntJoy.Mathematics;
using System.Diagnostics;
using System.Threading;

namespace EntJoySample.IJobChunkMoveCompareTest
{
    public struct MovePosition : IComponentData
    {
        public float2 Value;
    }

    public struct MoveVelocity : IComponentData
    {
        public float2 Value;
    }

    public struct MoveJobChunkCSharp : IJobChunk
    {
        public float DeltaTime;

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining | System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
        public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
        {
            Span<MovePosition> positions = chunk.GetComponentDataSpan<MovePosition>();
            Span<MoveVelocity> velocities = chunk.GetComponentDataSpan<MoveVelocity>();

            for (int index = 0; index < positions.Length; index++)
            {
                MovePosition position = positions[index];
                position.Value += velocities[index].Value * DeltaTime;
                positions[index] = position;
            }
        }
    }

    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Cpp)]
    public struct MoveJobChunkCpp : IJobChunk
    {
        public float DeltaTime;

        public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
        {
            NativeArray<MovePosition> positions = chunk.GetComponentDataNativeArray<MovePosition>();
            NativeArray<MoveVelocity> velocities = chunk.GetComponentDataNativeArray<MoveVelocity>();

            for (int index = 0; index < positions.Length; index++)
            {
                MovePosition position = positions[index];
                position.Value += velocities[index].Value * DeltaTime;
                positions[index] = position;
            }
        }
    }

    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Cpp, CppMathLib = NativeTranspiler.CppMathLib.fast)]
    public struct MoveJobChunkCppFast : IJobChunk
    {
        public float DeltaTime;

        public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
        {
            NativeArray<MovePosition> positions = chunk.GetComponentDataNativeArray<MovePosition>();
            NativeArray<MoveVelocity> velocities = chunk.GetComponentDataNativeArray<MoveVelocity>();

            for (int index = 0; index < positions.Length; index++)
            {
                MovePosition position = positions[index];
                position.Value += velocities[index].Value * DeltaTime;
                positions[index] = position;
            }
        }
    }

    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc)]
    public struct MoveJobChunkIspc : IJobChunk
    {
        public float DeltaTime;

        public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
        {
            NativeArray<MovePosition> positions = chunk.GetComponentDataNativeArray<MovePosition>();
            NativeArray<MoveVelocity> velocities = chunk.GetComponentDataNativeArray<MoveVelocity>();

            for (int index = 0; index < positions.Length; index++)
            {
                MovePosition position = positions[index];
                position.Value += velocities[index].Value * DeltaTime;
                positions[index] = position;
            }
        }
    }

    public struct MoveJobEntityCSharp : IJobEntity
    {
        public float DeltaTime;

        public void Execute(ref MovePosition position, in MoveVelocity velocity)
        {
            position.Value += velocity.Value * DeltaTime;
        }
    }

    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Cpp)]
    public struct MoveJobEntityCpp : IJobEntity
    {
        public float DeltaTime;

        public void Execute(ref MovePosition position, in MoveVelocity velocity)
        {
            position.Value += velocity.Value * DeltaTime;
        }
    }

    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc, MathLib = NativeTranspiler.IspcMathLib.fast)]
    public struct MoveJobEntityIspc : IJobEntity
    {
        public float DeltaTime;

        public void Execute(ref MovePosition position, in MoveVelocity velocity)
        {
            position.Value += velocity.Value * DeltaTime;
        }
    }

    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc, MathLib = NativeTranspiler.IspcMathLib.fast, UseISPC_MT = true)]
    public struct MoveJobEntityIspcMt : IJobEntity
    {
        public float DeltaTime;

        public void Execute(ref MovePosition position, in MoveVelocity velocity)
        {
            position.Value += velocity.Value * DeltaTime;
        }
    }

    public struct HeavyJobChunkCSharp : IJobChunk
    {
        public float DeltaTime;

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining | System.Runtime.CompilerServices.MethodImplOptions.AggressiveOptimization)]
        public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
        {
            Span<MovePosition> positions = chunk.GetComponentDataSpan<MovePosition>();
            Span<MoveVelocity> velocities = chunk.GetComponentDataSpan<MoveVelocity>();

            for (int index = 0; index < positions.Length; index++)
            {
                MovePosition position = positions[index];
                MoveVelocity velocity = velocities[index];

                float px = position.Value.x;
                float py = position.Value.y;
                float vx = velocity.Value.x;
                float vy = velocity.Value.y;
                float accX = px * 0.001f + vx * 0.01f;
                float accY = py * 0.001f + vy * 0.01f;

                for (int iteration = 0; iteration < 16; iteration++)
                {
                    float phaseX = accX + iteration * 0.03125f;
                    float phaseY = accY - iteration * 0.0625f;
                    float wave = MathF.Sin(phaseX) + MathF.Cos(phaseY);
                    float radius = MathF.Sqrt(accX * accX + accY * accY + 1.0f);
                    accX = accX * 0.985f + wave * 0.015f + radius * 0.0002f + vx * 0.0001f;
                    accY = accY * 0.982f - wave * 0.012f + radius * 0.0003f + vy * 0.0001f;
                }

                position.Value.x = px + vx * DeltaTime + accX * 0.001f;
                position.Value.y = py + vy * DeltaTime + accY * 0.001f;
                positions[index] = position;
            }
        }
    }

    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Cpp, CppMathLib = NativeTranspiler.CppMathLib.@default)]
    public struct HeavyJobChunkCpp : IJobChunk
    {
        public float DeltaTime;

        public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
        {
            NativeArray<MovePosition> positions = chunk.GetComponentDataNativeArray<MovePosition>();
            NativeArray<MoveVelocity> velocities = chunk.GetComponentDataNativeArray<MoveVelocity>();

            for (int index = 0; index < positions.Length; index++)
            {
                MovePosition position = positions[index];
                MoveVelocity velocity = velocities[index];

                float px = position.Value.x;
                float py = position.Value.y;
                float vx = velocity.Value.x;
                float vy = velocity.Value.y;
                float accX = px * 0.001f + vx * 0.01f;
                float accY = py * 0.001f + vy * 0.01f;

                for (int iteration = 0; iteration < 16; iteration++)
                {
                    float phaseX = accX + iteration * 0.03125f;
                    float phaseY = accY - iteration * 0.0625f;
                    float wave = MathF.Sin(phaseX) + MathF.Cos(phaseY);
                    float radius = MathF.Sqrt(accX * accX + accY * accY + 1.0f);
                    accX = accX * 0.985f + wave * 0.015f + radius * 0.0002f + vx * 0.0001f;
                    accY = accY * 0.982f - wave * 0.012f + radius * 0.0003f + vy * 0.0001f;
                }

                position.Value.x = px + vx * DeltaTime + accX * 0.001f;
                position.Value.y = py + vy * DeltaTime + accY * 0.001f;
                positions[index] = position;
            }
        }
    }

    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Cpp, CppMathLib = NativeTranspiler.CppMathLib.fast)]
    public struct HeavyJobChunkCppFast : IJobChunk
    {
        public float DeltaTime;

        public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
        {
            NativeArray<MovePosition> positions = chunk.GetComponentDataNativeArray<MovePosition>();
            NativeArray<MoveVelocity> velocities = chunk.GetComponentDataNativeArray<MoveVelocity>();

            for (int index = 0; index < positions.Length; index++)
            {
                MovePosition position = positions[index];
                MoveVelocity velocity = velocities[index];

                float px = position.Value.x;
                float py = position.Value.y;
                float vx = velocity.Value.x;
                float vy = velocity.Value.y;
                float accX = px * 0.001f + vx * 0.01f;
                float accY = py * 0.001f + vy * 0.01f;

                for (int iteration = 0; iteration < 16; iteration++)
                {
                    float phaseX = accX + iteration * 0.03125f;
                    float phaseY = accY - iteration * 0.0625f;
                    float wave = MathF.Sin(phaseX) + MathF.Cos(phaseY);
                    float radius = MathF.Sqrt(accX * accX + accY * accY + 1.0f);
                    accX = accX * 0.985f + wave * 0.015f + radius * 0.0002f + vx * 0.0001f;
                    accY = accY * 0.982f - wave * 0.012f + radius * 0.0003f + vy * 0.0001f;
                }

                position.Value.x = px + vx * DeltaTime + accX * 0.001f;
                position.Value.y = py + vy * DeltaTime + accY * 0.001f;
                positions[index] = position;
            }
        }
    }

    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc, MathLib = NativeTranspiler.IspcMathLib.fast)]
    public struct HeavyJobChunkIspc : IJobChunk
    {
        public float DeltaTime;

        public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
        {
            NativeArray<MovePosition> positions = chunk.GetComponentDataNativeArray<MovePosition>();
            NativeArray<MoveVelocity> velocities = chunk.GetComponentDataNativeArray<MoveVelocity>();

            for (int index = 0; index < positions.Length; index++)
            {
                MovePosition position = positions[index];
                MoveVelocity velocity = velocities[index];

                float px = position.Value.x;
                float py = position.Value.y;
                float vx = velocity.Value.x;
                float vy = velocity.Value.y;
                float accX = px * 0.001f + vx * 0.01f;
                float accY = py * 0.001f + vy * 0.01f;

                for (int iteration = 0; iteration < 16; iteration++)
                {
                    float phaseX = accX + iteration * 0.03125f;
                    float phaseY = accY - iteration * 0.0625f;
                    float wave = MathF.Sin(phaseX) + MathF.Cos(phaseY);
                    float radius = MathF.Sqrt(accX * accX + accY * accY + 1.0f);
                    accX = accX * 0.985f + wave * 0.015f + radius * 0.0002f + vx * 0.0001f;
                    accY = accY * 0.982f - wave * 0.012f + radius * 0.0003f + vy * 0.0001f;
                }

                position.Value.x = px + vx * DeltaTime + accX * 0.001f;
                position.Value.y = py + vy * DeltaTime + accY * 0.001f;
                positions[index] = position;
            }
        }
    }

    public struct HeavyJobEntityCSharp : IJobEntity
    {
        public float DeltaTime;

        public void Execute(ref MovePosition position, in MoveVelocity velocity)
        {
            float px = position.Value.x;
            float py = position.Value.y;
            float vx = velocity.Value.x;
            float vy = velocity.Value.y;
            float accX = px * 0.001f + vx * 0.01f;
            float accY = py * 0.001f + vy * 0.01f;

            for (int iteration = 0; iteration < 16; iteration++)
            {
                float phaseX = accX + iteration * 0.03125f;
                float phaseY = accY - iteration * 0.0625f;
                float wave = MathF.Sin(phaseX) + MathF.Cos(phaseY);
                float radius = MathF.Sqrt(accX * accX + accY * accY + 1.0f);
                accX = accX * 0.985f + wave * 0.015f + radius * 0.0002f + vx * 0.0001f;
                accY = accY * 0.982f - wave * 0.012f + radius * 0.0003f + vy * 0.0001f;
            }

            position.Value.x = px + vx * DeltaTime + accX * 0.001f;
            position.Value.y = py + vy * DeltaTime + accY * 0.001f;
        }
    }

    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Cpp, CppMathLib = NativeTranspiler.CppMathLib.@default)]
    public struct HeavyJobEntityCpp : IJobEntity
    {
        public float DeltaTime;

        public void Execute(ref MovePosition position, in MoveVelocity velocity)
        {
            float px = position.Value.x;
            float py = position.Value.y;
            float vx = velocity.Value.x;
            float vy = velocity.Value.y;
            float accX = px * 0.001f + vx * 0.01f;
            float accY = py * 0.001f + vy * 0.01f;

            for (int iteration = 0; iteration < 16; iteration++)
            {
                float phaseX = accX + iteration * 0.03125f;
                float phaseY = accY - iteration * 0.0625f;
                float wave = MathF.Sin(phaseX) + MathF.Cos(phaseY);
                float radius = MathF.Sqrt(accX * accX + accY * accY + 1.0f);
                accX = accX * 0.985f + wave * 0.015f + radius * 0.0002f + vx * 0.0001f;
                accY = accY * 0.982f - wave * 0.012f + radius * 0.0003f + vy * 0.0001f;
            }

            position.Value.x = px + vx * DeltaTime + accX * 0.001f;
            position.Value.y = py + vy * DeltaTime + accY * 0.001f;
        }
    }

    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc, MathLib = NativeTranspiler.IspcMathLib.fast)]
    public struct HeavyJobEntityIspc : IJobEntity
    {
        public float DeltaTime;

        public void Execute(ref MovePosition position, in MoveVelocity velocity)
        {
            float px = position.Value.x;
            float py = position.Value.y;
            float vx = velocity.Value.x;
            float vy = velocity.Value.y;
            float accX = px * 0.001f + vx * 0.01f;
            float accY = py * 0.001f + vy * 0.01f;

            for (int iteration = 0; iteration < 16; iteration++)
            {
                float phaseX = accX + iteration * 0.03125f;
                float phaseY = accY - iteration * 0.0625f;
                float wave = MathF.Sin(phaseX) + MathF.Cos(phaseY);
                float radius = MathF.Sqrt(accX * accX + accY * accY + 1.0f);
                accX = accX * 0.985f + wave * 0.015f + radius * 0.0002f + vx * 0.0001f;
                accY = accY * 0.982f - wave * 0.012f + radius * 0.0003f + vy * 0.0001f;
            }

            position.Value.x = px + vx * DeltaTime + accX * 0.001f;
            position.Value.y = py + vy * DeltaTime + accY * 0.001f;
        }
    }

    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc, MathLib = NativeTranspiler.IspcMathLib.fast, UseISPC_MT = true)]
    public struct HeavyJobEntityIspcMt : IJobEntity
    {
        public float DeltaTime;

        public void Execute(ref MovePosition position, in MoveVelocity velocity)
        {
            float px = position.Value.x;
            float py = position.Value.y;
            float vx = velocity.Value.x;
            float vy = velocity.Value.y;
            float accX = px * 0.001f + vx * 0.01f;
            float accY = py * 0.001f + vy * 0.01f;

            for (int iteration = 0; iteration < 16; iteration++)
            {
                float phaseX = accX + iteration * 0.03125f;
                float phaseY = accY - iteration * 0.0625f;
                float wave = MathF.Sin(phaseX) + MathF.Cos(phaseY);
                float radius = MathF.Sqrt(accX * accX + accY * accY + 1.0f);
                accX = accX * 0.985f + wave * 0.015f + radius * 0.0002f + vx * 0.0001f;
                accY = accY * 0.982f - wave * 0.012f + radius * 0.0003f + vy * 0.0001f;
            }

            position.Value.x = px + vx * DeltaTime + accX * 0.001f;
            position.Value.y = py + vy * DeltaTime + accY * 0.001f;
        }
    }

    public sealed unsafe class IJobChunkMoveCompareSample : IDisposable
    {
        private const int EntityCount = 1_000_000;
        private static readonly int WarmupFrames = ReadPositiveEnvironmentInt("ENTJOY_BENCH_WARMUP", 5);
        private static readonly int MeasureFrames = ReadPositiveEnvironmentInt("ENTJOY_BENCH_FRAMES", 100);
        private static readonly int SleepWarmupFrames = ReadPositiveEnvironmentInt("ENTJOY_BENCH_WARMUP", 5);
        private static readonly int SleepMeasureFrames = ReadPositiveEnvironmentInt("ENTJOY_BENCH_FRAMES", 100);
        private static readonly int BenchmarkRounds = ReadPositiveEnvironmentInt("ENTJOY_BENCH_ROUNDS", 5);
        private const int FrameSleepMilliseconds = 16;
        private const int HeavyIterations = 32;
        private const float DeltaTime = 1.0f / 60.0f;
        private const float Epsilon = 0.001f;
        private const float HeavyEpsilon = 0.001f;

        private readonly QueryBuilder _query = new QueryBuilder().WithAll<MovePosition, MoveVelocity>();
        private readonly World _csharpWorld;
        private readonly World _cppWorld;
        private readonly World _cppFastWorld;
        private readonly World _ispcWorld;
        private readonly World _entityCsharpWorld;
        private readonly World _entityCppWorld;
        private readonly World _entityIspcWorld;
        private readonly World _entityIspcMtWorld;
        private readonly World _sleepChunkCsharpWorld;
        private readonly World _sleepChunkCppWorld;
        private readonly World _sleepChunkIspcWorld;
        private readonly World _sleepEntityCsharpWorld;
        private readonly World _sleepEntityCppWorld;
        private readonly World _sleepEntityIspcWorld;

        private static int ReadPositiveEnvironmentInt(string name, int fallback)
        {
            return int.TryParse(Environment.GetEnvironmentVariable(name), out int value) && value > 0
                ? value
                : fallback;
        }

        public IJobChunkMoveCompareSample()
        {
            Console.WriteLine($"Preparing {EntityCount:N0} entities...");

            _csharpWorld = new World("IJobChunkMoveCompare_CSharp");
            CreateEntities(_csharpWorld);

            _cppWorld = new World("IJobChunkMoveCompare_Cpp");
            CreateEntities(_cppWorld);

            _cppFastWorld = new World("IJobChunkMoveCompare_CppFast");
            CreateEntities(_cppFastWorld);

            _ispcWorld = new World("IJobChunkMoveCompare_Ispc");
            CreateEntities(_ispcWorld);

            _entityCsharpWorld = new World("IJobEntityMoveCompare_CSharp");
            CreateEntities(_entityCsharpWorld);

            _entityCppWorld = new World("IJobEntityMoveCompare_Cpp");
            CreateEntities(_entityCppWorld);

            _entityIspcWorld = new World("IJobEntityMoveCompare_Ispc");
            CreateEntities(_entityIspcWorld);

            _entityIspcMtWorld = new World("IJobEntityMoveCompare_IspcMt");
            CreateEntities(_entityIspcMtWorld);

            _sleepChunkCsharpWorld = new World("IJobChunkMoveCompare_Sleep_CSharp");
            CreateEntities(_sleepChunkCsharpWorld);

            _sleepChunkCppWorld = new World("IJobChunkMoveCompare_Sleep_Cpp");
            CreateEntities(_sleepChunkCppWorld);

            _sleepChunkIspcWorld = new World("IJobChunkMoveCompare_Sleep_Ispc");
            CreateEntities(_sleepChunkIspcWorld);

            _sleepEntityCsharpWorld = new World("IJobEntityMoveCompare_Sleep_CSharp");
            CreateEntities(_sleepEntityCsharpWorld);

            _sleepEntityCppWorld = new World("IJobEntityMoveCompare_Sleep_Cpp");
            CreateEntities(_sleepEntityCppWorld);

            _sleepEntityIspcWorld = new World("IJobEntityMoveCompare_Sleep_Ispc");
            CreateEntities(_sleepEntityIspcWorld);
        }

        public void Run()
        {
            NativeJobScheduler.PrewakeWorkersOnce();

            Console.WriteLine();
            Console.WriteLine("=== IJobChunk 100w 轻量移动对比 ===");
            Console.WriteLine($"实体数: {EntityCount:N0}, Warmup: {WarmupFrames}, Measure: {MeasureFrames}, dt={DeltaTime:F6}");
            Console.WriteLine($"轮次: {BenchmarkRounds}，汇总时忽略首轮");
            Console.WriteLine();

            var lightCases = new (string Label, Action Run)[]
            {
                ("C# IJobChunk", () => { World.DefaultWorld = _csharpWorld; new MoveJobChunkCSharp { DeltaTime = DeltaTime }.Schedule(_query).Complete(); }),
                ("C++ IJobChunk", () => { World.DefaultWorld = _cppWorld; new MoveJobChunkCpp { DeltaTime = DeltaTime }.Schedule(_query).Complete(); }),
                ("C++ Fast IJobChunk", () => { World.DefaultWorld = _cppFastWorld; new MoveJobChunkCppFast { DeltaTime = DeltaTime }.Schedule(_query).Complete(); }),
                ("ISPC IJobChunk", () => { World.DefaultWorld = _ispcWorld; new MoveJobChunkIspc { DeltaTime = DeltaTime }.Schedule(_query).Complete(); }),
                ("C# IJobEntity", () => { World.DefaultWorld = _entityCsharpWorld; new MoveJobEntityCSharp { DeltaTime = DeltaTime }.Schedule(_query).Complete(); }),
                ("C++ IJobEntity", () => { World.DefaultWorld = _entityCppWorld; new MoveJobEntityCpp { DeltaTime = DeltaTime }.Schedule(_query).Complete(); }),
                ("ISPC IJobEntity", () => { World.DefaultWorld = _entityIspcWorld; new MoveJobEntityIspc { DeltaTime = DeltaTime }.Schedule(_query).Complete(); }),
                ("ISPC MT IJobEntity", () => { World.DefaultWorld = _entityIspcMtWorld; new MoveJobEntityIspcMt { DeltaTime = DeltaTime }.Schedule(_query).Complete(); }),
            };
            double[] light = RunInterleavedBenchmark(lightCases, WarmupFrames, MeasureFrames, 0);
            double csharpAverage = light[0], cppAverage = light[1], cppFastAverage = light[2], ispcAverage = light[3];
            double entityCsharpAverage = light[4], entityCppAverage = light[5], entityIspcAverage = light[6], entityIspcMtAverage = light[7];
            VerifyResults("Light Verify", Epsilon);
            VerifyExpectedPositions("Light Expected", _csharpWorld, BenchmarkRounds * (WarmupFrames + MeasureFrames), Epsilon);

            Console.WriteLine();
            Console.WriteLine($"C# IJobChunk       : {csharpAverage:F3} ms/frame");
            Console.WriteLine($"C++ IJobChunk      : {cppAverage:F3} ms/frame");
            Console.WriteLine($"C++ Fast IJobChunk : {cppFastAverage:F3} ms/frame");
            Console.WriteLine($"ISPC IJobChunk     : {ispcAverage:F3} ms/frame");
            Console.WriteLine($"C# IJobEntity      : {entityCsharpAverage:F3} ms/frame");
            Console.WriteLine($"C++ IJobEntity     : {entityCppAverage:F3} ms/frame");
            Console.WriteLine($"ISPC IJobEntity    : {entityIspcAverage:F3} ms/frame");
            Console.WriteLine($"ISPC MT IJobEntity : {entityIspcMtAverage:F3} ms/frame");
            Console.WriteLine($"C++ Speedup        : {csharpAverage / cppAverage:F2}x");
            Console.WriteLine($"C++ Fast Speedup   : {csharpAverage / cppFastAverage:F2}x");
            Console.WriteLine($"ISPC Speedup       : {csharpAverage / ispcAverage:F2}x");
            Console.WriteLine($"C++ Entity Speedup : {entityCsharpAverage / entityCppAverage:F2}x");
            Console.WriteLine($"ISPC Entity Speedup: {entityCsharpAverage / entityIspcAverage:F2}x");
            Console.WriteLine($"ISPC MT Entity Spd : {entityCsharpAverage / entityIspcMtAverage:F2}x");

            Console.WriteLine();
            Console.WriteLine("=== IJobChunk 100w Heavy 计算对比 ===");
            Console.WriteLine($"实体数: {EntityCount:N0}, Warmup: {WarmupFrames}, Measure: {MeasureFrames}, dt={DeltaTime:F6}, iterations={HeavyIterations}");
            Console.WriteLine($"轮次: {BenchmarkRounds}，汇总时忽略首轮");
            Console.WriteLine();

            var heavyCases = new (string Label, Action Run)[]
            {
                ("C# Heavy IJobChunk", () => { World.DefaultWorld = _csharpWorld; new HeavyJobChunkCSharp { DeltaTime = DeltaTime }.Schedule(_query).Complete(); }),
                ("C++ Heavy IJobChunk", () => { World.DefaultWorld = _cppWorld; new HeavyJobChunkCpp { DeltaTime = DeltaTime }.Schedule(_query).Complete(); }),
                ("C++ Fast Heavy IJobChunk", () => { World.DefaultWorld = _cppFastWorld; new HeavyJobChunkCppFast { DeltaTime = DeltaTime }.Schedule(_query).Complete(); }),
                ("ISPC Heavy IJobChunk", () => { World.DefaultWorld = _ispcWorld; new HeavyJobChunkIspc { DeltaTime = DeltaTime }.Schedule(_query).Complete(); }),
                ("C# Heavy IJobEntity", () => { World.DefaultWorld = _entityCsharpWorld; new HeavyJobEntityCSharp { DeltaTime = DeltaTime }.Schedule(_query).Complete(); }),
                ("C++ Heavy IJobEntity", () => { World.DefaultWorld = _entityCppWorld; new HeavyJobEntityCpp { DeltaTime = DeltaTime }.Schedule(_query).Complete(); }),
                ("ISPC Heavy IJobEntity", () => { World.DefaultWorld = _entityIspcWorld; new HeavyJobEntityIspc { DeltaTime = DeltaTime }.Schedule(_query).Complete(); }),
                ("ISPC MT Heavy IJobEntity", () => { World.DefaultWorld = _entityIspcMtWorld; new HeavyJobEntityIspcMt { DeltaTime = DeltaTime }.Schedule(_query).Complete(); }),
            };
            double[] heavy = RunInterleavedBenchmark(heavyCases, WarmupFrames, MeasureFrames, 0);
            double csharpHeavyAverage = heavy[0], cppHeavyAverage = heavy[1], cppFastHeavyAverage = heavy[2], ispcHeavyAverage = heavy[3];
            double entityCsharpHeavyAverage = heavy[4], entityCppHeavyAverage = heavy[5], entityIspcHeavyAverage = heavy[6], entityIspcMtHeavyAverage = heavy[7];
            VerifyResults("Heavy Verify", HeavyEpsilon);

            Console.WriteLine();
            Console.WriteLine($"C# Heavy IJobChunk      : {csharpHeavyAverage:F3} ms/frame");
            Console.WriteLine($"C++ Heavy IJobChunk     : {cppHeavyAverage:F3} ms/frame");
            Console.WriteLine($"C++ Fast Heavy IJobChunk: {cppFastHeavyAverage:F3} ms/frame");
            Console.WriteLine($"ISPC Heavy IJobChunk    : {ispcHeavyAverage:F3} ms/frame");
            Console.WriteLine($"C# Heavy IJobEntity     : {entityCsharpHeavyAverage:F3} ms/frame");
            Console.WriteLine($"C++ Heavy IJobEntity    : {entityCppHeavyAverage:F3} ms/frame");
            Console.WriteLine($"ISPC Heavy IJobEntity   : {entityIspcHeavyAverage:F3} ms/frame");
            Console.WriteLine($"ISPC MT Heavy IJobEntity: {entityIspcMtHeavyAverage:F3} ms/frame");
            Console.WriteLine($"C++ Heavy Speedup       : {csharpHeavyAverage / cppHeavyAverage:F2}x");
            Console.WriteLine($"C++ Fast Heavy Speedup  : {csharpHeavyAverage / cppFastHeavyAverage:F2}x");
            Console.WriteLine($"ISPC Heavy Speedup      : {csharpHeavyAverage / ispcHeavyAverage:F2}x");
            Console.WriteLine($"C++ Entity Heavy Speedup: {entityCsharpHeavyAverage / entityCppHeavyAverage:F2}x");
            Console.WriteLine($"ISPC Entity Heavy Spd   : {entityCsharpHeavyAverage / entityIspcHeavyAverage:F2}x");
            Console.WriteLine($"ISPC MT Entity Heavy Spd: {entityCsharpHeavyAverage / entityIspcMtHeavyAverage:F2}x");

            Console.WriteLine();
            Console.WriteLine("=== IJobChunk 100w Sleep 帧间隔移动对比 ===");
            Console.WriteLine($"实体数: {EntityCount:N0}, Warmup: {SleepWarmupFrames}, Measure: {SleepMeasureFrames}, dt={DeltaTime:F6}, Sleep={FrameSleepMilliseconds}ms");
            Console.WriteLine($"轮次: {BenchmarkRounds}，汇总时忽略首轮");
            Console.WriteLine("说明: 每次 Schedule().Complete() 后 Thread.Sleep(16ms)，只统计 Job 耗时，不统计 Sleep。");
            Console.WriteLine();

            var sleepCases = new (string Label, Action Run)[]
            {
                ("Sleep C# IJobChunk", () => { World.DefaultWorld = _sleepChunkCsharpWorld; new MoveJobChunkCSharp { DeltaTime = DeltaTime }.Schedule(_query).Complete(); }),
                ("Sleep C++ IJobChunk", () => { World.DefaultWorld = _sleepChunkCppWorld; new MoveJobChunkCpp { DeltaTime = DeltaTime }.Schedule(_query).Complete(); }),
                ("Sleep ISPC IJobChunk", () => { World.DefaultWorld = _sleepChunkIspcWorld; new MoveJobChunkIspc { DeltaTime = DeltaTime }.Schedule(_query).Complete(); }),
                ("Sleep C# IJobEntity", () => { World.DefaultWorld = _sleepEntityCsharpWorld; new MoveJobEntityCSharp { DeltaTime = DeltaTime }.Schedule(_query).Complete(); }),
                ("Sleep C++ IJobEntity", () => { World.DefaultWorld = _sleepEntityCppWorld; new MoveJobEntityCpp { DeltaTime = DeltaTime }.Schedule(_query).Complete(); }),
                ("Sleep ISPC IJobEntity", () => { World.DefaultWorld = _sleepEntityIspcWorld; new MoveJobEntityIspc { DeltaTime = DeltaTime }.Schedule(_query).Complete(); }),
            };
            double[] sleep = RunInterleavedBenchmark(sleepCases, SleepWarmupFrames, SleepMeasureFrames, FrameSleepMilliseconds);
            double sleepChunkCsharpAverage = sleep[0], sleepChunkCppAverage = sleep[1], sleepChunkIspcAverage = sleep[2];
            double sleepEntityCsharpAverage = sleep[3], sleepEntityCppAverage = sleep[4], sleepEntityIspcAverage = sleep[5];
            VerifySleepResults("Sleep Verify", Epsilon);
            VerifyExpectedPositions("Sleep Expected", _sleepChunkCsharpWorld, BenchmarkRounds * (SleepWarmupFrames + SleepMeasureFrames), Epsilon);

            Console.WriteLine();
            Console.WriteLine($"Sleep C# IJobChunk   : {sleepChunkCsharpAverage:F3} ms/frame");
            Console.WriteLine($"Sleep C++ IJobChunk  : {sleepChunkCppAverage:F3} ms/frame");
            Console.WriteLine($"Sleep ISPC IJobChunk : {sleepChunkIspcAverage:F3} ms/frame");
            Console.WriteLine($"Sleep C# IJobEntity  : {sleepEntityCsharpAverage:F3} ms/frame");
            Console.WriteLine($"Sleep C++ IJobEntity : {sleepEntityCppAverage:F3} ms/frame");
            Console.WriteLine($"Sleep ISPC IJobEntity: {sleepEntityIspcAverage:F3} ms/frame");
            Console.WriteLine($"Sleep C++ Chunk Spd  : {sleepChunkCsharpAverage / sleepChunkCppAverage:F2}x");
            Console.WriteLine($"Sleep ISPC Chunk Spd : {sleepChunkCsharpAverage / sleepChunkIspcAverage:F2}x");
            Console.WriteLine($"Sleep C++ Entity Spd : {sleepEntityCsharpAverage / sleepEntityCppAverage:F2}x");
            Console.WriteLine($"Sleep ISPC Entity Spd: {sleepEntityCsharpAverage / sleepEntityIspcAverage:F2}x");
        }

        private double RunCSharp()
        {
            World.DefaultWorld = _csharpWorld;
            var job = new MoveJobChunkCSharp { DeltaTime = DeltaTime };
            return RunBenchmark("C# IJobChunk", () => job.Schedule(_query).Complete());
        }

        private double RunCpp()
        {
            World.DefaultWorld = _cppWorld;
            var job = new MoveJobChunkCpp { DeltaTime = DeltaTime };
            return RunBenchmark("C++ IJobChunk", () => job.Schedule(_query).Complete());
        }

        private double RunCppFast()
        {
            World.DefaultWorld = _cppFastWorld;
            var job = new MoveJobChunkCppFast { DeltaTime = DeltaTime };
            return RunBenchmark("C++ Fast IJobChunk", () => job.Schedule(_query).Complete());
        }

        private double RunIspc()
        {
            World.DefaultWorld = _ispcWorld;
            var job = new MoveJobChunkIspc { DeltaTime = DeltaTime };
            return RunBenchmark("ISPC IJobChunk", () => job.Schedule(_query).Complete());
        }

        private double RunEntityCSharp()
        {
            World.DefaultWorld = _entityCsharpWorld;
            var job = new MoveJobEntityCSharp { DeltaTime = DeltaTime };
            return RunBenchmark("C# IJobEntity", () => job.Schedule(_query).Complete());
        }

        private double RunEntityCpp()
        {
            World.DefaultWorld = _entityCppWorld;
            var job = new MoveJobEntityCpp { DeltaTime = DeltaTime };
            return RunBenchmark("C++ IJobEntity", () => job.Schedule(_query).Complete());
        }

        private double RunEntityIspc()
        {
            World.DefaultWorld = _entityIspcWorld;
            var job = new MoveJobEntityIspc { DeltaTime = DeltaTime };
            return RunBenchmark("ISPC IJobEntity", () => job.Schedule(_query).Complete());
        }

        private double RunEntityIspcMt()
        {
            World.DefaultWorld = _entityIspcMtWorld;
            var job = new MoveJobEntityIspcMt { DeltaTime = DeltaTime };
            return RunBenchmark("ISPC MT IJobEntity", () => job.Schedule(_query).Complete());
        }

        private double RunHeavyCSharp()
        {
            World.DefaultWorld = _csharpWorld;
            var job = new HeavyJobChunkCSharp { DeltaTime = DeltaTime };
            return RunBenchmark("C# Heavy IJobChunk", () => job.Schedule(_query).Complete());
        }

        private double RunHeavyCpp()
        {
            World.DefaultWorld = _cppWorld;
            var job = new HeavyJobChunkCpp { DeltaTime = DeltaTime };
            return RunBenchmark("C++ Heavy IJobChunk", () => job.Schedule(_query).Complete());
        }

        private double RunHeavyCppFast()
        {
            World.DefaultWorld = _cppFastWorld;
            var job = new HeavyJobChunkCppFast { DeltaTime = DeltaTime };
            return RunBenchmark("C++ Fast Heavy IJobChunk", () => job.Schedule(_query).Complete());
        }

        private double RunHeavyIspc()
        {
            World.DefaultWorld = _ispcWorld;
            var job = new HeavyJobChunkIspc { DeltaTime = DeltaTime };
            return RunBenchmark("ISPC Heavy IJobChunk", () => job.Schedule(_query).Complete());
        }

        private double RunHeavyEntityCSharp()
        {
            World.DefaultWorld = _entityCsharpWorld;
            var job = new HeavyJobEntityCSharp { DeltaTime = DeltaTime };
            return RunBenchmark("C# Heavy IJobEntity", () => job.Schedule(_query).Complete());
        }

        private double RunHeavyEntityCpp()
        {
            World.DefaultWorld = _entityCppWorld;
            var job = new HeavyJobEntityCpp { DeltaTime = DeltaTime };
            return RunBenchmark("C++ Heavy IJobEntity", () => job.Schedule(_query).Complete());
        }

        private double RunHeavyEntityIspc()
        {
            World.DefaultWorld = _entityIspcWorld;
            var job = new HeavyJobEntityIspc { DeltaTime = DeltaTime };
            return RunBenchmark("ISPC Heavy IJobEntity", () => job.Schedule(_query).Complete());
        }

        private double RunHeavyEntityIspcMt()
        {
            World.DefaultWorld = _entityIspcMtWorld;
            var job = new HeavyJobEntityIspcMt { DeltaTime = DeltaTime };
            return RunBenchmark("ISPC MT Heavy IJobEntity", () => job.Schedule(_query).Complete());
        }

        private double RunSleepChunkCSharp()
        {
            World.DefaultWorld = _sleepChunkCsharpWorld;
            var job = new MoveJobChunkCSharp { DeltaTime = DeltaTime };
            return RunSleepBenchmark("Sleep C# IJobChunk", () => job.Schedule(_query).Complete());
        }

        private double RunSleepChunkCpp()
        {
            World.DefaultWorld = _sleepChunkCppWorld;
            var job = new MoveJobChunkCpp { DeltaTime = DeltaTime };
            return RunSleepBenchmark("Sleep C++ IJobChunk", () => job.Schedule(_query).Complete());
        }

        private double RunSleepChunkIspc()
        {
            World.DefaultWorld = _sleepChunkIspcWorld;
            var job = new MoveJobChunkIspc { DeltaTime = DeltaTime };
            return RunSleepBenchmark("Sleep ISPC IJobChunk", () => job.Schedule(_query).Complete());
        }

        private double RunSleepEntityCSharp()
        {
            World.DefaultWorld = _sleepEntityCsharpWorld;
            var job = new MoveJobEntityCSharp { DeltaTime = DeltaTime };
            return RunSleepBenchmark("Sleep C# IJobEntity", () => job.Schedule(_query).Complete());
        }

        private double RunSleepEntityCpp()
        {
            World.DefaultWorld = _sleepEntityCppWorld;
            var job = new MoveJobEntityCpp { DeltaTime = DeltaTime };
            return RunSleepBenchmark("Sleep C++ IJobEntity", () => job.Schedule(_query).Complete());
        }

        private double RunSleepEntityIspc()
        {
            World.DefaultWorld = _sleepEntityIspcWorld;
            var job = new MoveJobEntityIspc { DeltaTime = DeltaTime };
            return RunSleepBenchmark("Sleep ISPC IJobEntity", () => job.Schedule(_query).Complete());
        }

        private static double[] RunInterleavedBenchmark(
            (string Label, Action Run)[] cases,
            int warmupFrames,
            int measureFrames,
            int sleepMilliseconds)
        {
            var roundAverages = new double[BenchmarkRounds, cases.Length];
            for (int round = 0; round < BenchmarkRounds; round++)
            {
                if (sleepMilliseconds == 0)
                {
                    int caseOffset = round % cases.Length;
                    for (int step = 0; step < cases.Length; step++)
                    {
                        int index = (caseOffset + step) % cases.Length;
                        for (int frame = 0; frame < warmupFrames; frame++) cases[index].Run();

                        double total = 0;
                        for (int frame = 0; frame < measureFrames; frame++)
                        {
                            long start = Stopwatch.GetTimestamp();
                            cases[index].Run();
                            long end = Stopwatch.GetTimestamp();
                            total += (end - start) * 1000.0 / Stopwatch.Frequency;
                        }
                        roundAverages[round, index] = total / measureFrames;
                    }
                    continue;
                }

                for (int frame = 0; frame < warmupFrames; frame++)
                {
                    int offset = (round + frame) % cases.Length;
                    for (int step = 0; step < cases.Length; step++)
                    {
                        cases[(offset + step) % cases.Length].Run();
                        if (sleepMilliseconds > 0) Thread.Sleep(sleepMilliseconds);
                    }
                }

                var totals = new double[cases.Length];
                for (int frame = 0; frame < measureFrames; frame++)
                {
                    int offset = (round + warmupFrames + frame) % cases.Length;
                    for (int step = 0; step < cases.Length; step++)
                    {
                        int index = (offset + step) % cases.Length;
                        long start = Stopwatch.GetTimestamp();
                        cases[index].Run();
                        long end = Stopwatch.GetTimestamp();
                        totals[index] += (end - start) * 1000.0 / Stopwatch.Frequency;
                        if (sleepMilliseconds > 0) Thread.Sleep(sleepMilliseconds);
                    }
                }

                for (int index = 0; index < cases.Length; index++)
                {
                    roundAverages[round, index] = totals[index] / measureFrames;
                }
            }

            int firstAcceptedRound = BenchmarkRounds > 1 ? 1 : 0;
            int acceptedRoundCount = BenchmarkRounds - firstAcceptedRound;
            var averages = new double[cases.Length];
            for (int index = 0; index < cases.Length; index++)
            {
                double total = 0;
                for (int round = firstAcceptedRound; round < BenchmarkRounds; round++)
                {
                    total += roundAverages[round, index];
                }
                averages[index] = total / acceptedRoundCount;
                Console.WriteLine($"{cases[index].Label,-26}: avg={averages[index]:F3} ms");
            }
            return averages;
        }

        private static double RunBenchmark(string label, Action scheduleAndComplete)
        {
            for (int frame = 0; frame < WarmupFrames; frame++)
            {
                scheduleAndComplete();
            }

            double totalMilliseconds = 0;
            for (int frame = 0; frame < MeasureFrames; frame++)
            {
                long start = Stopwatch.GetTimestamp();
                scheduleAndComplete();
                long end = Stopwatch.GetTimestamp();
                double elapsed = (end - start) * 1000.0 / Stopwatch.Frequency;
                totalMilliseconds += elapsed;
            }

            double average = totalMilliseconds / MeasureFrames;
            Console.WriteLine($"{label,-14}: avg={average:F3} ms");
            return average;
        }

        private static double RunSleepBenchmark(string label, Action scheduleAndComplete)
        {
            for (int frame = 0; frame < SleepWarmupFrames; frame++)
            {
                scheduleAndComplete();
                Thread.Sleep(FrameSleepMilliseconds);
            }

            double totalMilliseconds = 0;
            for (int frame = 0; frame < SleepMeasureFrames; frame++)
            {
                long start = Stopwatch.GetTimestamp();
                scheduleAndComplete();
                long end = Stopwatch.GetTimestamp();
                double elapsed = (end - start) * 1000.0 / Stopwatch.Frequency;
                totalMilliseconds += elapsed;
                Thread.Sleep(FrameSleepMilliseconds);
            }

            double average = totalMilliseconds / SleepMeasureFrames;
            Console.WriteLine($"{label,-22}: avg={average:F3} ms");
            return average;
        }

        private void VerifyResults(string label, float epsilon)
        {
            var targets = new[]
            {
                ("C++", _cppWorld),
                ("C++ Fast", _cppFastWorld),
                ("ISPC", _ispcWorld),
                ("C# Entity", _entityCsharpWorld),
                ("C++ Entity", _entityCppWorld),
                ("ISPC Entity", _entityIspcWorld),
                ("ISPC MT Entity", _entityIspcMtWorld),
            };

            VerifyPositionWorlds(label, epsilon, _csharpWorld, targets);
        }

        private void VerifySleepResults(string label, float epsilon)
        {
            var targets = new[]
            {
                ("C++ Chunk", _sleepChunkCppWorld),
                ("ISPC Chunk", _sleepChunkIspcWorld),
                ("C# Entity", _sleepEntityCsharpWorld),
                ("C++ Entity", _sleepEntityCppWorld),
                ("ISPC Entity", _sleepEntityIspcWorld),
            };

            VerifyPositionWorlds(label, epsilon, _sleepChunkCsharpWorld, targets);
        }

        private void VerifyPositionWorlds(string label, float epsilon, World baselineWorld, (string Label, World World)[] targetWorlds)
        {
            var csharpChunks = GetPositionChunks(baselineWorld);
            var targets = targetWorlds
                .Select(target => (target.Label, Chunks: GetPositionChunks(target.World)))
                .ToArray();

            float[] maxDiffs = new float[targets.Length];
            int[] mismatchCounts = new int[targets.Length];

            for (int chunkIndex = 0; chunkIndex < csharpChunks.Length; chunkIndex++)
            {
                var csharpChunk = csharpChunks[chunkIndex];
                int entityOffset = 0;
                for (int previousChunk = 0; previousChunk < chunkIndex; previousChunk++)
                {
                    entityOffset += csharpChunks[previousChunk].Count;
                }

                for (int targetIndex = 0; targetIndex < targets.Length; targetIndex++)
                {
                    var target = targets[targetIndex];
                    if (chunkIndex >= target.Chunks.Length)
                    {
                        mismatchCounts[targetIndex] += csharpChunk.Count;
                        continue;
                    }

                    var targetChunk = target.Chunks[chunkIndex];
                    int count = Math.Min(csharpChunk.Count, targetChunk.Count);

                    for (int localIndex = 0; localIndex < count; localIndex++)
                    {
                        int entityIndex = entityOffset + localIndex;
                        float2 csharp = csharpChunk.Positions[localIndex].Value;
                        float2 actual = targetChunk.Positions[localIndex].Value;
                        float diff = MathF.Max(MathF.Abs(csharp.x - actual.x), MathF.Abs(csharp.y - actual.y));

                        if (diff > epsilon)
                        {
                            mismatchCounts[targetIndex]++;
                            if (mismatchCounts[targetIndex] <= 3)
                            {
                                Console.WriteLine($"{target.Item1} mismatch entity {entityIndex}: C#=({csharp.x:F4},{csharp.y:F4}) {target.Item1}=({actual.x:F4},{actual.y:F4}) diff={diff:E4}");
                            }
                        }

                        if (diff > maxDiffs[targetIndex]) maxDiffs[targetIndex] = diff;
                    }

                    if (targetChunk.Count != csharpChunk.Count)
                    {
                        mismatchCounts[targetIndex] += Math.Abs(targetChunk.Count - csharpChunk.Count);
                    }
                }
            }

            bool passed = mismatchCounts.All(count => count == 0);
            Console.WriteLine(passed
                ? $"{label,-13}: OK, {FormatVerifySummary(targets, maxDiffs)}, epsilon={epsilon:E4}"
                : $"{label,-13}: ERROR, {FormatMismatchSummary(targets, mismatchCounts)}, {FormatVerifySummary(targets, maxDiffs)}, epsilon={epsilon:E4}");
        }

        private static string FormatVerifySummary((string Label, PositionChunkView[] Chunks)[] targets, float[] maxDiffs)
        {
            return string.Join(", ", targets.Select((target, index) => $"{target.Label}MaxDiff={maxDiffs[index]:E4}"));
        }

        private static string FormatMismatchSummary((string Label, PositionChunkView[] Chunks)[] targets, int[] mismatchCounts)
        {
            return string.Join(", ", targets.Select((target, index) => $"{target.Label}Mismatch={mismatchCounts[index]:N0}"));
        }

        private void VerifyExpectedPositions(string label, World world, int frameCount, float epsilon)
        {
            float expectedEpsilon = MathF.Max(epsilon, 0.01f);
            var chunks = GetPositionChunks(world);
            int entityIndex = 0;
            int mismatchCount = 0;
            float maxDiff = 0;

            foreach (var chunk in chunks)
            {
                for (int localIndex = 0; localIndex < chunk.Count; localIndex++, entityIndex++)
                {
                    float2 expected = CreateInitialPosition(entityIndex);
                    float2 frameDelta = CreateInitialVelocity(entityIndex) * DeltaTime;
                    for (int frame = 0; frame < frameCount; frame++) expected += frameDelta;
                    float2 actual = chunk.Positions[localIndex].Value;
                    float diff = MathF.Max(MathF.Abs(expected.x - actual.x), MathF.Abs(expected.y - actual.y));
                    if (diff > maxDiff) maxDiff = diff;
                    if (diff > expectedEpsilon) mismatchCount++;
                }
            }

            bool passed = entityIndex == EntityCount && mismatchCount == 0;
            Console.WriteLine(passed
                ? $"{label,-13}: OK, entities={entityIndex:N0}, MaxDiff={maxDiff:E4}, epsilon={expectedEpsilon:E4}"
                : $"{label,-13}: ERROR, entities={entityIndex:N0}, mismatches={mismatchCount:N0}, MaxDiff={maxDiff:E4}");
        }

        private static void CreateEntities(World world)
        {
            var entityManager = world.EntityManager;
            for (int index = 0; index < EntityCount; index++)
            {
                var entity = entityManager.NewEntity(typeof(MovePosition), typeof(MoveVelocity));
                entityManager.Set(entity, new MovePosition { Value = CreateInitialPosition(index) });
                entityManager.Set(entity, new MoveVelocity { Value = CreateInitialVelocity(index) });
            }
        }

        private static float2 CreateInitialPosition(int index)
        {
            float x = index % 1920;
            float y = index % 1080;
            return new float2(x, y);
        }

        private static float2 CreateInitialVelocity(int index)
        {
            float x = ((index * 17) % 201 - 100) * 0.25f;
            float y = ((index * 31) % 201 - 100) * 0.25f;
            return new float2(x, y);
        }

        private PositionChunkView[] GetPositionChunks(World world)
        {
            var chunks = new List<PositionChunkView>();
            var entityManager = world.EntityManager;
            for (int archetypeIndex = 0; archetypeIndex < entityManager.ArchetypeCount; archetypeIndex++)
            {
                var archetype = entityManager.Archetypes[archetypeIndex];
                if (archetype == null || !archetype.IsMatch(_query))
                {
                    continue;
                }

                foreach (var chunk in archetype.GetChunks())
                {
                    if (chunk.EntityCount == 0)
                    {
                        continue;
                    }

                    int positionTypeIndex = archetype.GetComponentTypeIndex<MovePosition>();
                    chunks.Add(new PositionChunkView((MovePosition*)chunk.GetComponentArrayPointer(positionTypeIndex), chunk.EntityCount));
                }
            }

            return chunks.ToArray();
        }

        private readonly struct PositionChunkView
        {
            public readonly MovePosition* Positions;
            public readonly int Count;

            public PositionChunkView(MovePosition* positions, int count)
            {
                Positions = positions;
                Count = count;
            }
        }

        public void Dispose()
        {
            _sleepEntityIspcWorld.Dispose();
            _sleepEntityCppWorld.Dispose();
            _sleepEntityCsharpWorld.Dispose();
            _sleepChunkIspcWorld.Dispose();
            _sleepChunkCppWorld.Dispose();
            _sleepChunkCsharpWorld.Dispose();
            _entityIspcMtWorld.Dispose();
            _entityIspcWorld.Dispose();
            _entityCppWorld.Dispose();
            _entityCsharpWorld.Dispose();
            _ispcWorld.Dispose();
            _cppFastWorld.Dispose();
            _cppWorld.Dispose();
            _csharpWorld.Dispose();
        }
    }

}
