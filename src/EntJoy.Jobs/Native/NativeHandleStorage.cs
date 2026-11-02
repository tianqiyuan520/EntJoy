using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace EntJoy.JobSystem
{
    // 槽位页保持地址稳定；代次校验防止旧副本访问复用后的所有权。
    // Gate 保护所有权转换，不包住任务执行或等待。原生合并只登记依赖，不执行用户作业。
    internal static unsafe class NativeHandleStorage
    {
        private struct Slot
        {
            public long Generation;
            public IntPtr Handle;
            public Slot* Next;
            public DependencyBlock* Dependencies;
        }
        private struct Page { public Page* Next; }
        internal struct DependencyBlock
        {
            public DependencyBlock* Next;
            public int Count;
            public int Capacity;
        }
        private const int SlotsPerPage = 256;
        private const int PageHeaderSize = sizeof(long); // align generations on both 32-bit and 64-bit
        private const int CompletedFlagOffset = 4;
        private static int _gate;
        private static bool _accepting = true;
        private static Slot* _free;
        private static Page* _pages;

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static void Enter()
        {
            var spin = new SpinWait();
            while (Interlocked.CompareExchange(ref _gate, 1, 0) != 0) spin.SpinOnce();
        }
        private static void Exit() => Volatile.Write(ref _gate, 0);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static NativeJobHandle CreateLocked(IntPtr handle, DependencyBlock* dependencies)
        {
            if (_free == null)
            {
                var page = (Page*)Marshal.AllocHGlobal(PageHeaderSize + SlotsPerPage * sizeof(Slot));
                page->Next = _pages;
                _pages = page;
                var slots = (Slot*)((byte*)page + PageHeaderSize);
                for (int i = 0; i < SlotsPerPage; i++)
                {
                    slots[i] = default;
                    slots[i].Next = _free;
                    _free = slots + i;
                }
            }
            Slot* slot = _free;
            _free = slot->Next;
            Volatile.Write(ref slot->Generation, slot->Generation + 1);
            slot->Dependencies = dependencies;
            Volatile.Write(ref slot->Handle, handle);
            return new NativeJobHandle((IntPtr)slot, slot->Generation);
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        internal static NativeJobHandle Create(IntPtr handle, DependencyBlock* dependencies = null)
        {
            try
            {
                Enter();
                try { if (_accepting) return CreateLocked(handle, dependencies); }
                finally { Exit(); }
            }
            catch
            {
                NativeJobCore.JobSystem_ReleaseHandle(handle);
                if (dependencies != null) ReturnDependencies(dependencies);
                throw;
            }
            NativeJobCore.JobSystem_ReleaseHandle(handle);
            if (dependencies != null) ReturnDependencies(dependencies);
            return default;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static IntPtr GetHandle(NativeJobHandle token)
        {
            var slot = (Slot*)token.Slot;
            if (slot == null || Volatile.Read(ref slot->Generation) != token.Generation) return IntPtr.Zero;
            IntPtr handle = Volatile.Read(ref slot->Handle);
            return Volatile.Read(ref slot->Generation) == token.Generation ? handle : IntPtr.Zero;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        internal static IntPtr Retain(NativeJobHandle token)
        {
            if (token.Slot == IntPtr.Zero) return IntPtr.Zero;
            Enter();
            try
            {
                IntPtr handle = GetHandle(token);
                if (handle != IntPtr.Zero) NativeJobCore.JobSystem_RetainHandle(handle);
                return handle;
            }
            finally { Exit(); }
        }

        internal static bool IsCompleted(NativeJobHandle token)
        {
            IntPtr handle = Retain(token);
            if (handle == IntPtr.Zero) return false;
            try { return Volatile.Read(ref *((byte*)handle + CompletedFlagOffset)) != 0; }
            finally { NativeJobCore.JobSystem_ReleaseHandle(handle); }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static DependencyBlock* RentDependencies(int capacity)
        {
            int bytes = checked(sizeof(DependencyBlock) + capacity * (sizeof(NativeJobHandle) + sizeof(IntPtr)));
            var block = (DependencyBlock*)NativeJobCore.ContextPool.Rent(bytes);
            block->Next = null;
            block->Count = 0;
            block->Capacity = capacity;
            return block;
        }
        private static NativeJobHandle* Tokens(DependencyBlock* block) => (NativeJobHandle*)(block + 1);
        private static IntPtr* Handles(DependencyBlock* block) => (IntPtr*)(Tokens(block) + block->Capacity);
        private static void ReturnDependencies(DependencyBlock* block)
        {
            int bytes = sizeof(DependencyBlock) + block->Capacity * (sizeof(NativeJobHandle) + sizeof(IntPtr));
            NativeJobCore.ContextPool.Return((IntPtr)block, bytes);
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        internal static NativeJobHandle Combine(ReadOnlySpan<JobHandle> jobs, ReadOnlySpan<NativeJobHandle> native, int capacity = -1)
        {
            int length = jobs.IsEmpty ? native.Length : jobs.Length;
            if (capacity < 0)
            {
                capacity = 0;
                for (int i = 0; i < length; i++)
                    if ((jobs.IsEmpty ? native[i] : jobs[i]._nativeHandle).IsValid) capacity++;
            }
            if (capacity == 0) return default;
            DependencyBlock* block = RentDependencies(capacity);
            IntPtr combined = IntPtr.Zero;
            try
            {
                Enter();
                try
                {
                    if (!_accepting) return default;
                    for (int i = 0; i < length; i++)
                    {
                        NativeJobHandle token = jobs.IsEmpty ? native[i] : jobs[i]._nativeHandle;
                        if (token.Slot == IntPtr.Zero) continue;
                        IntPtr handle = GetHandle(token);
                        if (handle == IntPtr.Zero) continue;
                        if (block->Count == capacity) throw new InvalidOperationException("Dependency input changed during combination.");
                        Tokens(block)[block->Count] = token;
                        Handles(block)[block->Count++] = handle;
                    }
                    if (block->Count == 1)
                    {
                        combined = Handles(block)[0];
                        NativeJobCore.JobSystem_RetainHandle(combined);
                    }
                    else if (block->Count > 1)
                        combined = NativeJobCore.JobSystem_CombineDependencies(Handles(block), block->Count);
                    if (combined == IntPtr.Zero) return default;
                    NativeJobHandle result = CreateLocked(combined, block);
                    combined = IntPtr.Zero;
                    block = null;
                    return result;
                }
                finally { Exit(); }
            }
            finally
            {
                if (combined != IntPtr.Zero) NativeJobCore.JobSystem_ReleaseHandle(combined);
                if (block != null) ReturnDependencies(block);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        internal static NativeJobHandle CreateDependent(IntPtr handle, NativeJobHandle? dependency)
        {
            if (handle == IntPtr.Zero) return default;
            DependencyBlock* block = null;
            if (dependency.HasValue && dependency.Value.IsValid)
            {
                try { block = RentDependencies(1); }
                catch { NativeJobCore.JobSystem_ReleaseHandle(handle); throw; }
                Tokens(block)[0] = dependency.Value;
                block->Count = 1;
            }
            return Create(handle, block);
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        private static DependencyBlock* TakeLocked(NativeJobHandle token, bool consumeDependencies, out IntPtr handle,
            bool requireCompleted = false)
        {
            handle = IntPtr.Zero;
            if (token.Slot == IntPtr.Zero) return null;
            DependencyBlock* block;
            bool completed;
            handle = GetHandle(token);
            if (handle == IntPtr.Zero) return null;
            var slot = (Slot*)token.Slot;
            completed = Volatile.Read(ref *((byte*)handle + CompletedFlagOffset)) != 0;
            if (requireCompleted && !completed) { handle = IntPtr.Zero; return null; }
            block = slot->Dependencies;
            slot->Dependencies = null;
            Volatile.Write(ref slot->Handle, IntPtr.Zero);
            if (slot->Generation != long.MaxValue)
            {
                slot->Next = _free;
                _free = slot;
            }
            if (block != null && (!completed || !consumeDependencies || NativeJobCore.HasPendingJobExceptions))
            {
                ReturnDependencies(block);
                block = null;
            }
            return block;
        }

        [MethodImpl(MethodImplOptions.AggressiveOptimization)]
        internal static void Release(NativeJobHandle token)
        {
            if (token.Slot == IntPtr.Zero) return;
            bool consumeDependencies = !NativeJobCore.HasPendingJobExceptions;
            DependencyBlock* pending;
            IntPtr root;
            Enter();
            try { pending = TakeLocked(token, consumeDependencies, out root); }
            finally { Exit(); }
            if (root != IntPtr.Zero) NativeJobCore.JobSystem_ReleaseHandle(root);
            while (pending != null)
            {
                DependencyBlock* current = pending;
                pending = current->Next;
                int count = 0;
                Enter();
                try
                {
                    for (int i = 0; i < current->Count; i++)
                    {
                        DependencyBlock* child = TakeLocked(Tokens(current)[i], consumeDependencies,
                            out IntPtr handle, requireCompleted: true);
                        if (handle != IntPtr.Zero) Handles(current)[count++] = handle;
                        if (child == null) continue;
                        child->Next = pending;
                        pending = child;
                    }
                }
                finally { Exit(); }
                if (count != 0) NativeJobCore.JobSystem_ReleaseHandles(Handles(current), count);
                ReturnDependencies(current);
            }
        }

        internal static void SetAccepting(bool accepting)
        {
            Enter();
            try { _accepting = accepting; }
            finally { Exit(); }
        }

        // 槽位页为进程内高水位缓存，关闭并排空后仍保留地址，以便旧副本安全失效。
        internal static void ReleaseAll()
        {
            for (Page* page = _pages; page != null; page = page->Next)
            {
                var slots = (Slot*)((byte*)page + PageHeaderSize);
                for (int i = 0; i < SlotsPerPage; i++)
                    if (slots[i].Handle != IntPtr.Zero)
                        Release(new NativeJobHandle((IntPtr)(slots + i), slots[i].Generation));
            }
        }
    }
}
