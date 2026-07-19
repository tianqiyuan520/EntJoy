using Microsoft.Build.Framework;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace NativeTranspiler.Tasks
{
    public class NativeCompileTask : Microsoft.Build.Utilities.Task
    {
        [Required]
        public string NativeCodeGenDir { get; set; }

        public ITaskItem[] ExtraDependencies { get; set; }

        public override bool Execute()
        {
            var normalizedDir = Path.GetFullPath(NativeCodeGenDir)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var isWindows = Path.DirectorySeparatorChar == '\\';
            var lockIdentity = isWindows ? normalizedDir.ToUpperInvariant() : normalizedDir;
            var mutexName = (isWindows ? @"Local\" : "") +
                "EntJoy.NativeCompile." + ComputeTextHash(lockIdentity);

            using (var compileMutex = new System.Threading.Mutex(false, mutexName))
            {
                var lockTaken = false;
                try
                {
                    Log.LogMessage(MessageImportance.Low, $"Waiting for native compilation lock: {normalizedDir}");
                    try
                    {
                        lockTaken = compileMutex.WaitOne(TimeSpan.FromMinutes(15));
                    }
                    catch (System.Threading.AbandonedMutexException)
                    {
                        // The previous owner exited during compilation. The lock is
                        // ours and the normal cache/build-system checks repair state.
                        lockTaken = true;
                    }

                    if (!lockTaken)
                    {
                        Log.LogError($"Timed out waiting for another native compilation of '{normalizedDir}'.");
                        return false;
                    }

                    return ExecuteLocked();
                }
                finally
                {
                    if (lockTaken)
                        compileMutex.ReleaseMutex();
                }
            }
        }

        private bool ExecuteLocked()
        {
            Log.LogMessage(MessageImportance.High, $"Checking native code directory: {NativeCodeGenDir}");

            if (!Directory.Exists(NativeCodeGenDir))
            {
                Log.LogWarning($"Directory '{NativeCodeGenDir}' does not exist. Creating it...");
                Directory.CreateDirectory(NativeCodeGenDir);
            }

            var cmakeListsPath = Path.Combine(NativeCodeGenDir, "CMakeLists.txt");
            if (!File.Exists(cmakeListsPath))
            {
                Log.LogError($"CMakeLists.txt not found at {cmakeListsPath}. Skipping native compilation.");
                return false;
            }

            var cppFiles = Directory.GetFiles(NativeCodeGenDir, "*.cpp");
            if (cppFiles.Length == 0)
            {
                Log.LogMessage(MessageImportance.High, "No .cpp files found. Skipping native compilation.");
                return true;
            }

            if (!IsCMakeAvailable())
            {
                Log.LogWarning("CMake not found in PATH. Skipping native compilation.");
                return true;
            }

            // ---- 基于内容哈希的增量检测 ----
            var dependencies = CollectDependencies(NativeCodeGenDir);
            // 将 hash 文件存在项目根目录下，避免被 CMake 清理 build 目录时删除
            var hashFile = Path.Combine(NativeCodeGenDir, "native_compile.hash");
            var buildDir = Path.Combine(NativeCodeGenDir, "build");
            var expectedNativeDll = Path.Combine(buildDir, "Release", "NativeDll.dll");
            if (IsUpToDateByHash(dependencies, hashFile) && File.Exists(expectedNativeDll))
            {
                Log.LogMessage(MessageImportance.High, "Native code is up-to-date (content hashes unchanged). Skipping CMake build.");
                return true;
            }
            if (!File.Exists(expectedNativeDll))
            {
                Log.LogMessage(MessageImportance.High, "Native output missing. CMake build required.");
            }

            // 分离 CMakeLists.txt 和其他依赖的检测：
            // 如果只有 .cpp/.h/.ispc 改了，但 CMakeLists.txt 没变且 CMakeCache.txt 存在，则跳过 configure
            var cmakeListsHash = ComputeFileHash(cmakeListsPath);
            var savedCmakeListsHash = GetSavedFileHash(hashFile, cmakeListsPath);
            bool cmakeListsUnchanged = (savedCmakeListsHash != null && savedCmakeListsHash == cmakeListsHash);

            bool cmakeCacheExists = File.Exists(Path.Combine(buildDir, "CMakeCache.txt"));
            bool cmakeBuildSystemExists = HasGeneratedBuildSystem(buildDir);

            if (cmakeCacheExists && cmakeBuildSystemExists && cmakeListsUnchanged)
            {
                Log.LogMessage(MessageImportance.High, "CMakeLists.txt unchanged, cache valid. Skipping configure, running build only.");
            }
            else
            {
                if (cmakeCacheExists && !cmakeBuildSystemExists)
                    Log.LogMessage(MessageImportance.High, "CMake cache exists but generated build files are missing. Reconfiguring.");

                // 清理旧的 build 目录（无论 cache 是否存在），避免路径缓存冲突
                if (Directory.Exists(buildDir))
                {
                    Log.LogMessage(MessageImportance.High, "Cleaning build directory for fresh configure...");
                    Directory.Delete(buildDir, true);
                }
                Directory.CreateDirectory(buildDir);

                // ---- CMake 配置 ----
                var configureArgs = new List<string>
                {
                    "-S", NativeCodeGenDir,
                    "-B", buildDir
                };
                Log.LogMessage(MessageImportance.High, $"Running CMake configure: cmake {string.Join(" ", configureArgs)}");
                var configureResult = RunProcessWithTimeout("cmake", configureArgs.ToArray(), NativeCodeGenDir, 120000);
                if (configureResult.ExitCode != 0)
                {
                    Log.LogError($"CMake configuration failed.\nOutput: {configureResult.Output}\nError: {configureResult.Error}");
                    return false;
                }
            }

            // ---- CMake 构建 ----
            var buildArgs = new string[] { "--build", buildDir, "--config", "Release", "--parallel" };
            Log.LogMessage(MessageImportance.High, $"Running CMake build: cmake {string.Join(" ", buildArgs)}");
            var buildResult = RunProcessWithTimeout("cmake", buildArgs, NativeCodeGenDir, 600000);
            if (buildResult.ExitCode != 0)
            {
                // A cancelled/overlapping IDE build can leave CMakeCache.txt behind
                // while deleting Visual Studio's generated project files. Repair the
                // generated build system once before reporting a hard failure.
                Log.LogWarning($"CMake build failed. Reconfiguring once before retry.\nOutput: {buildResult.Output}\nError: {buildResult.Error}");
                var repairArgs = new[] { "-S", NativeCodeGenDir, "-B", buildDir };
                var repairResult = RunProcessWithTimeout("cmake", repairArgs, NativeCodeGenDir, 120000);
                if (repairResult.ExitCode != 0)
                {
                    Log.LogError($"CMake repair configuration failed.\nOutput: {repairResult.Output}\nError: {repairResult.Error}");
                    return false;
                }

                buildResult = RunProcessWithTimeout("cmake", buildArgs, NativeCodeGenDir, 600000);
                if (buildResult.ExitCode != 0)
                {
                    Log.LogError($"CMake build failed after repair.\nOutput: {buildResult.Output}\nError: {buildResult.Error}");
                    return false;
                }
            }

            SaveHashManifest(dependencies, hashFile);
            Log.LogMessage(MessageImportance.High, "Native compilation succeeded.");
            return true;
        }

        private static string ComputeTextHash(string value)
        {
            using (var md5 = MD5.Create())
            {
                var bytes = md5.ComputeHash(Encoding.UTF8.GetBytes(value));
                var result = new StringBuilder(bytes.Length * 2);
                foreach (var b in bytes)
                    result.Append(b.ToString("x2"));
                return result.ToString();
            }
        }

        private static bool HasGeneratedBuildSystem(string buildDir)
        {
            if (!Directory.Exists(buildDir))
                return false;

            // Cover the common single- and multi-config CMake generators. A cache
            // alone is insufficient: interrupted configuration may leave it behind.
            if (File.Exists(Path.Combine(buildDir, "build.ninja")) ||
                File.Exists(Path.Combine(buildDir, "Makefile")))
                return true;

            var visualStudioSolutions = Directory.GetFiles(buildDir, "*.sln", SearchOption.TopDirectoryOnly);
            if (visualStudioSolutions.Length > 0)
                return File.Exists(Path.Combine(buildDir, "ALL_BUILD.vcxproj"));

            return false;
        }

        /// <summary>从哈希清单中读取指定文件的已保存哈希</summary>
        private static string GetSavedFileHash(string hashFile, string filePath)
        {
            if (!File.Exists(hashFile))
                return null;

            foreach (var line in File.ReadAllLines(hashFile))
            {
                var parts = line.Split(new char[] { ' ' }, 2);
                if (parts.Length == 2 && string.Equals(parts[1], filePath, StringComparison.OrdinalIgnoreCase))
                    return parts[0];
            }
            return null;
        }

        /// <summary>收集所有依赖文件的完整路径列表</summary>
        private List<string> CollectDependencies(string rootDir)
        {
            var files = new List<string>
            {
                Path.Combine(rootDir, "CMakeLists.txt")
            };
            foreach (var pattern in new[] { "*.cpp", "*.h", "*.ispc" })
            {
                foreach (var f in Directory.GetFiles(rootDir, pattern))
                    files.Add(f);
            }

            // NativeCodeGenDir is <project>/NativeTranspiler_Generated. Resolve
            // the shared runtime explicitly; Directory.GetParent behaves
            // differently when the input retains a trailing separator.
            var nativeDllDir = Path.GetFullPath(Path.Combine(rootDir, "..", "..", "NativeDll"));
            if (Directory.Exists(nativeDllDir))
            {
                Log.LogMessage(MessageImportance.Low, $"  Including shared native sources: {nativeDllDir}");
                foreach (var pattern in new[] { "*.h", "*.cpp", "*.hpp" })
                {
                    foreach (var f in Directory.GetFiles(nativeDllDir, pattern))
                        files.Add(f);
                }
            }

            if (ExtraDependencies != null)
            {
                foreach (var item in ExtraDependencies)
                    files.Add(item.ItemSpec);
            }

            return files.Distinct().OrderBy(f => f).ToList();
        }

        /// <summary>计算单个文件的 MD5 哈希（小写十六进制）</summary>
        private static string ComputeFileHash(string filePath)
        {
            try
            {
                using (var md5 = MD5.Create())
                using (var stream = File.OpenRead(filePath))
                {
                    var hashBytes = md5.ComputeHash(stream);
                    var sb = new StringBuilder(hashBytes.Length * 2);
                    foreach (var b in hashBytes)
                        sb.Append(b.ToString("x2"));
                    return sb.ToString();
                }
            }
            catch
            {
                // 文件不可读/不存在时返回空，触发重新编译
                return "";
            }
        }

        /// <summary>基于内容哈希判断是否最新</summary>
        private bool IsUpToDateByHash(List<string> dependencies, string hashFile)
        {
            if (!File.Exists(hashFile))
            {
                Log.LogMessage(MessageImportance.High, "Hash manifest not found. Full rebuild required.");
                return false;
            }

            // 读取上一次保存的哈希清单
            var existingHashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in File.ReadAllLines(hashFile))
            {
                var parts = line.Split(new char[] { ' ' }, 2);
                if (parts.Length == 2)
                    existingHashes[parts[1]] = parts[0];
            }

            Log.LogMessage(MessageImportance.Low, $"  Checking {dependencies.Count} files against {existingHashes.Count} saved hashes...");

            // 检查每一个依赖文件的当前哈希是否与记录一致
            foreach (var dep in dependencies)
            {
                var currentHash = ComputeFileHash(dep);
                if (!existingHashes.TryGetValue(dep, out var savedHash) || savedHash != currentHash)
                {
                    string reason = !existingHashes.ContainsKey(dep)
                        ? "NEW FILE (not in saved hash)"
                        : "CONTENT CHANGED";
                    Log.LogMessage(MessageImportance.High, $"  ★ {reason}: {Path.GetFileName(dep)}");
                    return false;
                }
            }

            // 检查是否有文件被删除了（哈希清单里有多余的记录）
            foreach (var savedFile in existingHashes.Keys)
            {
                if (!dependencies.Any(d => string.Equals(d, savedFile, StringComparison.OrdinalIgnoreCase)))
                {
                    Log.LogMessage(MessageImportance.High, $"  ★ FILE DELETED: {savedFile}");
                    return false;
                }
            }

            return true;
        }

        /// <summary>保存当前所有依赖文件的内容哈希清单</summary>
        private void SaveHashManifest(List<string> dependencies, string hashFile)
        {
            var dir = Path.GetDirectoryName(hashFile);
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var sb = new StringBuilder();
            foreach (var dep in dependencies)
            {
                var hash = ComputeFileHash(dep);
                sb.AppendLine($"{hash} {dep}");
            }
            File.WriteAllText(hashFile, sb.ToString());
        }

        private bool IsCMakeAvailable()
        {
            try
            {
                var result = RunProcessWithTimeout("cmake", new[] { "--version" }, Environment.CurrentDirectory, 5000);
                return result.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        private (int ExitCode, string Output, string Error) RunProcessWithTimeout(
            string fileName, string[] arguments, string workingDir, int timeoutMilliseconds)
        {
            var argsString = string.Join(" ", arguments.Select(a => a.Contains(' ') ? $"\"{a}\"" : a));

            var startInfo = new ProcessStartInfo(fileName, argsString)
            {
                WorkingDirectory = workingDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            using (var process = new Process { StartInfo = startInfo })
            {
                var output = new System.Text.StringBuilder();
                var error = new System.Text.StringBuilder();

                using (var outputWaitHandle = new System.Threading.AutoResetEvent(false))
                using (var errorWaitHandle = new System.Threading.AutoResetEvent(false))
                {
                    process.OutputDataReceived += (sender, e) =>
                    {
                        if (e.Data == null) outputWaitHandle.Set();
                        else output.AppendLine(e.Data);
                    };
                    process.ErrorDataReceived += (sender, e) =>
                    {
                        if (e.Data == null) errorWaitHandle.Set();
                        else error.AppendLine(e.Data);
                    };

                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    if (process.WaitForExit(timeoutMilliseconds))
                    {
                        outputWaitHandle.WaitOne(5000);
                        errorWaitHandle.WaitOne(5000);
                    }
                    else
                    {
                        process.Kill();
                        output.Append("ERROR: Process timed out and was killed.");
                        error.Append("ERROR: Process timed out and was killed.");
                    }

                    return (process.ExitCode, output.ToString(), error.ToString());
                }
            }
        }
    }
}
