using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using EntJoy.JobSystem;

// 旧 DLL ABI fixture：验证 ModuleInitializer 加载旧版/不兼容 NativeDll.dll 时
// 能安全回退 Managed（而非崩溃），且 fallback 后 job 正常执行。
//
// 用法：
//   Stage11_AbiFixture <stub_no_abi.dll> <stub_wrong_version.dll>
//     主模式：用 stub 覆盖输出目录的 NativeDll.dll，spawn 子进程验证 fallback。
//   Stage11_AbiFixture --probe
//     探针模式：Initialize → Schedule → Complete，验证 job 执行（Native 或 fallback）。
static class Program
{
    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "--probe")
            return RunProbe();

        if (args.Length < 2)
        {
            Console.WriteLine("usage: Stage11_AbiFixture <stub_no_abi.dll> <stub_wrong_version.dll>");
            return 1;
        }
        return RunFixture(Path.GetFullPath(args[0]), Path.GetFullPath(args[1]));
    }

    // 探针：ModuleInitializer 已加载（真实或 stub）NativeDll.dll。无论 Native 还是
    // fallback Managed，job 都必须正常执行；否则返回非 0。
    static int RunProbe()
    {
        try
        {
            JobScheduler.Initialize(2);
            var job = new ProbeJob { Counter = new[] { 0 } };
            var handle = JobScheduler.Schedule(ref job);
            handle.Complete();
            int ran = Volatile.Read(ref job.Counter[0]);
            JobScheduler.Shutdown();
            Console.WriteLine($"probe executed={ran}");
            return ran == 1 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"probe failed: {ex.Message}");
            return 2;
        }
    }

    // 主模式：覆盖输出目录的 NativeDll.dll → spawn --probe → 验证返回 0 → 恢复。
    static int RunFixture(string stubNoAbi, string stubWrongVersion)
    {
        string exe = Environment.ProcessPath!;
        string nativeDll = Path.Combine(AppContext.BaseDirectory, "NativeDll.dll");
        string backup = nativeDll + ".bak";
        int passed = 0;

        // CI 不提供真实 NativeDll.dll（bin/ 未入库）：不存在时跳过备份，直接用 stub
        // 覆盖（探针子进程靠 stub 触发 fallback）；本地有真实 DLL 时备份并在结束恢复。
        bool hadNative = File.Exists(nativeDll);
        if (hadNative) File.Copy(nativeDll, backup, true);

        try
        {
            File.Copy(stubNoAbi, nativeDll, true);
            int rc = RunChild(exe, "--probe");
            if (rc == 0) { Console.WriteLine("PASS missing-ABI-export fallback"); passed++; }
            else Console.WriteLine($"FAIL missing-ABI-export fallback (exit {rc})");

            File.Copy(stubWrongVersion, nativeDll, true);
            rc = RunChild(exe, "--probe");
            if (rc == 0) { Console.WriteLine("PASS version-mismatch fallback"); passed++; }
            else Console.WriteLine($"FAIL version-mismatch fallback (exit {rc})");
        }
        finally
        {
            if (hadNative) { File.Copy(backup, nativeDll, true); File.Delete(backup); }
            else File.Delete(nativeDll);
        }

        Console.WriteLine($"PASS Stage11: {passed}/2");
        return passed == 2 ? 0 : 1;
    }

    static int RunChild(string exe, string arg)
    {
        var psi = new ProcessStartInfo(exe, arg)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        using var p = Process.Start(psi)!;
        if (!p.WaitForExit(15000))
        {
            try { p.Kill(); } catch { }
            return -1;
        }
        return p.ExitCode;
    }
}

struct ProbeJob : IJob
{
    public int[] Counter;
    public void Execute() => Interlocked.Increment(ref Counter[0]);
}
