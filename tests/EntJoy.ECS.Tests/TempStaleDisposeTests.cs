using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using EntJoy.Collections;
using Xunit;

namespace EntJoy.ECS.Tests
{
    /// <summary>
    /// B18 残留（2026-09-27 修复）：**帧末回收之后，陈旧 Temp 容器再 `Dispose()`**。
    ///
    /// `TempAllocator.Reset()` 会把帧末未手动释放的 Temp 容器 `MarkReleased` 并归还内存。此后若调用方
    /// 仍对那个陈旧容器调用 `Dispose()`，旧实现会：
    ///   ① index 已被新容器复用 → `Release` 看到 `Active` ⇒ 把**新容器的 index 再次入队**
    ///      （同一 index 可能被发给两个容器 ⇒ 安全跟踪失效）；
    ///   ② index 未被复用 → 仍按地址 `UnsafeUtility.Free` ⇒ 那块内存已归还池子、可能已重分配给别人
    ///      ⇒ 释放别人的块。
    /// 修复：释放路径增加"句柄是否仍活着"（状态 Active **且 version 未变**）判断，陈旧容器降级为幂等空操作。
    /// </summary>
    public class TempStaleDisposeTests
    {
        private static readonly FieldInfo? SafetyField =
            typeof(NativeArray<int>).GetField("_safety", BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly FieldInfo? FreeQueueField =
            typeof(SafetyHandleManager).GetField("_freeIndices", BindingFlags.NonPublic | BindingFlags.Static);

        /// <summary>安全句柄空闲队列当前长度（用于断言"陈旧 Dispose 不得重复入队"）。</summary>
        private static int FreeQueueCount()
        {
            if (FreeQueueField == null) Assert.Fail("反射取不到 SafetyHandleManager._freeIndices（测试基建失效）");
            var q = (ConcurrentQueue<int>)FreeQueueField!.GetValue(null)!;
            return q.Count;
        }

        private static int SafetyIndexOf(NativeArray<int> arr)
        {
            if (SafetyField == null) Assert.Fail("反射取不到 NativeArray<int>._safety（测试基建失效）");
            return ((AtomicSafetyHandle)SafetyField!.GetValue(arr)!).Index;
        }

        [Fact]
        public void StaleTempContainer_DisposeAfterReset_MustNotDisturbReusedIndex()
        {
            TestBackend.EnsureInitialized();
            TempAllocator.Reset();

            var a = new NativeArray<int>(16, Allocator.Temp);
            int idxA = SafetyIndexOf(a);
            TempAllocator.Reset();                     // 帧末：a 被 MarkReleased（状态 Released）+ 内存归还池子

            // ★ 不要靠"多试几轮碰运气"等 index 复用：空闲队列是 FIFO，队列长时结构上不可能在有限轮内命中
            //   （旧版本即因此偶发失败）。这里**把排在 idxA 之前的空闲索引全部取走**，使复用必然发生。
            var drain = new List<NativeArray<int>>();
            NativeArray<int> b;
            int attempts = 0;
            while (true)
            {
                b = new NativeArray<int>(16, Allocator.Temp);
                if (SafetyIndexOf(b) == idxA) break;
                drain.Add(b);
                if (++attempts > 4096)
                    Assert.Fail("未能消耗到 idxA（测试前提不成立：TempAllocator 的索引回收被谁破坏）");
            }

            int queuedBefore = FreeQueueCount();
            a.Dispose();                               // 陈旧容器的 Dispose 必须是幂等空操作
            Assert.Equal(queuedBefore, FreeQueueCount());   // 不得把已复用的 index 再次入队
            b[0] = 7;
            Assert.Equal(7, b[0]);                     // 修复前：b 的句柄被 Release 置 Released ⇒ 抛 ObjectDisposedException

            foreach (var d in drain) d.Dispose();
            b.Dispose();
        }

        [Fact]
        public void StaleTempContainer_DisposeAfterReset_MustBeIdempotent()
        {
            TestBackend.EnsureInitialized();

            var a = new NativeArray<int>(64, Allocator.Temp);
            a[0] = 1;
            TempAllocator.Reset();      // 帧末统一回收
            a.Dispose();                // 不得抛、不得再次按地址 Free
            a.Dispose();                // 再重复一次也必须幂等

            // 后续 Temp 分配照常可用
            var b = new NativeArray<int>(64, Allocator.Temp);
            b[0] = 9;
            Assert.Equal(9, b[0]);
        }

        [Fact]
        public void LiveTempContainer_Dispose_MustStillWork()
        {
            TestBackend.EnsureInitialized();

            // 反向守卫：修复不能把"活着的" Temp 容器的正常 Dispose 也变成空操作
            var a = new NativeArray<int>(16, Allocator.Temp);
            a[0] = 5;
            a.Dispose();
            Assert.Throws<InvalidOperationException>(() => a[0] = 1);
        }
    }
}
