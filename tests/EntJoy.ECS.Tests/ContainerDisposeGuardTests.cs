using System;
using System.Reflection;
using System.Threading;
using EntJoy.Collections;
using EntJoy.JobSystem;
using Xunit;

namespace EntJoy.ECS.Tests
{
    /// <summary>
    /// 容器释放与句柄回收的两条不变式（审计 B18）：
    ///
    /// **① 释放前把关**：`Dispose()` 必须拒绝「容器仍被活动 Job 持有」的情况。
    /// 否则句柄索引会被放回空闲队列，而分配器会把刚释放的块交给下一个同尺寸请求 ——
    /// Job 的后续写入就落到**另一个活容器**的内存上（裸指针路径还绕过了索引器检查）。
    ///
    /// **② Temp 句柄索引必须回收**：帧末 `TempAllocator.Reset` 走的是 `MarkReleased`。
    /// 若它只把状态置为 Released 而不归还索引，则每帧每块 Temp 内存都会永久消耗一个 index
    /// （上限 1,048,576），耗尽后**所有**容器创建都会抛 "Out of safety handles"。
    /// </summary>
    public class ContainerDisposeGuardTests
    {
        private unsafe struct WriteAndHoldJob : IJobParallelFor
        {
            public NativeArray<float> Arr;
            public int* Flags; // [0]=已写入(持有写声明), [1]=放行

            public void Execute(int index)
            {
                if (index == 0)
                {
                    Arr[0] = 1f;      // 索引器写 → 登记写声明
                    Flags[0] = 1;
                }
                int spin = 0;
                while (Flags[1] == 0 && ++spin < 400_000_000) Thread.SpinWait(50);
            }
        }

        [Fact]
        public unsafe void DisposeWhileJobHoldsContainer_MustThrow()
        {
            TestBackend.EnsureInitialized();

            var arr = new NativeArray<float>(4, Allocator.Persistent);
            var flags = new NativeArray<int>(2, Allocator.Persistent);
            int* fp = (int*)flags.GetUnsafePtr();
            fp[0] = 0;
            fp[1] = 0;

            var job = new WriteAndHoldJob { Arr = arr, Flags = fp };
            var handle = JobScheduler.ScheduleParallelFor(ref job, 4, 1);

            int guard = 0;
            while (fp[0] == 0 && ++guard < 400_000_000) Thread.SpinWait(50);
            Assert.Equal(1, fp[0]);   // Job 已登记写声明

            var ex = Record.Exception(() => arr.Dispose());
            Assert.NotNull(ex);
            Assert.Contains("being written by an active job", ex!.Message);

            // 放行并完成后再释放必须成功
            fp[1] = 1;
            handle.Complete();
            arr.Dispose();
            flags.Dispose();
        }

        [Fact]
        public void TempContainerHandles_MustBeRecycledByFrameReset()
        {
            FieldInfo? nextIndex = typeof(SafetyHandleManager).GetField(
                "_nextIndex", BindingFlags.NonPublic | BindingFlags.Static);
            if (nextIndex == null)
                Assert.Fail("未能通过反射拿到 SafetyHandleManager._nextIndex（测试基建设施失效）");

            int before = (int)nextIndex!.GetValue(null)!;

            const int Frames = 2000;
            for (int f = 0; f < Frames; f++)
            {
                // 每帧一块 Temp（正确用法：不手动 Dispose，帧末统一回收）
                var a = new NativeArray<int>(16, Allocator.Temp);
                a[0] = f;
                TempAllocator.Reset();   // 帧末（SystemRunner.Update 每帧调用）→ 必须把索引还回空闲队列
            }

            int after = (int)nextIndex.GetValue(null)!;
            int growth = after - before;
            Assert.True(growth <= 8,
                $"Temp 句柄索引未随帧末回收：{Frames} 帧后 _nextIndex 增长 {growth}（应接近 0；否则约 100 万帧后所有容器创建都会抛 Out of safety handles）");
        }
    }
}
