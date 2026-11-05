// ============================================================================
// JobSystemStressTest.CSharp — C# Managed + Native 调度器全面压力测试
//
// 测试范围：
//   Part A: ManagedJobScheduler（纯 C# Chase-Lev 路径，无 NativeDll）
//   Part B: NativeJobScheduler（C# → C++ P/Invoke 路径）
//
// 构建 & 运行：
//   cd tools/JobSystemStressTest.CSharp
//   dotnet run -c Release
//   dotnet run -c Release -- --timeout 120
//   STRESS_LONG=1 dotnet run -c Release
// ============================================================================

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using EntJoy.JobSystem;
using EntJoy.JobSystem.Managed;

namespace JobSystemStressTest;

class Program
{
    static int _timeoutSec = 60;
    static int _rounds = 1;
    static bool _longMode = false;
    static int _workers = 0;
    static bool _contractsOnly;
    static readonly Stopwatch _wallClock = new();

    static void Main(string[] args)
    {
        Console.OutputEncoding = new System.Text.UTF8Encoding(false);
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--timeout" && i + 1 < args.Length)
                _timeoutSec = int.Parse(args[++i]);
            if (args[i] == "--rounds" && i + 1 < args.Length)
                _rounds = int.Parse(args[++i]);
            if (args[i] == "--workers" && i + 1 < args.Length)
                _workers = int.Parse(args[++i]);
            if (args[i] == "--contracts-only") _contractsOnly = true;
        }
        if (Environment.GetEnvironmentVariable("STRESS_ROUNDS") is { Length: > 0 } r)
            _rounds = Math.Max(1, int.Parse(r));
        if (Environment.GetEnvironmentVariable("STRESS_LONG") == "1")
            _longMode = true;
        if (_workers < 0 || _rounds < 1 || _timeoutSec < 1)
            throw new ArgumentOutOfRangeException(nameof(args));

        Console.WriteLine("============================================================");
        Console.WriteLine("EntJoy C# JobSystem Stress Test");
        Console.WriteLine($"Timeout: {_timeoutSec}s | Rounds: {_rounds} | Long: {_longMode} | Workers: {_workers} (0 = default) | ContractsOnly: {_contractsOnly}");
        Console.WriteLine("============================================================\n");

        _wallClock.Start();

        for (int round = 0; round < _rounds; round++)
        {
            if (_rounds > 1) Console.WriteLine($"\n--- Round {round + 1}/{_rounds} ---\n");

            // ═══════════════════════════════════════════════════════
            // Part A: ManagedJobScheduler（纯 C# 路径）
            // ═══════════════════════════════════════════════════════
            Console.WriteLine("── Part A: ManagedJobScheduler (Pure C#) ──");
            try { ManagedStressTests.RunAll(_timeoutSec, _longMode, _workers, _contractsOnly); }
            finally { ManagedJobScheduler.Shutdown(); }

            // ═══════════════════════════════════════════════════════
            // Part B: NativeJobScheduler（C# → C++ P/Invoke）
            // ═══════════════════════════════════════════════════════
            Console.WriteLine("\n── Part B: NativeJobScheduler (C# → C++) ──");
            try { NativeStressTests.RunAll(_timeoutSec, _longMode, _workers, _contractsOnly); }
            finally { EntJoy.JobSystem.NativeJobScheduler.Shutdown(); }
            NativeStressTests.VerifyDrained();
        }

        _wallClock.Stop();
        Console.WriteLine($"\n============================================================");
        Console.WriteLine($"ALL TESTS PASSED in {_wallClock.Elapsed.TotalSeconds:F2}s");
        Console.WriteLine("============================================================");
    }
}
