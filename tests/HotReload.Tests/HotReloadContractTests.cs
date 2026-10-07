using System;
using System.IO;
using System.Reflection;
using EntJoy.JobSystem;
using Xunit;

namespace HotReload.Tests
{
    /// <summary>
    /// 热重载契约门禁（不需要原生构建/编译器）：重载结果的契约 + 监视器的检测与排队语义。
    /// 真换模块不在这里覆盖，由 `tools/HotReloadProbe` 的 p1-7 / p2 / p4 承担。
    /// </summary>
    public class HotReloadContractTests : IDisposable
    {
        private readonly string _dir;

        public HotReloadContractTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "entjoy-hotreload-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            try { Directory.Delete(_dir, recursive: true); } catch { }
        }

        // ── 重载结果的契约（拒绝不抛异常）──

        [Fact]
        public void ReloadNativeLibrary_MissingFile_ReturnsFileNotFound_InsteadOfThrowing()
        {
            // 拒绝用结果对象表达，只有"编程错误"（null 路径）才抛
            var r = NativeJobScheduler.ReloadNativeLibrary(Path.Combine(_dir, "nope.dll"));
            Assert.Equal(NativeReloadOutcome.FileNotFound, r.Outcome);
            Assert.False(r.Swapped);
            Assert.NotEmpty(r.Message);
        }

        [Fact]
        public void ReloadNativeLibrary_NullPath_Throws()
            => Assert.Throws<ArgumentNullException>(() => NativeJobScheduler.ReloadNativeLibrary(null));

        // ── 监视器：检测 + 改名 copy + 搬清单（不触发真换模块）──

        [Fact]
        public void Watcher_DetectsChangedDll_CopiesIt_AndCarriesTheSidecar()
        {
            string watch = Path.Combine(_dir, "watch");
            Directory.CreateDirectory(watch);
            string dll = Path.Combine(watch, "NativeTranspiled.dll");
            File.WriteAllBytes(dll, new byte[] { 1, 2, 3 });
            File.WriteAllText(Path.ChangeExtension(dll, ".layout.json"), "{}");

            using var w = new NativeHotReloadWatcher(watch, 50);
            w.Start();

            // 换内容（模拟一次构建落地）。注意 mtime 稳定期 250 ms。
            File.WriteAllBytes(dll, new byte[] { 9, 9, 9, 9 });

            bool processed = false;
            NativeReloadResult r = default;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (!processed && sw.Elapsed.TotalSeconds < 15)
            {
                processed = w.TryProcessPending(out r, out _);
                if (!processed) System.Threading.Thread.Sleep(50);
            }
            w.Stop();

            Assert.True(processed, "watcher did not detect the changed dll");
            // 内容不是合法 PE ⇒ 真换模块必然失败；本用例只断言"被检测到并排队"（换模块由原生探针覆盖）
            Assert.False(r.Swapped);
            var copies = Directory.GetFiles(watch, "NativeTranspiled.auto*.dll");
            Assert.Single(copies);
            // 伴生清单必须跟着搬，否则守卫会因"缺声明"拒绝
            Assert.True(File.Exists(Path.ChangeExtension(copies[0], ".layout.json")),
                        "sidecar layout manifest was not carried over with the copy");
            // 自己 copy 出来的不能再次触发（不自激）⇒ 没有新的待处理项
            Assert.False(w.TryProcessPending(out _, out _));
        }

        [Fact]
        public void Watcher_NothingPending_TryProcessPendingReturnsFalse()
        {
            string watch = Path.Combine(_dir, "watch2");
            Directory.CreateDirectory(watch);
            using var w = new NativeHotReloadWatcher(watch, 50);
            Assert.False(w.TryProcessPending(out _, out double ms));
            Assert.Equal(0, ms);
        }

        [Fact]
        public void Watcher_Start_PrunesStaleAutoCopies_ButKeepsTheSourceDll()
        {
            // 上一轮运行留下的改名副本（+ 伴生清单）由下一次启动清掉；监视源文件必须留着
            string watch = Path.Combine(_dir, "watch3");
            Directory.CreateDirectory(watch);
            File.WriteAllBytes(Path.Combine(watch, "NativeTranspiled.autoDEADBEEF.dll"), new byte[] { 1 });
            File.WriteAllBytes(Path.ChangeExtension(Path.Combine(watch, "NativeTranspiled.autoDEADBEEF.dll"), ".layout.json"), new byte[] { 0x7B, 0x7D });
            File.WriteAllBytes(Path.Combine(watch, "NativeTranspiled.dll"), new byte[] { 1, 2, 3 });

            using (var w = new NativeHotReloadWatcher(watch, 50)) w.Start();

            Assert.Empty(Directory.GetFiles(watch, "NativeTranspiled.auto*"));
            Assert.True(File.Exists(Path.Combine(watch, "NativeTranspiled.dll")), "the watched source dll must survive");
        }
    }
}
