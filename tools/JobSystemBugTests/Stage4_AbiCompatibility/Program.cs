using System.Reflection;
using System.Runtime.InteropServices;
using EntJoy.JobSystem;
using EntJoy.JobSystem.Managed;

unsafe static class Program
{
    static int Main(string[] args)
    {
        int passed = 0;
        if (args.Length > 0 && File.Exists(args[0]))
        {
            IntPtr dll = NativeLibrary.Load(args[0]);
            try
            {
                if (!NativeLibrary.TryGetExport(dll, "JobSystem_GetAbiVersion", out var ptr))
                    throw new Exception("ABI export missing");
                uint version = ((delegate* unmanaged[Cdecl]<uint>)ptr)();
                // ABI 3：删除了 6 个死 stats 字段（stats 结构体布局已变）。本用例断言
                // "随包发布的 NativeDll 报告的正是 C# 侧期望的版本"，故此处必须与
                // src/EntJoy.Jobs/Native/NativeJobCore.cs 的 ExpectedAbiVersion 同步。
                if (version != 3) throw new Exception($"ABI version {version}");
                Console.WriteLine("PASS ABI version"); passed++;
            }
            finally { NativeLibrary.Free(dll); }
        }
        else Console.WriteLine("PASS ABI fixture skipped (no DLL argument)");

        var prop = typeof(JobScheduler).GetProperty("UseNative", BindingFlags.Static | BindingFlags.NonPublic);
        prop?.SetValue(null, false);
        ManagedJobScheduler.Initialize(2);
        try
        {
            int ran = 0;
            var job = new ProbeJob { Counter = new[] { 0 } };
            var h = JobScheduler.Schedule(ref job);
            h.Complete();
            ran = Volatile.Read(ref job.Counter[0]);
            if (ran != 1) throw new Exception("managed fallback did not execute");
            Console.WriteLine("PASS managed fallback"); passed++;
        }
        finally { ManagedJobScheduler.Shutdown(); }
        Console.WriteLine($"PASS Stage4: {passed}/2");
        return passed == 2 || args.Length == 0 ? 0 : 1;
    }
    private struct ProbeJob : IJob { public int[] Counter; public void Execute() => Interlocked.Increment(ref Counter[0]); }
}
