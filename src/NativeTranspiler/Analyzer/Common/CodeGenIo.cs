using System;
using System.IO;
using System.Threading;

namespace NativeTranspiler.Analyzer.Common
{
    /// <summary>
    /// NativeTranspiler 的文件 I/O 与路径工具（从 NativeTranspilerGenerator 拆出）。
    /// 负责带重试/增量比较的写出、删除、仓库根探测与相对路径计算，与代码生成编排解耦。
    /// </summary>
    internal static class CodeGenIo
    {
        /// <summary>
        /// 带重试与内容级增量比较的文本写出。
        /// 仅当内容变化时才写文件，避免因时间戳更新触发不必要的原生重编译。
        /// </summary>
        public static void WriteAllTextWithRetry(string path, string contents, int maxRetries = 5)
        {
            // 内容级增量写入：只有内容变化时才写文件，避免因时间戳更新触发编译
            if (File.Exists(path))
            {
                try
                {
                    string existing = File.ReadAllText(path);
                    if (existing == contents)
                        return;
                }
                catch { }
            }
            else
            {
                var dir = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
            }

            int retryCount = 0;
            while (true)
            {
                try
                {
                    File.WriteAllText(path, contents);
                    break;
                }
                catch (IOException ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { throw; }
                catch (IOException) when (retryCount < maxRetries) { retryCount++; Thread.Sleep(50 * retryCount); }
                catch (UnauthorizedAccessException) when (retryCount < maxRetries) { retryCount++; Thread.Sleep(50 * retryCount); }
            }
        }

        /// <summary>文件存在则删除，忽略一切异常。</summary>
        public static void DeleteIfExists(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch { }
        }

        /// <summary>
        /// 从 startDir 向上逐级探测，返回包含 <c>src/NativeDll/Exports.cpp</c> 的仓库根目录。
        /// 不依赖项目目录深度，工程无论放多深都能自动指向。找不到返回 null。
        /// </summary>
        public static string? FindRepoRoot(string startDir)
        {
            var dir = new DirectoryInfo(startDir);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "src", "NativeDll", "Exports.cpp")))
                    return dir.FullName;
                dir = dir.Parent;
            }
            return null;
        }

        /// <summary>计算相对路径（兼容 netstandard2.0，不支持 Path.GetRelativePath）</summary>
        public static string GetRelativePath(string basePath, string targetPath)
        {
            if (!basePath.EndsWith(Path.DirectorySeparatorChar.ToString()))
                basePath += Path.DirectorySeparatorChar;

            var baseUri = new Uri(basePath);
            var targetUri = new Uri(targetPath);
            var relativeUri = baseUri.MakeRelativeUri(targetUri);
            var relativePath = Uri.UnescapeDataString(relativeUri.ToString());
            return relativePath.Replace('/', Path.DirectorySeparatorChar);
        }
    }
}
