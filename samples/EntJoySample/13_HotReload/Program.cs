using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using EntJoy.Collections;
using EntJoy.JobSystem;
using NativeTranspiler.Bindings;

namespace EntJoySample.HotReload
{
    /// <summary>
    /// 热重载样例：每 500 ms 跑一帧 <see cref="HotReloadAddJob"/>，并在安全点检查新构建的
    /// NativeTranspiled.dll（监视 `bin`）—— 有就换模块，值随内核常量变化（101 → 102）。
    /// <code>
    /// dotnet build samples\EntJoySample\EntJoySample.csproj -c Release -o artifacts\hotreload-demo # 一次性：准备宿主目录
    /// artifacts\hotreload-demo\EntJoySample.exe # 宿主（Ctrl+C 退出）
    /// dotnet build samples\EntJoySample\EntJoySample.csproj -c Release # 之后：改完就编（零参数）
    /// </code>
    /// ⚠ 宿主不能跑在 `bin` 里：那样它锁住 `bin`，普通构建就写不进去（MSB3021/MSB3027）。
    /// </summary>
    public static class Program
    {
        private const int N = 1024;

        public static int Main()
        {
            string binDir = Path.GetFullPath(Path.Combine(RepoRoot(), "bin"));
            if (SameDir(AppContext.BaseDirectory, binDir))
            {
                Console.WriteLine("宿主不能跑在 bin 里（它会锁住 bin，普通构建就写不进去）。请：");
                Console.WriteLine(@"  dotnet build samples\EntJoySample\EntJoySample.csproj -c Release -o artifacts\hotreload-demo");
                Console.WriteLine(@"  artifacts\hotreload-demo\EntJoySample.exe");
                return 1;
            }

            // 原生 worker 池是它建的；不初始化 ⇒ 提交的 job 没人取，Complete() 立刻返回、数组保持 0。
            NativeJobScheduler.Initialize();
            var values = new NativeArray<int>(N, Allocator.Persistent);
            using var watcher = new NativeHotReloadWatcher(binDir);   // 监视普通构建的输出目录
            watcher.Start();

            Console.WriteLine("=== EntJoy 热重载样例（Ctrl+C 退出）===");
            Console.WriteLine($"  监视目录: {binDir}");
            Console.WriteLine(@"  改 HotReloadJob.cs 的内核常量后:  dotnet build samples\EntJoySample\EntJoySample.csproj -c Release");

            while (true)
            {
                // 安全点：上一帧的 job 已 Complete（没有在飞的 job），此刻换模块才安全
                if (watcher.TryProcessPending(out NativeReloadResult r, out double ms))
                    Console.WriteLine($"  >>> 热重载: {r.Outcome} swapped={r.Swapped} gen={r.Generation} {ms:F0} ms");

                for (int i = 0; i < N; i++) values[i] = 0;      // 每帧清零 ⇒ 打印的就是这一帧内核加了多少
                var job = new HotReloadAddJob { Values = values, Delta = 100 };
                job.Schedule(N).Complete();
                Console.WriteLine($"values[0]={values[0]}  gen={NativeJobScheduler.DelegateCacheGeneration}");
                Thread.Sleep(500);
            }
        }

        private static bool SameDir(string a, string b)
            => string.Equals(Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar),
                             Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar),
                             StringComparison.OrdinalIgnoreCase);

        /// <summary>仓库根 = 本文件往上三级（编译期常量，与当前工作目录无关）。</summary>
        private static string RepoRoot([CallerFilePath] string thisFile = "")
            => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", ".."));
    }
}
