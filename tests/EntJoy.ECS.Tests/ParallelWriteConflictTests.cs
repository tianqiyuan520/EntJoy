using System;
using EntJoy.Collections;
using EntJoy.JobSystem;
using Xunit;

namespace EntJoy.ECS.Tests
{
    /// <summary>
    /// 并行写冲突检测：不同 job 并行写同一 NativeArray → 在写入点按 job 上下文检测冲突并抛异常；
    /// 依赖链串行化则不冲突。同 job 多 worker 并行 tile 写不同 index 合法（不误报）。
    /// </summary>
    public class ParallelWriteConflictTests
    {
        private struct IncJob : IJobParallelFor
        {
            public NativeArray<long> Data;
            public void Execute(int index)
            {
                long v = Data[index];
                Data[index] = v + 1;
            }
        }

        private struct IncJobSingle : IJob
        {
            public NativeArray<long> Data;
            public void Execute()
            {
                // 忙等放大执行窗，使两个 IJob 并行时段实际重叠
                for (int i = 0; i < Data.Length; i++)
                {
                    long v = Data[i];
                    System.Threading.Thread.SpinWait(4);
                    Data[i] = v + 1;
                }
            }
        }

        private const int N = 2048;
        private const int InnerBatch = 128;

        // 对齐现有 ECS 测试惯例：一次性初始化后端，不 Shutdown（避免干扰并行测试类）
        static ParallelWriteConflictTests()
        {
            JobScheduler.Initialize();
        }

        private static long Sum(NativeArray<long> d)
        {
            long s = 0;
            for (int i = 0; i < d.Length; i++) s += d[i];
            return s;
        }

        [Fact]
        public void SerialDependency_NoConflict_SumsTo2N()
        {
            using var data = new NativeArray<long>(N, Allocator.Persistent);
            var a = new IncJob { Data = data }.Schedule(N, InnerBatch);
            var b = new IncJob { Data = data }.Schedule(N, InnerBatch, a);   // 依赖链
            b.Complete();
            Assert.Equal(2L * N, Sum(data));
        }

        [Fact]
        public void IJob_ParallelNoDependency_WriteConflict_Throws()
        {
            // 两个 IJob 并行写同一容器（job 内忙等重叠执行窗）
            for (int attempt = 0; attempt < 30; attempt++)
            {
                using var data = new NativeArray<long>(N, Allocator.Persistent);
                var a = new IncJobSingle { Data = data }.Schedule();
                var b = new IncJobSingle { Data = data }.Schedule();   // 无依赖 → 并行
                var ex = Record.Exception(() => { a.Complete(); b.Complete(); });
                if (ex != null && ContainsWriteConflict(ex))
                    return;
            }
            Assert.Fail("IJob parallel write conflict was not detected in any of 30 attempts.");
        }

        [Fact]
        public void ParallelNoDependency_WriteConflict_Throws()
        {
            // 写入点是「运行时竞态检测」：只有两个 job 的执行时段实际交叉写同一容器时才触发。
            // 调度时序可能偶发串行（尤其 Debug/Managed fallback），故多次调度循环，任一次命中即通过。
            for (int attempt = 0; attempt < 30; attempt++)
            {
                using var data = new NativeArray<long>(N, Allocator.Persistent);
                var a = new IncJob { Data = data }.Schedule(N, InnerBatch);
                var b = new IncJob { Data = data }.Schedule(N, InnerBatch);   // 无依赖 → 并行
                var ex = Record.Exception(() => { a.Complete(); b.Complete(); });
                if (ex != null && ContainsWriteConflict(ex))
                    return;   // 检测到冲突
            }
            Assert.Fail("Parallel write conflict was not detected in any of 30 attempts.");
        }

        /// <summary>Ensure the conflict exception surfaces (wrapped by job aggregation mechanism).</summary>
        private static bool ContainsWriteConflict(Exception? ex)
        {
            while (ex != null)
            {
                if (ex.Message.Contains("being written by another parallel job"))
                    return true;
                ex = ex.InnerException;
            }
            return false;
        }
    }
}