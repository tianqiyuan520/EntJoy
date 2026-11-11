using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace EntJoy.ECS
{
    /// <summary>进程内固定的列布局参数；硬件信息不可用时采用 64 字节对齐、4096 字节检测周期。</summary>
    internal readonly struct ChunkCacheLayout
    {
        internal const int DefaultLineSize = 64;
        internal const int DefaultConflictPeriod = 4096;
        // slab 按 64 KiB 对齐，支持的缓存行必须落在 16..65536 且为 2 的幂。
        private const int MinLineSize = 16;
        private const int MaxLineSize = 65536;
        internal static readonly ChunkCacheLayout Current = Detect();

        internal readonly int LineSize;
        internal readonly int ConflictPeriod;

        internal ChunkCacheLayout(int lineSize, int conflictPeriod)
        {
            LineSize = lineSize;
            ConflictPeriod = conflictPeriod;
        }

        private static ChunkCacheLayout Defaults => new(DefaultLineSize, DefaultConflictPeriod);

        private static ChunkCacheLayout Detect()
        {
            if (!OperatingSystem.IsWindows()) return Defaults;
            try
            {
                uint bytes = 0;
                GetLogicalProcessorInformationEx(2, null, ref bytes); // RelationCache
                if (bytes < 20 || bytes > int.MaxValue) return Defaults;
                var buffer = new byte[bytes];
                if (!GetLogicalProcessorInformationEx(2, buffer, ref bytes) || bytes > buffer.Length)
                    return Defaults;
                return FromWindowsCacheRecords(buffer.AsSpan(0, (int)bytes));
            }
            catch (DllNotFoundException) { return Defaults; }
            catch (EntryPointNotFoundException) { return Defaults; }
        }

        internal static ChunkCacheLayout FromWindowsCacheRecords(ReadOnlySpan<byte> records)
        {
            int lineSize = 0, period = 0;
            bool uniformPeriod = true;
            while (!records.IsEmpty)
            {
                if (records.Length < 8) return Defaults;
                int relationship = BinaryPrimitives.ReadInt32LittleEndian(records);
                int size = BinaryPrimitives.ReadInt32LittleEndian(records.Slice(4));
                if (size < 8 || size > records.Length) return Defaults;
                var record = records.Slice(0, size);
                records = records.Slice(size);
                if (relationship != 2) continue;

                var parsed = ReadCacheLevel(record, ref period, ref uniformPeriod);
                if (parsed.Failed) return Defaults;
                lineSize = Math.Max(lineSize, parsed.LineSize);
            }
            if (lineSize == 0) lineSize = DefaultLineSize;
            // 核心间周期不一致或信息不足时，周期回退 4096；有效缓存行取最大值。
            if (!uniformPeriod || period == 0) period = DefaultConflictPeriod;
            if (period <= lineSize || period % lineSize != 0) return Defaults;
            return new ChunkCacheLayout(lineSize, period);
        }

        // 解析一条 L1 缓存描述：只接受统一的、按 64 KiB slab 对齐的 2 的幂缓存行。
        // 任一字段不自洽都标记为「周期不统一」，由调用方回退到默认周期。
        private static (int LineSize, bool Failed) ReadCacheLevel(ReadOnlySpan<byte> record, ref int period, ref bool uniformPeriod)
        {
            if (record.Length < 20) return (0, true);
            int type = BinaryPrimitives.ReadInt32LittleEndian(record.Slice(16));
            if (record[8] != 1 || (type != 0 && type != 1)) return (0, false);   // 只看 L1 unified/data

            int line = BinaryPrimitives.ReadUInt16LittleEndian(record.Slice(10));
            int ways = record[9];
            int cacheBytes = BinaryPrimitives.ReadInt32LittleEndian(record.Slice(12));
            // 常见组相联缓存的组索引重复周期 = 容量 / 相联度，独立于操作系统页大小。
            int candidate = ways == 0 || ways == 255 || cacheBytes <= 0 || cacheBytes % ways != 0
                ? 0 : cacheBytes / ways;

            if (line < MinLineSize || line > MaxLineSize || (line & (line - 1)) != 0
                || candidate <= line || (candidate != 0 && candidate % line != 0))
            {
                uniformPeriod = false;
                return (line, false);
            }
            if (period == 0) period = candidate;
            else if (period != candidate) uniformPeriod = false;
            return (line, false);
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetLogicalProcessorInformationEx(
            int relationship, [Out] byte[] buffer, ref uint returnedLength);
    }
}
