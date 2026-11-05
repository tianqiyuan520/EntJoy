using System;
using System.Threading;
using EntJoy.Collections;
using EntJoy.JobSystem;
using Xunit;

namespace EntJoy.ECS.Tests
{
    public unsafe class TypedJobContextTests
    {
        private struct ManagedSingle : IJob
        { public int[] Values; public bool Fail; public void Execute() => Visit(ref Values[0], Fail); }
        private struct ManagedFor : IJobFor
        { public int[] Values; public bool Fail; public void Execute(int i) => Visit(ref Values[i], Fail); }
        private struct ManagedParallel : IJobParallelFor
        { public int[] Values; public bool Fail; public void Execute(int i) => Visit(ref Values[i], Fail); }
        private struct ManagedBatch : IJobParallelForBatch
        { public int[] Values; public bool Fail; public void Execute(int first, int count) { for (int i = first; i < first + count; i++) Visit(ref Values[i], Fail); } }
        private struct RawSingle : IJob
        { public int* Values; public bool Fail; public void Execute() => Visit(ref Values[0], Fail); }
        private struct RawFor : IJobFor
        { public int* Values; public bool Fail; public void Execute(int i) => Visit(ref Values[i], Fail); }
        private struct RawParallel : IJobParallelFor
        { public int* Values; public bool Fail; public void Execute(int i) => Visit(ref Values[i], Fail); }
        private struct RawBatch : IJobParallelForBatch
        { public int* Values; public bool Fail; public void Execute(int first, int count) { for (int i = first; i < first + count; i++) Visit(ref Values[i], Fail); } }

        private static void Visit(ref int value, bool fail)
        {
            Interlocked.Increment(ref value);
            if (fail) throw new InvalidOperationException("typed-context-test");
        }

        private struct ParentGate : IJob
        {
            public ManualResetEventSlim Entered, Release;
            public int[] Managed;
            public int* Raw;
            public int Length;
            public void Execute()
            {
                Entered.Set();
                Release.Wait();
                for (int i = 0; i < Length; i++)
                    if (Managed != null) Managed[i] = 0; else Raw[i] = 0;
            }
        }

        [Theory]
        [InlineData(0, false)] [InlineData(1, false)] [InlineData(2, false)] [InlineData(3, false)]
        [InlineData(0, true)] [InlineData(1, true)] [InlineData(2, true)] [InlineData(3, true)]
        public void FourJobShapes_PreserveDependenciesAndTailResults(int shape, bool managed)
        {
            TestBackend.EnsureInitialized(2);
            int length = shape == 0 ? 1 : 19;
            using var storage = new NativeArray<int>(length, Allocator.Persistent);
            var array = managed ? new int[length] : null;
            int* pointer = (int*)storage.GetUnsafePtr();
            var data = managed ? array.AsSpan() : new Span<int>(pointer, length);
            data.Fill(-1);
            using var entered = new ManualResetEventSlim(false);
            using var release = new ManualResetEventSlim(false);
            var parentJob = new ParentGate { Entered = entered, Release = release, Managed = array, Raw = pointer, Length = length };
            var parent = parentJob.Schedule();
            JobHandle child = default;
            try
            {
                Assert.True(entered.Wait(3000), "Parent job did not start.");
                child = Schedule(shape, array, pointer, length, false, parent);
                Assert.False(child.IsCompleted);
                release.Set();
                child.Complete();
                for (int i = 0; i < length; i++) Assert.Equal(1, data[i]);
            }
            finally { release.Set(); child.Complete(); parent.Complete(); }
        }

        [Theory]
        [InlineData(0, false)] [InlineData(1, false)] [InlineData(2, false)] [InlineData(3, false)]
        [InlineData(0, true)] [InlineData(1, true)] [InlineData(2, true)] [InlineData(3, true)]
        public void FourJobShapes_PropagateFailureAndRemainReusable(int shape, bool managed)
        {
            TestBackend.EnsureInitialized(2);
            bool countStates = TestBackend.IsNative;
            long before = 0;
            if (countStates)
            {
                // 全局状态计数只在此前作业物理退役后取基线，避免计入其他用例的收尾。
                Assert.True(NativeJobCore.DrainAllCore());
                Assert.True(NativeJobCore.TryGetLiveHandleStateCount(out before));
            }
            int length = shape == 0 ? 1 : 19;
            using var storage = new NativeArray<int>(length, Allocator.Persistent);
            var array = managed ? new int[length] : null;
            int* pointer = (int*)storage.GetUnsafePtr();
            var data = managed ? array.AsSpan() : new Span<int>(pointer, length);
            data.Clear();
            var failed = Schedule(shape, array, pointer, length, true);
            var error = Record.Exception(() => failed.Complete());
            Assert.NotNull(error);
            Assert.Contains("typed-context-test", error.ToString());
            Assert.Equal(1, data[0]);
            int[] previous = data.ToArray();
            Schedule(shape, array, pointer, length, false).Complete();
            for (int i = 0; i < length; i++) Assert.Equal(previous[i] + 1, data[i]);
            if (countStates)
            {
                Assert.True(NativeJobCore.DrainAllCore());
                Assert.True(NativeJobCore.TryGetLiveHandleStateCount(out long after));
                Assert.Equal(before, after);
            }
        }

        private static JobHandle Schedule(int shape, int[] array, int* pointer, int length, bool fail, JobHandle dependency = default)
        {
            if (array != null)
            {
                switch (shape)
                {
                    case 0: var single = new ManagedSingle { Values = array, Fail = fail }; return JobScheduler.Schedule(ref single, dependency);
                    case 1: var loop = new ManagedFor { Values = array, Fail = fail }; return JobScheduler.ScheduleFor(ref loop, length, dependency);
                    case 2: var parallel = new ManagedParallel { Values = array, Fail = fail }; return JobScheduler.ScheduleParallelFor(ref parallel, length, 4, dependency);
                    case 3: var batch = new ManagedBatch { Values = array, Fail = fail }; return JobScheduler.ScheduleBatch(ref batch, length, 4, dependency);
                }
            }
            switch (shape)
            {
                case 0: var single = new RawSingle { Values = pointer, Fail = fail }; return JobScheduler.Schedule(ref single, dependency);
                case 1: var loop = new RawFor { Values = pointer, Fail = fail }; return JobScheduler.ScheduleFor(ref loop, length, dependency);
                case 2: var parallel = new RawParallel { Values = pointer, Fail = fail }; return JobScheduler.ScheduleParallelFor(ref parallel, length, 4, dependency);
                case 3: var batch = new RawBatch { Values = pointer, Fail = fail }; return JobScheduler.ScheduleBatch(ref batch, length, 4, dependency);
                default: throw new ArgumentOutOfRangeException(nameof(shape));
            }
        }
    }
}
