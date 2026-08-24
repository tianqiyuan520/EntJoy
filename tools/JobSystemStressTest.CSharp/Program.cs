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
    static readonly Stopwatch _wallClock = new();

    static void Main(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i] == "--timeout" && i + 1 < args.Length)
                _timeoutSec = int.Parse(args[++i]);
            if (args[i] == "--rounds" && i + 1 < args.Length)
                _rounds = int.Parse(args[++i]);
        }
        if (Environment.GetEnvironmentVariable("STRESS_ROUNDS") is { Length: > 0 } r)
            _rounds = Math.Max(1, int.Parse(r));
        if (Environment.GetEnvironmentVariable("STRESS_LONG") == "1")
            _longMode = true;

        Console.WriteLine("============================================================");
        Console.WriteLine("EntJoy C# JobSystem Stress Test");
        Console.WriteLine($"Timeout: {_timeoutSec}s | Rounds: {_rounds} | Long: {_longMode}");
        Console.WriteLine("============================================================\n");

        _wallClock.Start();

        for (int round = 0; round < _rounds; round++)
        {
            if (_rounds > 1) Console.WriteLine($"\n--- Round {round + 1}/{_rounds} ---\n");

            // ═══════════════════════════════════════════════════════
            // Part A: ManagedJobScheduler（纯 C# 路径）
            // ═══════════════════════════════════════════════════════
            Console.WriteLine("── Part A: ManagedJobScheduler (Pure C#) ──");
            ManagedStressTests.RunAll(_timeoutSec, _longMode);
            ManagedJobScheduler.Shutdown();

            // ═══════════════════════════════════════════════════════
            // Part B: NativeJobScheduler（C# → C++ P/Invoke）
            // ═══════════════════════════════════════════════════════
            Console.WriteLine("\n── Part B: NativeJobScheduler (C# → C++) ──");
            NativeStressTests.RunAll(_timeoutSec, _longMode);
            EntJoy.JobSystem.NativeJobScheduler.Shutdown();
        }

        _wallClock.Stop();
        Console.WriteLine($"\n============================================================");
        Console.WriteLine($"ALL TESTS PASSED in {_wallClock.Elapsed.TotalSeconds:F2}s");
        Console.WriteLine("============================================================");
    }
}
