using System;
using System.Threading;
using EntJoy.Collections;
using EntJoy.JobSystem;
using Xunit;

namespace EntJoy.ECS.Tests
{
    /// <summary>
    /// F-04：`TempAllocator.Reset` 与**跨线程 Temp 释放**的锁序。
    ///
    /// 缺陷形态（修复前）：`Reset()` 先 `lock(_resetLock)` 再调用 `OnBeforeReset`（等待所有活跃 job，
    /// 由 ECS 层注册）；而 `TempAllocator.Free` 的**跨线程慢路径**要拿同一把 `_resetLock`。
    /// 若某个 job 正在做跨线程 Temp 释放，就形成「Reset 持锁等 job、job 等锁」= 永久死锁。
    /// 修复：把 `OnBeforeReset` 挪到取锁**之前**（等待期间不持锁）。
    ///
    /// 本用例用**受控 hook** 精确复现该交错（不等 ECS 世界状态）：
    /// hook 里 `Complete()` 那个正在释放 Temp 的 job；修复前 Reset 持锁 → 双方互等 → 有界等待超时。
    /// </summary>
    public class TempAllocatorLockOrderTests
    {
        private static IntPtr s_payload;
        private static int s_entered;
        private static int s_go;
        private static int s_freed;

        private unsafe struct CrossThreadFreeJob : IJob
        {
            public void Execute()
            {
                Volatile.Write(ref s_entered, 1);
                int spin = 0;
                while (Volatile.Read(ref s_go) == 0 && ++spin < 400_000_000) Thread.SpinWait(50);
                // 让 Reset 有机会先进入（修复前它会持锁并等本 job）
                Thread.Sleep(5);
                TempAllocator.Free(s_payload);   // 跨线程：走慢路径 → 需要 _resetLock
                Volatile.Write(ref s_freed, 1);
            }
        }

        [Fact]
        public unsafe void Reset_WithConcurrentCrossThreadTempFree_MustNotDeadlock()
        {
            TestBackend.EnsureInitialized();
            TempAllocator.Reset();                 // 先清干净（同时验证上一轮 hook 已还原）

            s_entered = 0; s_go = 0; s_freed = 0;
            var arr = new NativeArray<int>(16, Allocator.Temp);   // 主线程分配 ⇒ 登记在主线程表
            s_payload = (IntPtr)arr.GetUnsafePtr();

            var prevHook = TempAllocator.OnBeforeReset;
            try
            {
                var job = new CrossThreadFreeJob();
                var handle = JobScheduler.Schedule(ref job);
                int spin = 0;
                while (Volatile.Read(ref s_entered) == 0 && ++spin < 200_000_000) Thread.SpinWait(50);
                Assert.Equal(1, Volatile.Read(ref s_entered));

                // Reset 期间"等待活跃 job"的动作：Complete 那个正在释放 Temp 的 job
                TempAllocator.OnBeforeReset = () => handle.Complete();

                var done = new ManualResetEventSlim(false);
                var runner = new Thread(() =>
                {
                    try { TempAllocator.Reset(); }
                    finally { done.Set(); }
                })
                { IsBackground = true };
                runner.Start();
                Volatile.Write(ref s_go, 1);      // 放行 job → 它开始跨线程释放

                bool finished = done.Wait(TimeSpan.FromSeconds(20));
                Assert.True(finished,
                    "TempAllocator.Reset 与跨线程 Temp 释放互等 ⇒ 死锁（F-04）：" +
                    "OnBeforeReset 必须在取 _resetLock **之前**调用");
                Assert.Equal(1, Volatile.Read(ref s_freed));
            }
            finally
            {
                TempAllocator.OnBeforeReset = prevHook;
                Volatile.Write(ref s_go, 1);      // 兜底放行，避免残留自旋
            }
        }
    }
}
