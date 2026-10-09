using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;

namespace EntJoy.JobSystem
{
    /// <summary>
    /// 文件监视 + 自动重载（开发期可选）：盯住目录里最新的 `NativeTranspiled*.dll`，内容变了就 copy 成
    /// 新文件名并排队（同路径 `Load` 会返回旧模块），伴生布局清单一起搬。
    /// ⚠ 检测与重载分离：换模块必须由宿主在安全点（已停派发）调 <see cref="TryProcessPending"/> 完成。
    /// </summary>
    public sealed class NativeHotReloadWatcher : IDisposable
    {
        private readonly string _dir;
        private readonly int _pollMs;
        private readonly object _lock = new();
        private Thread _thread;
        private volatile bool _stop;

        private string _lastSeenSha = "";
        private long _lastSeenTicks;
        private string _pendingPath;        // 已 copy 好、等待宿主在安全点处理的新文件
        private int _processed;

        /// <summary>监视 <paramref name="directory"/> 里 `<c>NativeTranspiled*.dll</c> 的变化。</summary>
        public NativeHotReloadWatcher(string directory, int pollMilliseconds = 200)
        {
            if (string.IsNullOrEmpty(directory)) throw new ArgumentNullException(nameof(directory));
            _dir = Path.GetFullPath(directory);
            _pollMs = pollMilliseconds < 50 ? 50 : pollMilliseconds;
        }

        /// <summary>已被宿主处理（或处理失败）的次数（诊断/验收用）。</summary>
        public int ProcessedCount { get { lock (_lock) return _processed; } }

        public void Start()
        {
            if (_thread != null) return;
            _stop = false;
            PruneStaleCopies();
            // 记下基线：启动时已有的那份不算"变化"
            try
            {
                // 只取基线指纹（返回值 = 最新文件路径，这里不需要）
                FindNewest(out string sha, out long ticks);
                _lastSeenSha = sha;
                _lastSeenTicks = ticks;
            }
            catch { /* 目录还没东西：下一轮自然会检测到 */ }

            _thread = new Thread(PollLoop) { IsBackground = true, Name = "EntJoyHotReloadWatcher" };
            _thread.Start();
        }

        /// <summary>
        /// 清掉监视目录里上一次运行留下的改名副本（本次运行还没加载任何副本，删得掉）。
        /// 宿主的旧模块不 Free ⇒ 本进程自己产生的副本只能等进程退出后由下一次启动来清；
        /// 仍被别的进程加载的删不掉（Windows 锁）—— 忽略失败。
        /// </summary>
        private void PruneStaleCopies()
        {
            int removed = 0;
            try
            {
                foreach (var filter in new[] { "NativeTranspiled.auto*.dll", "NativeTranspiled.auto*.layout.json" })
                {
                    foreach (var f in Directory.EnumerateFiles(_dir, filter))
                    {
                        try { File.Delete(f); removed++; } catch { }
                    }
                }
            }
            catch { /* 目录不存在等：无可清 */ }
            if (removed > 0)
                Console.Error.WriteLine($"[HOTRELOAD] pruned {removed} stale auto cop{(removed == 1 ? "y" : "ies")} in {_dir}");
        }

        public void Stop()
        {
            _stop = true;
            var t = _thread;
            _thread = null;
            if (t != null && t.IsAlive) t.Join(1000);
        }

        private void PollLoop()
        {
            while (!_stop)
            {
                try
                {
                    string newest = FindNewest(out string sha, out long ticks);
                    // "写完了"的判定：文件时间戳要有一小段稳定期，避免读到编译到一半的文件
                    if (newest != null && sha != _lastSeenSha
                        && DateTime.UtcNow.Ticks - ticks > TimeSpan.TicksPerMillisecond * 250)
                    {
                        string copyTo = Path.Combine(_dir, "NativeTranspiled.auto" + sha.Substring(0, 8) + ".dll");
                        if (!File.Exists(copyTo))
                        {
                            File.Copy(newest, copyTo, overwrite: false);
                            // 伴生清单要跟着搬：守卫按"与 DLL 同名的旁挂清单"找它，换名后找不到 ⇒ 缺声明即拒
                            string srcManifest = Path.ChangeExtension(newest, ".layout.json");
                            string dstManifest = Path.ChangeExtension(copyTo, ".layout.json");
                            if (File.Exists(srcManifest) && !File.Exists(dstManifest))
                                File.Copy(srcManifest, dstManifest, overwrite: false);
                            lock (_lock)
                            {
                                _pendingPath = copyTo;
                            }
                            Console.Error.WriteLine($"[HOTRELOAD] auto-detected {Path.GetFileName(newest)} sha={sha.Substring(0, 8)} -> {Path.GetFileName(copyTo)}");
                        }
                        _lastSeenSha = sha;
                        _lastSeenTicks = ticks;
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[HOTRELOAD] watcher poll error: {ex.GetType().Name}: {ex.Message}");
                }
                Thread.Sleep(_pollMs);
            }
        }

        /// <summary>目录里最新的 `NativeTranspiled*.dll`（按最后写时间）+ 它的 sha256 与 mtime。</summary>
        private string FindNewest(out string sha, out long ticks)
        {
            sha = ""; ticks = 0;
            string best = null;
            DateTime bestTime = DateTime.MinValue;
            foreach (var f in Directory.EnumerateFiles(_dir, "NativeTranspiled*.dll"))
            {
                // 排除自动重载自己 copy 出来的那些，避免自激
                if (Path.GetFileName(f).StartsWith("NativeTranspiled.auto", StringComparison.OrdinalIgnoreCase)) continue;
                var t = File.GetLastWriteTimeUtc(f);
                if (t > bestTime) { bestTime = t; best = f; }
            }
            if (best == null) return null;
            ticks = bestTime.Ticks;
            using var fs = File.Open(best, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var h = SHA256.Create();
            sha = Convert.ToHexString(h.ComputeHash(fs));
            return best;
        }

        /// <summary>
        /// 宿主在安全点（已停派发）调用：若有待处理的变化就换模块，返回 true 并给出结果与耗时。
        /// </summary>
        public bool TryProcessPending(out NativeReloadResult result, out double elapsedMs)
        {
            string path;
            lock (_lock)
            {
                path = _pendingPath;
                _pendingPath = null;
            }
            if (path == null)
            {
                result = default;
                elapsedMs = 0;
                return false;
            }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            result = NativeJobScheduler.ReloadNativeLibrary(path);
            sw.Stop();
            elapsedMs = sw.Elapsed.TotalMilliseconds;
            lock (_lock) _processed++;
            Console.Error.WriteLine(
                $"[HOTRELOAD] auto-processed {Path.GetFileName(path)} outcome={result.Outcome} in {elapsedMs:F0} ms");
            return true;
        }

        public void Dispose() => Stop();
    }
}
