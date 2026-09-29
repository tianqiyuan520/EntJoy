using System;
using System.Reflection;
using EntJoy.Collections;
using Xunit;

namespace EntJoy.ECS.Tests
{
    /// <summary>
    /// 安全句柄索引复用时的**豁免标志残留**（审计项 ①，静默失效类）：
    ///
    /// `AtomicSafetyHandle` 的 `_writeTxExempt[index]` 表示"该句柄豁免并行写冲突检测"
    /// （ECS chunk view 等框架共享句柄会用它）。但 `Allocate()` 在**复用**空闲索引时
    /// 只重置 `_state`/`_version`，**不清 `_writeTxExempt`** ⇒ 一旦被豁免过的句柄释放、
    /// 其索引被新容器拿到，新容器就静默继承豁免：Job 持有它时主线程写入不再报错，安全网无声消失。
    ///
    /// 修复：`Allocate()` 交付任何索引前把该标志清零。
    ///
    /// ⚠ 说明：这里用反射直接写 `_writeTxExempt`（等价于 `ExemptWriteTracking` 的效果）——
    /// 该 public API 在测试工程实际引用的程序集配置里未被编译进来（CS0117），
    /// 而本用例要验证的是**索引复用时的清零不变式**，与调用入口无关。
    /// </summary>
    public class SafetyHandleExemptReuseTests
    {
        private static readonly FieldInfo? ExemptField =
            typeof(SafetyHandleManager).GetField("_writeTxExempt", BindingFlags.NonPublic | BindingFlags.Static);

        private static readonly FieldInfo? SafetyField =
            typeof(NativeArray<int>).GetField("_safety", BindingFlags.NonPublic | BindingFlags.Instance);

        private static int[] ExemptFlags()
        {
            if (ExemptField == null) Assert.Fail("反射取不到 SafetyHandleManager._writeTxExempt（测试基建失效）");
            return (int[])ExemptField!.GetValue(null)!;
        }

        private static int SafetyIndexOf(NativeArray<int> arr)
        {
            if (SafetyField == null) Assert.Fail("反射取不到 NativeArray<int>._safety（测试基建失效）");
            return ((AtomicSafetyHandle)SafetyField!.GetValue(arr)!).Index;
        }

        [Fact]
        public void AllocatedHandle_MustNotInheritExemptFlagFromRecycledIndex()
        {
            var flags = ExemptFlags();

            // 空闲队列是 FIFO：本轮被豁免并释放的索引会在后续轮次被重新交付。
            // 因此"每一轮交付出来的索引都必须是干净的非豁免态"是确定性判据（不依赖具体交付顺序）。
            for (int round = 0; round < 64; round++)
            {
                var a = new NativeArray<int>(4, Allocator.Persistent);
                int idx = SafetyIndexOf(a);
                SafetyHandleManager.ExemptWriteTracking(idx);   // 模拟 chunk view 的豁免句柄
                Assert.Equal(1, flags[idx]);
                a.Dispose();                    // 索引回到空闲队列

                var b = new NativeArray<int>(4, Allocator.Persistent);
                int idx2 = SafetyIndexOf(b);
                Assert.True(flags[idx2] == 0,
                    $"第 {round} 轮：新容器拿到索引 {idx2} 时继承了残留的豁免标志（该容器的并行持有跟踪会静默失效）");
                b.Dispose();
            }
        }

        [Fact]
        public void ExemptFlag_MustBeClearedOnEveryHandout_EvenWhenIndexIsReusedRepeatedly()
        {
            var flags = ExemptFlags();

            // 反复"标记 → 释放 → 再分配"同一个索引，直至观察到索引复用为止；
            // 复用发生时标志必须已经被清零。
            int idx = -1;
            for (int i = 0; i < 32; i++)
            {
                var a = new NativeArray<int>(4, Allocator.Persistent);
                int cur = SafetyIndexOf(a);
                SafetyHandleManager.ExemptWriteTracking(cur);
                a.Dispose();

                var b = new NativeArray<int>(4, Allocator.Persistent);
                idx = SafetyIndexOf(b);
                Assert.True(flags[idx] == 0, $"索引 {idx} 复用后仍带豁免标志");
                b.Dispose();
            }
            Assert.True(idx >= 0);
        }
    }
}
