using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

[StructLayout(LayoutKind.Sequential)]
public struct ProfilerEntry
{
    public ulong JobNameHash;
    public ulong StartCycles;
    public ulong EndCycles;
    public int   ThreadIndex;
    public int   JobType;
}

internal static class StableHash
{
    private const ulong FnvOffsetBasis = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong Compute(string text)
    {
        ulong hash = FnvOffsetBasis;
        foreach (char c in text)
        {
            hash ^= c;
            hash *= FnvPrime;
        }
        return hash;
    }
}

internal static class ProfilerRecorder
{
    private readonly record struct ProfilerRecord(ulong Hash, long Start, long End, int ThreadIdx, int JobType);
    private static readonly ConcurrentQueue<ProfilerRecord> _records = new();

    public static void Record(ulong hash, long startTicks, long endTicks, int threadIdx, int jobType)
    {
        _records.Enqueue(new ProfilerRecord(hash, startTicks, endTicks, threadIdx, jobType));
    }

    public static int Drain(ProfilerEntry[] buffer, int startIndex, int maxCount)
    {
        int count = 0;
        while (count < maxCount && _records.TryDequeue(out var r))
        {
            buffer[startIndex + count] = new ProfilerEntry
            {
                JobNameHash = r.Hash,
                StartCycles = (ulong)r.Start,
                EndCycles = (ulong)r.End,
                ThreadIndex = r.ThreadIdx,
                JobType = r.JobType
            };
            count++;
        }
        return count;
    }

    public static void Clear() { while (_records.TryDequeue(out _)) { } }
}

public static class JobProfiler
{
    private const int MaxBatchRead = 65536;

    private static readonly ConcurrentDictionary<ulong, string> _jobNameMap = new();

    // 原始拉取数据
    private static ProfilerEntry[] _rawEntries = new ProfilerEntry[MaxBatchRead];
    private static int _rawCount = 0;

    // 稳定副本（仅由 PullFrameData 写入，仅 Panel 读取）
    private static ProfilerEntry[] _stableEntries = new ProfilerEntry[0];
    private static int _stableCount = 0;
    private static int _generation = 0;

    private static bool _enabled = false;

    private static readonly double TimestampToMs;
    private static readonly long TimestampFreq;

    static JobProfiler()
    {
        TimestampFreq = Stopwatch.Frequency;
        TimestampToMs = 1000.0 / TimestampFreq;
    }

    public static bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            NativeJobScheduler.Profiler_SetEnabled(value ? 1 : 0);
            if (!value)
            {
                _rawCount = 0;
                _stableCount = 0;
            }
        }
    }

    internal static void RegisterJobName(string name)
    {
        ulong hash = StableHash.Compute(name);
        _jobNameMap.TryAdd(hash, name);
    }

    internal static void RegisterJobName(ulong hash, string name)
    {
        _jobNameMap.TryAdd(hash, name);
    }

    public static string GetJobName(ulong hash)
    {
        return _jobNameMap.TryGetValue(hash, out var name) ? name : $"0x{hash:X16}";
    }

    public static string GetJobTypeName(int jobType)
    {
        return jobType switch
        {
            0 => "IJob",
            1 => "IJobFor",
            2 => "ParallelFor",
            3 => "ParallelForBatch",
            4 => "Chunk",
            _ => "Unknown"
        };
    }

    /// <summary>
    /// 当前数据代际。每次 PullFrameData 刷新到新数据时递增（仅用于面板去重）。
    /// </summary>
    public static int Generation => _generation;

    /// <summary>
    /// 从 native 和 C# 缓冲读取最新数据，刷新稳定副本。
    /// 返回 true 表示读到了新数据（_stableEntries 已更新）。
    /// </summary>
    public static bool PullFrameData()
    {
        if (!_enabled)
        {
            _rawCount = 0;
            return false;
        }

        if (_rawEntries.Length < MaxBatchRead)
            _rawEntries = new ProfilerEntry[MaxBatchRead];

        int nativeCount = 0;
        if (NativeJobScheduler.Profiler_IsEnabled() != 0)
            nativeCount = NativeJobScheduler.Profiler_ReadAll(_rawEntries, MaxBatchRead);

        int csCount = ProfilerRecorder.Drain(_rawEntries, nativeCount, MaxBatchRead - nativeCount);

        _rawCount = nativeCount + csCount;
        bool hasNewData = _rawCount > 0;

        if (hasNewData)
        {
            if (_stableEntries.Length < _rawCount)
                _stableEntries = new ProfilerEntry[_rawCount];
            Array.Copy(_rawEntries, _stableEntries, _rawCount);
            _stableCount = _rawCount;
            _generation++;
        }
        return hasNewData;
    }

    /// <summary>
    /// 当前稳定副本的条目数
    /// </summary>
    public static int CurrentFrameEntryCount => _stableCount;

    /// <summary>
    /// 读取当前稳定副本
    /// </summary>
    public static ReadOnlySpan<ProfilerEntry> GetCurrentFrameEntries()
    {
        return new ReadOnlySpan<ProfilerEntry>(_stableEntries, 0, _stableCount);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static double ComputeElapsedMs(ProfilerEntry entry)
    {
        double elapsed = (double)(entry.EndCycles - entry.StartCycles);
        return elapsed * TimestampToMs;
    }

    /// <summary>
    /// 从当前稳定副中读取聚合统计数据（供控制台输出用）。
    /// 不会消费缓冲，不破坏 Panel 的数据源。
    /// </summary>
    public static AggregatedJobInfo[] AggregateByJob()
    {
        if (_stableCount == 0) return Array.Empty<AggregatedJobInfo>();

        var dict = new Dictionary<ulong, (double totalMs, int callCount, int minThread, int maxThread)>();
        var span = GetCurrentFrameEntries();

        for (int i = 0; i < span.Length; i++)
        {
            var entry = span[i];
            double ms = ComputeElapsedMs(entry);
            if (dict.TryGetValue(entry.JobNameHash, out var existing))
            {
                dict[entry.JobNameHash] = (
                    existing.totalMs + ms,
                    existing.callCount + 1,
                    Math.Min(existing.minThread, entry.ThreadIndex),
                    Math.Max(existing.maxThread, entry.ThreadIndex)
                );
            }
            else
            {
                dict[entry.JobNameHash] = (ms, 1, entry.ThreadIndex, entry.ThreadIndex);
            }
        }

        var result = new AggregatedJobInfo[dict.Count];
        int idx = 0;
        foreach (var kv in dict)
        {
            result[idx++] = new AggregatedJobInfo
            {
                JobNameHash = kv.Key,
                JobName = GetJobName(kv.Key),
                TotalMs = kv.Value.totalMs,
                CallCount = kv.Value.callCount,
                AvgMs = kv.Value.totalMs / kv.Value.callCount,
                MinThreadIndex = kv.Value.minThread,
                MaxThreadIndex = kv.Value.maxThread
            };
        }

        Array.Sort(result, (a, b) => b.TotalMs.CompareTo(a.TotalMs));
        return result;
    }

    public static WorkerJobDetail[] AggregateJobsByThread()
    {
        if (_stableCount == 0) return Array.Empty<WorkerJobDetail>();

        var span = GetCurrentFrameEntries();

        var workerDict = new Dictionary<int, List<(ulong hash, double ms)>>();
        for (int i = 0; i < span.Length; i++)
        {
            var entry = span[i];
            double ms = ComputeElapsedMs(entry);
            if (!workerDict.TryGetValue(entry.ThreadIndex, out var list))
            {
                list = new List<(ulong, double)>();
                workerDict[entry.ThreadIndex] = list;
            }
            list.Add((entry.JobNameHash, ms));
        }

        var result = new WorkerJobDetail[workerDict.Count];
        int idx = 0;
        foreach (var kv in workerDict)
        {
            var jobList = kv.Value;
            var mergeDict = new Dictionary<ulong, (double totalMs, int count)>();
            foreach (var (hash, ms) in jobList)
            {
                if (mergeDict.TryGetValue(hash, out var existing))
                    mergeDict[hash] = (existing.totalMs + ms, existing.count + 1);
                else
                    mergeDict[hash] = (ms, 1);
            }

            var jobs = new WorkerJobEntry[mergeDict.Count];
            int j = 0;
            double workerTotalMs = 0;
            foreach (var m in mergeDict)
            {
                jobs[j] = new WorkerJobEntry
                {
                    JobName = GetJobName(m.Key),
                    JobNameHash = m.Key,
                    TotalMs = m.Value.totalMs,
                    CallCount = m.Value.count,
                    AvgMs = m.Value.totalMs / m.Value.count
                };
                workerTotalMs += m.Value.totalMs;
                j++;
            }

            Array.Sort(jobs, (a, b) => b.TotalMs.CompareTo(a.TotalMs));

            result[idx] = new WorkerJobDetail
            {
                ThreadIndex = kv.Key,
                TotalMs = workerTotalMs,
                Jobs = jobs
            };
            idx++;
        }

        Array.Sort(result, (a, b) => a.ThreadIndex.CompareTo(b.ThreadIndex));
        return result;
    }

    public static void Clear()
    {
        _rawCount = 0;
        _stableCount = 0;
        _generation = 0;
        _stableEntries = new ProfilerEntry[0];
        // 不清除 _jobNameMap！Job 名称注册后应永久保留
        // 清除后，delegate 缓存中的回调不会再重新注册名称
        // _jobNameMap.Clear();
        ProfilerRecorder.Clear();
        NativeJobScheduler.Profiler_Clear();
    }

    public struct AggregatedJobInfo
    {
        public ulong  JobNameHash;
        public string JobName;
        public double TotalMs;
        public int    CallCount;
        public double AvgMs;
        public int    MinThreadIndex;
        public int    MaxThreadIndex;
    }

    public struct WorkerJobEntry
    {
        public string JobName;
        public ulong  JobNameHash;
        public double TotalMs;
        public int    CallCount;
        public double AvgMs;
    }

    public struct WorkerJobDetail
    {
        public int            ThreadIndex;
        public double         TotalMs;
        public WorkerJobEntry[] Jobs;
        public string ThreadLabel => ThreadIndex < 0 ? "Main" : $"Worker {ThreadIndex}";
    }
}
