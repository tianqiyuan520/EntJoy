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

            // ---- 基于内容哈希的增量检测（取代原来的时间戳比对） ----
            var dependencies = CollectDependencies(NativeCodeGenDir);
            var hashFile = Path.Combine(NativeCodeGenDir, "build", "native_compile.hash");
            if (IsUpToDateByHash(dependencies, hashFile))
            {
                Log.LogMessage(MessageImportance.High, "Native code is up-to-date (content hashes unchanged). Skipping CMake build.");
                return true;
            }

            var buildDir = Path.Combine(NativeCodeGenDir, "build");
            if (Directory.Exists(buildDir))
            {
                var cacheFile = Path.Combine(buildDir, "CMakeCache.txt");
                if (!File.Exists(cacheFile))
                {
                    Log.LogMessage(MessageImportance.High, "CMake cache missing, cleaning build directory...");
                    Directory.Delete(buildDir, true);
                    Directory.CreateDirectory(buildDir);
                }
            }
            else
            {
                Directory.CreateDirectory(buildDir);
            }

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

            // ---- CMake 构建 ----
            var buildArgs = new[] { "--build", buildDir, "--config", "Release" };
            Log.LogMessage(MessageImportance.High, $"Running CMake build: cmake {string.Join(" ", buildArgs)}");
            var buildResult = RunProcessWithTimeout("cmake", buildArgs, NativeCodeGenDir, 600000);
            if (buildResult.ExitCode != 0)
            {
                Log.LogError($"CMake build failed.\nOutput: {buildResult.Output}\nError: {buildResult.Error}");
                return false;
            }

            SaveHashManifest(dependencies, hashFile);
            Log.LogMessage(MessageImportance.High, "Native compilation succeeded.");
            return true;
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

            var nativeDllDir = Path.Combine(Directory.GetParent(rootDir)?.FullName ?? "", "..", "NativeDll");
            if (Directory.Exists(nativeDllDir))
            {
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
