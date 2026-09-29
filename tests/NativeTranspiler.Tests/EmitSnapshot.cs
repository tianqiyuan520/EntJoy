using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;
using Xunit.Abstractions;

namespace NativeTranspiler.Tests
{
    /// <summary>
    /// 临时工具：把一组"代表性 job"的产物落盘，用于**逐字对比"改动前 vs 改动后"**，
    /// 证明除了 7 个缺陷点以外没有无关的发射变化（ENTJOY_NT_SNAP_DIR 指定输出目录）。
    /// </summary>
    public class EmitSnapshot
    {
        private readonly ITestOutputHelper _out;
        public EmitSnapshot(ITestOutputHelper output) => _out = output;

        private static readonly (string name, string src)[] Cases =
        {
            ("01_scalar_parallelfor", @"
using NativeTranspiler; using EntJoy.JobSystem; using EntJoy.Collections;
[NativeTranspile]
public struct ScalarPfJob : IJobParallelFor {
    public NativeArray<float> Out; public float Scale; public int Limit;
    public void Execute(int index) {
        if (Out[index] < 0.0f) return;
        Out[index] = Out[index] * Scale;
        if (index > Limit) return;
        Out[index] = Out[index] + 1.0f;
    }
}"),
            ("02_simd_parallelfor", @"
using NativeTranspiler; using EntJoy.JobSystem; using EntJoy.Collections;
[NativeTranspile(AutoSIMD = AutoSIMD.Enabled)]
public struct SimdPfJob : IJobParallelFor {
    public NativeArray<float> Out; public NativeArray<float> Src; public float Bias;
    public void Execute(int index) {
        Out[index] = Src[index] * Bias;
        Out[index * 2] = 3.0f;
        Out[index] = Src[index];
        if (Out[index] < 0.0f) return;
        Out[index] = 1.0f;
    }
}"),
            ("03_chunk_standard", @"
using NativeTranspiler; using EntJoy.ECS; using EntJoy.Collections;
public struct LSnapA { public float V; public float W; }
[NativeTranspile]
public struct ChunkStdJob : IJobChunk {
    public NativeArray<float> Result;
    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask) {
        var arr = chunk.GetComponentDataNativeArray<LSnapA>();
        var other = chunk.GetComponentDataNativeArray<LSnapA>();
        for (int i = 0; i < arr.Length; i++) {
            var e = arr[i];
            if (e.V > 0.0f) { arr[i] = other[i]; }
            Result[i] = e.V + e.W;
        }
    }
}"),
            ("04_chunk_autosimd", @"
using NativeTranspiler; using EntJoy.ECS; using EntJoy.Collections;
[NativeTranspile(AutoSIMD = AutoSIMD.Enabled)]
public struct ChunkSimdJob : IJobChunk {
    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask) {
        var values = chunk.GetComponentDataNativeArray<float>();
        for (int index = 0; index < values.Length; index++) {
            values[index] = 1.0f;
        }
    }
}"),
            ("05_entity_standard", @"
using NativeTranspiler; using EntJoy.ECS;
public struct LSnapB { public float X; public float Y; }
[NativeTranspile]
public struct EntityStdJob : IJobEntity {
    public float Dt;
    public void Execute(ref LSnapB p) { p.X = p.X + Dt; p.Y = p.Y - Dt; }
}"),
            ("06_entity_simd", @"
using NativeTranspiler; using EntJoy.ECS;
public struct LSnapC { public float X; public float Marker; }
[NativeTranspile(AutoSIMD = AutoSIMD.Enabled)]
public struct EntitySimdJob : IJobEntity {
    public void Execute(ref LSnapC p) {
        if (p.X < 0.0f) return;
        p.Marker = p.X * 2.0f;
    }
}"),
            ("07_entity_vectorize", @"
using NativeTranspiler; using EntJoy.ECS;
public struct LSnapD { public float X; public float Marker; }
[NativeTranspile(AutoSIMD = AutoSIMD.Vectorize)]
public struct EntityVecJob : IJobEntity {
    public void Execute(ref LSnapD p) {
        if (p.X < 0.0f) return;
        p.Marker = p.X * 2.0f;
    }
}"),
            ("08_ijob_autosimd", @"
using NativeTranspiler; using EntJoy.JobSystem; using EntJoy.Collections;
[NativeTranspile(AutoSIMD = AutoSIMD.Enabled)]
public struct SingleSimdJob : IJob {
    public NativeArray<float> Out;
    public void Execute() {
        for (int i = 0; i < Out.Length; i++) { Out[i] = Out[i] + 1.0f; }
    }
}"),
            ("09_batch_job", @"
using NativeTranspiler; using EntJoy.JobSystem; using EntJoy.Collections;
[NativeTranspile]
public struct RangeJob : IJobParallelForBatch {
    public NativeArray<int> Values; public int Delta;
    public void Execute(int startIndex, int count) {
        for (int i = startIndex; i < startIndex + count; i++) { Values[i] += Delta; }
    }
}"),
            ("10_perlane_simd", @"
using NativeTranspiler; using EntJoy.JobSystem; using EntJoy.Collections;
[NativeTranspile(AutoSIMD = AutoSIMD.Enabled)]
public struct PerLaneSnapJob : IJobParallelFor {
    public NativeArray<float> Out; public float Bias;
    public void Execute(int index) {
        if (Out[index] < 0.0f) return;
        for (int k = 0; k < index; k = k + 1) { Bias = Bias + 1.0f; }
        Out[index] = Bias;
    }
}"),
        };

        [Fact]
        public void Snapshot()
        {
            var root = Environment.GetEnvironmentVariable("ENTJOY_NT_SNAP_DIR");
            if (string.IsNullOrEmpty(root))
            {
                _out.WriteLine("ENTJOY_NT_SNAP_DIR not set — skipping snapshot");
                return;
            }
            Directory.CreateDirectory(root);
            foreach (var (name, src) in Cases)
            {
                var result = GeneratorHarness.EmitFor(src);
                var dir = Path.Combine(root, name);
                Directory.CreateDirectory(dir);
                foreach (var file in Directory.GetFiles(result.OutputDir, "*", SearchOption.TopDirectoryOnly))
                {
                    var ext = Path.GetExtension(file);
                    if (ext != ".cpp" && ext != ".h" && ext != ".ispc") continue;
                    File.WriteAllText(Path.Combine(dir, Path.GetFileName(file)), File.ReadAllText(file));
                }
                File.WriteAllText(Path.Combine(dir, "BINDINGS.g.cs"), result.Bindings);
                _out.WriteLine(name + ": " + result.DiagnosticSummary);
            }
        }
    }
}
