using System;
using System.Collections.Generic;

namespace EntJoy.JobSystem
{
    /// <summary>
    /// 隐式批（全局单例 BatchScope，C# 全权收集）。
    ///
    /// 用法（无头框架推荐，每个逻辑帧一遍）：
    ///   ImplicitBatch.SetEnabled(true);            // 一次性开启
    ///   ImplicitBatch.Add(ref job1);               // 纯 C# 收集，零 P/Invoke（入队即快照）
    ///   ImplicitBatch.AddFor(ref forJob, 1024);
    ///   ImplicitBatch.AddParallelFor(ref parJob, 8192, 0);
    ///   ImplicitBatch.EndFrame();                  // force point：一次 ScheduleBatch 提交 + 缓存句柄
    ///   ImplicitBatch.Handle(0).Complete();        // 批内句柄可用（或 CompleteAll）
    ///   ...（下一帧重复 Add → EndFrame）
    ///
    /// 语义：
    ///   - 与 BatchScope 同构（同一收集/提交路径），只是全局单例 + 帧语义命名；
    ///   - 不触发 EndFrame/CompleteAll 时 job 不提交执行（延迟刷新模型）——超
    ///     AutoFlushThreshold 自动提交（安全阀，防堆积失控）；
    ///   - Complete() 前会自动 EndFrame（NativeJobScheduler.Complete 接驳）；
    ///   - 仅 Native 后端（Managed 回退后端不支持批，Add 抛 NotSupportedException）。
    /// </summary>
    public static unsafe class ImplicitBatch
    {
        /// <summary>安全阀：pending 超此阈值仍无人 EndFrame 时自动提交（防无限挂起/堆积）。</summary>
        public const int AutoFlushThreshold = 1024;

        private static BatchScope? _scope;
        private static JobHandle[]? _handles;
        private static int _handleCount;
        private static bool _enabled;

        public static bool Enabled => _enabled;

        public static void SetEnabled(bool enabled)
        {
            if (enabled)
            {
                if (_enabled) return;
                _enabled = true;
                _scope = new BatchScope();
                _handles = null;
            }
            else
            {
                EndFrame();               // 关闭前清积压（防悬挂）
                _enabled = false;
                _scope = null;
                _handles = null;
            }
        }

        // ── 收集（纯 C#，零 P/Invoke；入队即快照） ──

        public static void Add<T>(ref T job, JobHandle dependsOn = default)
            where T : unmanaged, IJob
            => EnsureScope().Add(ref job, dependsOn);

        public static void AddFor<T>(ref T job, int length, JobHandle dependsOn = default)
            where T : unmanaged, IJobFor
            => EnsureScope().AddFor(ref job, length, dependsOn);

        public static void AddParallelFor<T>(ref T job, int length, int innerBatchCount, JobHandle dependsOn = default)
            where T : unmanaged, IJobParallelFor
            => EnsureScope().AddParallelFor(ref job, length, innerBatchCount, dependsOn);

        // ── force point ──

        /// <summary>帧末统一提交（一次 ScheduleBatch + 单次唤醒），返回本帧句柄数组。</summary>
        public static JobHandle[] EndFrame()
        {
            if (!_enabled || _scope == null)
                return Array.Empty<JobHandle>();
            _handles = _scope.Submit();
            _handleCount = _handles.Length;
            _scope = new BatchScope();   // 下一帧新批
            return _handles;
        }

        /// <summary>批内句柄（EndFrame 后有效）。</summary>
        public static JobHandle Handle(int index)
            => _handles != null && (uint)index < (uint)_handleCount ? _handles[index] : default;

        public static int HandleCount => _handleCount;

        public static void CompleteAll()
        {
            if (_handles == null) return;
            for (int i = 0; i < _handleCount; i++)
            {
                if (_handles[i]._nativeHandle.IsValid)
                    _handles[i].Complete();
            }
        }

        // ── 内部：Complete 自动 flush + 安全阀 ──

        /// <summary>Complete/IsCompleted 前调用：开启时把当前批提交（语义=Unity Complete 隐式刷新）。</summary>
        internal static void FlushForComplete()
        {
            if (_enabled && _scope != null && _scope.Count > 0)
                EndFrame();
        }

        internal static void NotifyAdded()
        {
            if (_enabled && _scope != null && _scope.Count >= AutoFlushThreshold)
                EndFrame();   // 安全阀：自动提交，防无限堆积
        }

        private static BatchScope EnsureScope()
        {
            if (!_enabled)
                throw new InvalidOperationException("ImplicitBatch is not enabled — call SetEnabled(true) first.");
            if (_scope == null)
                _scope = new BatchScope();
            else
                NotifyAdded();
            return _scope;
        }
    }
}