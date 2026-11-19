using System;
using System.Diagnostics;
using System.IO;
using Xunit;

namespace NativeTranspiler.Tests;

/// <summary>
/// 把生成出来的 chunk 内核真交给 ispc 编译。
/// 文本断言看不出 varying/uniform 不匹配（G297C951C）这类错误，只有编译器能看出来。
/// 找不到 ispc 时用例失败；确实装不了的机器设 ENTJOY_SKIP_ISPC_COMPILE=1 跳过。
/// </summary>
public class IspcKernelCompilesTests
{
    [Fact]
    public void SerialStencilKernelCompiles()
    {
        var result = GeneratorHarness.EmitForJob(@"using NativeTranspiler; using EntJoy.ECS; using EntJoy.Collections; using EntJoy.Mathematics;
public struct Pair { public float2 Value; }
[NativeTranspile(Target=BackendTarget.Ispc)] public struct StencilPairJob : IJobChunk {
    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask mask) {
        NativeArray<Pair> ps = chunk.GetComponentDataNativeArray<Pair>();
        NativeArray<Pair> vs = chunk.GetComponentDataNativeArray<Pair>();
        for (int index = 0; index < ps.Length; index++) {
            Pair t = ps[index];
            t.Value += vs[index + 1].Value;
            ps[index] = t;
        }
    }
}");
        CompileKernel(result, "StencilPairJob_Execute.ispc");
    }

    [Fact]
    public void SerialInt2StencilKernelCompiles()
    {
        // int2/uint2 的 uniform 算术重载只服务串行路径，缺了它这里会报 G297C951C。
        var result = GeneratorHarness.EmitForJob(@"using NativeTranspiler; using EntJoy.ECS; using EntJoy.Collections; using EntJoy.Mathematics;
public struct Cell { public int2 Value; }
[NativeTranspile(Target=BackendTarget.Ispc)] public struct StencilCellJob : IJobChunk {
    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask mask) {
        NativeArray<Cell> cells = chunk.GetComponentDataNativeArray<Cell>();
        NativeArray<Cell> deltas = chunk.GetComponentDataNativeArray<Cell>();
        for (int index = 0; index < cells.Length; index++) {
            Cell t = cells[index];
            t.Value += deltas[index + 1].Value * 2;
            cells[index] = t;
        }
    }
}");
        CompileKernel(result, "StencilCellJob_Execute.ispc");
    }

    [Fact]
    public void FlattenedChunkKernelCompiles()
    {
        var result = GeneratorHarness.EmitForJob(@"using NativeTranspiler; using EntJoy.ECS; using EntJoy.Collections; using EntJoy.Mathematics;
public struct Pair2 { public float2 Value; }
[NativeTranspile(Target=BackendTarget.Ispc)] public struct FlatCopyJob : IJobChunk {
    public float DeltaTime;
    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask mask) {
        NativeArray<Pair2> ps = chunk.GetComponentDataNativeArray<Pair2>();
        NativeArray<Pair2> vs = chunk.GetComponentDataNativeArray<Pair2>();
        for (int index = 0; index < ps.Length; index++) {
            Pair2 t = ps[index];
            t.Value += vs[index].Value * DeltaTime;
            ps[index] = t;
        }
    }
}");
        var source = File.ReadAllText(FindGenerated(result, "FlatCopyJob_Execute.ispc"));
        Assert.Contains("__flat", source);
        CompileKernel(result, "FlatCopyJob_Execute.ispc");
    }

    private static string FindGenerated(EmitResult result, string fileNameSuffix)
    {
        foreach (var file in Directory.GetFiles(result.OutputDir, "*.ispc", SearchOption.AllDirectories))
            if (Path.GetFileName(file).EndsWith(fileNameSuffix, StringComparison.OrdinalIgnoreCase))
                return file;
        Assert.Fail("生成物里没有 " + fileNameSuffix + Environment.NewLine + result.Dump());
        return null;
    }

    private static void CompileKernel(EmitResult result, string fileNameSuffix)
    {
        string ispc = FindIspc();
        if (ispc == null) return;
        string source = FindGenerated(result, fileNameSuffix);

        string obj = Path.Combine(Path.GetTempPath(), "entjoy-ispc-" + Guid.NewGuid().ToString("N") + ".obj");
        var startInfo = new ProcessStartInfo(ispc)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("--target=host");
        startInfo.ArgumentList.Add("--arch=x86-64");
        startInfo.ArgumentList.Add(source);
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add(obj);

        using var process = Process.Start(startInfo);
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEndAsync();
        process.WaitForExit();
        try { if (File.Exists(obj)) File.Delete(obj); } catch (IOException) { }

        Assert.True(process.ExitCode == 0,
            "ispc 编译失败(" + process.ExitCode + "):" + Environment.NewLine + stderr.Result + stdout.Result +
            Environment.NewLine + result.Dump());
    }

    private static string FindIspc()
    {
        if (Environment.GetEnvironmentVariable("ENTJOY_SKIP_ISPC_COMPILE") == "1") return null;
        string path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var directory in path.Split(Path.PathSeparator))
        {
            if (directory.Length == 0) continue;
            foreach (var name in new[] { "ispc.exe", "ispc" })
            {
                string candidate = Path.Combine(directory, name);
                if (File.Exists(candidate)) return candidate;
            }
        }
        // 缺编译器时让用例失败：这几个用例的全部价值就是"真的编一遍"，
        // 静默跳过等于没有守护。装不了 ispc 的机器显式设 ENTJOY_SKIP_ISPC_COMPILE=1。
        Assert.Fail("找不到 ispc：装到 PATH，或设 ENTJOY_SKIP_ISPC_COMPILE=1 跳过编译守护");
        return null;
    }
}
