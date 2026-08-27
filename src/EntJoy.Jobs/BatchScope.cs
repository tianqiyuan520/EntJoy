using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace EntJoy.JobSystem
{
    /// <summary>
    /// 显式批提交（对应 Unity JobHandle.ScheduleBatchedJobs 语义）。
    ///
    /// 用法：
    ///   using var batch = new BatchScope();
    ///   batch.Add(ref job1);                       // 入队即快照（blittable 池拷贝）
    ///   batch.Add(ref job2, dependsOn: h0);        // 依赖"已发布"句柄（批外或上批）
    ///   batch.AddFor(ref forJob, 1024);
    ///   batch.AddParallelFor(ref parJob, 8192, 0);
    ///   var handles = batch.Submit();              // 发布：提交整批（C# v1：逐个走既有入口）
    ///   handles[i].Complete();                     // 或 batch.CompleteAll();
    ///
    /// 语义（docs 20260826-JobSystem-多Job调度开销基准与分析.md §14）：
    ///   - 批 = blittable-only：泛型约束 `unmanaged` 在编译期拒绝含托管引用的 job
    ///     （CS8377），另有运行时 IsReferenceOrContainsReferences 兜底；
    ///   - Job 字段 = 入队快照；引用内存（NativeArray/指针）= 活引用，批生命周期内主线程只读
    ///     （§14.5 的容器持有检查随批提交实现后生效）；
    ///   - End/Complete/Flush 为发布 force point。
    ///   - v1：C# 层批（入队零 P/Invoke，End 逐个走既有 ScheduleRaw 入口——API 形态先行，
    ///     P/Invoke 合并 + native 单次唤醒在隐式批阶段随 deferNotify 一起做）。
    /// </summary>
    public sealed unsafe class BatchScope : IDisposable
    {
        // 批内描述符 = native JobBatchDesc 布局（可直接 fixed 传单次 P/Invoke）
        private NativeJobCore.NativeJobBatchDesc[] _items = null!;
        private List<JobHandle>? _managedHandles;  // Managed 后端：Add 即调度存句柄（无延迟/无 desc）
        private IntPtr[]? _handleOut;   // Submit 的句柄回写缓冲区
        private List<NativeJobCore.RetainedNativeDependency>? _depLeases;
        private JobHandle[]? _handles;   // Submit 结果缓存（CompleteAll 复用）
        private int _count;
        private bool _ended;
        private bool _disposed;

        // Managed 后端（NativeDll 缺失的回退）：Add 立即泛型调度（值传递即快照），句柄留存。
        private bool IsManaged => !JobScheduler.IsNative;

        public BatchScope()
        {
            _items = new NativeJobCore.NativeJobBatchDesc[DefaultCapacity];
        }

        private const int DefaultCapacity = 64;

        public int Count => IsManaged ? (_managedHandles?.Count ?? 0) : _count;

        // ── 入队：IJob / IJobFor / IJobParallelFor（泛型约束 unmanaged = 编译期拒绝托管 job） ──
        // 注意：Debug 构建下 EntJoy.Collections 容器保持 blittable（DisposeSentinel 用静态表 + int 句柄，
        // 不嵌入 struct 字段），故含容器的 unmanaged job 在 Debug 下同样满足约束。

        public void Add<T>(ref T job, JobHandle dependsOn = default)
            where T : unmanaged, IJob
        {
            if (IsManaged)
            {
                _managedHandles ??= new List<JobHandle>();
                EnsureWritable();
                _managedHandles.Add(JobScheduler.Schedule(ref job, dependsOn));
                return;
            }
            ThrowIfManaged<T>();
            var cache = NativeJobCore.JobDelegateCacheFor<T>.Cache;
            AddDesc(0, cache.FuncPtr, NativeJobCore.AllocContext(ref job), NativeJobCore.CleanupPtr, dependsOn);
        }

        public void AddFor<T>(ref T job, int length, JobHandle dependsOn = default)
            where T : unmanaged, IJobFor
        {
            if (IsManaged)
            {
                _managedHandles ??= new List<JobHandle>();
                EnsureWritable();
                _managedHandles.Add(JobScheduler.ScheduleFor(ref job, length, dependsOn));
                return;
            }
            ThrowIfManaged<T>();
            var cache = NativeJobCore.ForDelegateCacheFor<T>.Cache;
            AddDesc(1, cache.FuncPtr, NativeJobCore.AllocContext(ref job), NativeJobCore.CleanupPtr,
                dependsOn, length: length);
        }

        public void AddParallelFor<T>(ref T job, int length, int innerBatchCount, JobHandle dependsOn = default)
            where T : unmanaged, IJobParallelFor
        {
            if (IsManaged)
            {
                _managedHandles ??= new List<JobHandle>();
                EnsureWritable();
                _managedHandles.Add(JobScheduler.ScheduleParallelFor(ref job, length, innerBatchCount, dependsOn));
                return;
            }
            ThrowIfManaged<T>();
            var cache = NativeJobCore.GetAutoParallelForCache<T>();
            AddDesc(2, cache.FuncPtr, NativeJobCore.AllocContext(ref job), NativeJobCore.CleanupPtr,
                dependsOn, length: length, batchSize: innerBatchCount);
        }

        // 运行时兜底：unmanaged 约束已在编译期拒绝托管 job，此处防御绕过约束的路径（如反射/动态调用）
        private static void ThrowIfManaged<T>() where T : struct
        {
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
                throw new NotSupportedException(
                    $"{typeof(T).Name} 含托管引用字段，批快路径仅支持 unmanaged job；请使用单 job Schedule。");
        }

        private void AddDesc(byte kind, IntPtr func, IntPtr ctx, IntPtr cleanup,
            JobHandle dependsOn, int length = 0, int batchSize = 0)
        {
            EnsureWritable();
            if (_count == _items.Length)
                Array.Resize(ref _items, _items.Length * 2);

            IntPtr depPtr = IntPtr.Zero;
            if (dependsOn._nativeHandle.IsValid)
            {
                var lease = new NativeJobCore.RetainedNativeDependency(dependsOn._nativeHandle);
                _depLeases ??= new List<NativeJobCore.RetainedNativeDependency>();
                _depLeases.Add(lease);
                depPtr = lease.Handle;
            }

            _items[_count] = new NativeJobCore.NativeJobBatchDesc
            {
                Kind = kind,
                Func = func,
                Context = ctx,
                Cleanup = cleanup,
                Dependency = depPtr,
                Length = length,
                BatchSize = batchSize
            };
            _count++;
        }

        // ── 发布 / 完成 ──

        /// <summary>发布整批：单次 P/Invoke 提交全部 job（native 侧 defer 窗口 + 统一唤醒）。</summary>
        public JobHandle[] Submit()
        {
            if (_ended)
            {
                // 幂等：已提交则直接返回缓存句柄，不重复提交（误用只产生一次副作用）。
                if (_disposed) throw new ObjectDisposedException(nameof(BatchScope));
                return _handles ?? Array.Empty<JobHandle>();
            }
            if (IsManaged)
            {
                // Managed 后端：Add 已立即调度（无延迟发布），Submit 仅收集返回句柄。
                _ended = true;
                _handles = _managedHandles?.ToArray() ?? Array.Empty<JobHandle>();
                _count = _handles.Length;
                return _handles;
            }
            // Batch = Native-only 批快照/合并：NativeDll 缺失时不应到达（IsManaged 已分流）。
            if (NativeJobCore.NativeDllHandle == IntPtr.Zero)
                throw new NotSupportedException(
                    "BatchScope requires the Native Job System (NativeDll.dll); " +
                    "the managed fallback backend does not support batching — use single job Schedule() instead.");
            _ended = true;
            var handles = new JobHandle[_count];
            if (_count == 0)
            {
                _handleOut = Array.Empty<IntPtr>();
                _handles = handles;
                return handles;
            }
            _handleOut = new IntPtr[_count];
            try
            {
                fixed (NativeJobCore.NativeJobBatchDesc* descPtr = _items)
                fixed (IntPtr* outPtr = _handleOut)
                {
                    NativeJobCore.JobSystem_ScheduleBatch(descPtr, _count, outPtr);
                }
                for (int i = 0; i < _count; i++)
                {
                    if (_handleOut[i] != IntPtr.Zero)
                        handles[i] = new JobHandle(new NativeJobHandle(_handleOut[i]));
                }
            }
            finally
            {
                // native Schedule 内部已对依赖 Acquire；此处释放 C# 侧 retain（防依赖句柄提前释放）
                if (_depLeases != null)
                {
                    foreach (var lease in _depLeases) lease.Dispose();
                    _depLeases.Clear();
                }
            }
            _handles = handles;
            return handles;
        }

        /// <summary>等待整批完成；未提交（未 Submit）时先提交。</summary>
        public void CompleteAll()
        {
            JobHandle[] handles = _ended ? GetSubmittedHandles() : Submit();
            foreach (var h in handles)
            {
                if (h._nativeHandle.IsValid)
                    h.Complete();
            }
        }

        private JobHandle[] GetSubmittedHandles()
        {
            return _handles ?? Array.Empty<JobHandle>();
        }

        private void EnsureWritable()
        {
            if (_ended) throw new InvalidOperationException("BatchScope already ended.");
            if (_disposed) throw new ObjectDisposedException(nameof(BatchScope));
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _items = Array.Empty<NativeJobCore.NativeJobBatchDesc>();
            _depLeases?.Clear();
            _handles = null;
            _managedHandles = null;
        }
    }
}