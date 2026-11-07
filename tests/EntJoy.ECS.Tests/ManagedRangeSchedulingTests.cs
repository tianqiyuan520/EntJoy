using System;
using System.Reflection;
using System.Threading;
using EntJoy.JobSystem;
using EntJoy.JobSystem.Managed;
using Xunit;

namespace EntJoy.ECS.Tests
{
    public class ManagedRangeSchedulingTests
    {
        private struct MutableJob : IJobParallelFor
        {
            public int Counter;
            public int[] Values;
            public void Execute(int index) => Values[index] = ++Counter;
        }

        private struct FaultingJob : IJobParallelFor
        {
            public int[] Visits;
            public void Execute(int index)
            {
                Interlocked.Increment(ref Visits[index]);
                if (index == 17) throw new InvalidOperationException("expected");
            }
        }

        private struct BlockingJob : IJob
        {
            public CountdownEvent Started;
            public ManualResetEventSlim Release;
            public void Execute()
            {
                Started.Signal();
                if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
            }
        }

        private struct OneBlockedIndexJob : IJobParallelFor
        {
            public int[] Visits;
            public ManualResetEventSlim Blocked;
            public ManualResetEventSlim Release;
            public CountdownEvent OthersFinished;

            public void Execute(int index)
            {
                Interlocked.Increment(ref Visits[index]);
                if (index == 1024)
                {
                    Blocked.Set();
                    if (!Release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
                }
                else OthersFinished.Signal();
            }
        }

        private static void WithScheduler(Action action)
        {
            var field = typeof(ManagedJobScheduler).GetField("_isInitialized", BindingFlags.Static | BindingFlags.NonPublic);
            bool owned = !(bool)field.GetValue(null);
            ManagedJobScheduler.Initialize(2);
            try { action(); }
            finally { if (owned) ManagedJobScheduler.Shutdown(); }
        }

        [Theory]
        [InlineData(1)]
        [InlineData(7)]
        [InlineData(64)]
        public void RangeTokens_PreserveStructCopiesAtLogicalBatchBoundaries(int batch)
        {
            WithScheduler(() =>
            {
                var values = new int[4099];
                var job = new MutableJob { Values = values };
                ManagedJobScheduler.Schedule(ref job, values.Length, batch).Complete();
                for (int i = 0; i < values.Length; i++) Assert.Equal(i % batch + 1, values[i]);
            });
        }

        [Fact]
        public void AThrowingTile_DoesNotSkipOtherLogicalTiles()
        {
            WithScheduler(() =>
            {
                var visits = new int[113];
                var job = new FaultingJob { Visits = visits };
                var handle = ManagedJobScheduler.Schedule(ref job, visits.Length, 7);
                Assert.Throws<InvalidOperationException>(() => handle.Complete());
                for (int i = 0; i < visits.Length; i++)
                    Assert.Equal(i >= 18 && i <= 20 ? 0 : 1, visits[i]);
            });
        }

        [Fact]
        public void ABlockedIndex_DoesNotHoldTheRestOfItsSlice()
        {
            WithScheduler(() =>
            {
                var visits = new int[4096];
                using var blocked = new ManualResetEventSlim(false);
                using var release = new ManualResetEventSlim(false);
                using var othersFinished = new CountdownEvent(visits.Length - 1);
                var job = new OneBlockedIndexJob
                {
                    Visits = visits, Blocked = blocked, Release = release,
                    OthersFinished = othersFinished
                };
                var handle = ManagedJobScheduler.Schedule(ref job, visits.Length, 1);
                try
                {
                    Assert.True(blocked.Wait(TimeSpan.FromSeconds(5)));
                    Assert.True(othersFinished.Wait(TimeSpan.FromSeconds(5)));
                    Assert.False(handle.IsCompleted);
                }
                finally
                {
                    release.Set();
                    handle.Complete();
                }
                foreach (int visit in visits) Assert.Equal(1, visit);
            });
        }

        [Fact]
        public void CallerAssist_CanTakeWorkFromWorkerZero()
        {
            WithScheduler(() =>
            {
                var scheduler = typeof(ManagedJobScheduler);
                int workers = (int)scheduler.GetField("_workerCount", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
                using var started = new CountdownEvent(workers);
                using var release = new ManualResetEventSlim(false);
                var handles = new ManagedJobHandle[workers];
                ManagedWorkStealingDeque[] deques = null;
                try
                {
                    for (int i = 0; i < workers; i++)
                    {
                        var blocker = new BlockingJob { Started = started, Release = release };
                        handles[i] = ManagedJobScheduler.Schedule(ref blocker);
                    }
                    Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
                    // All owners are inside their callbacks, so worker zero's deque is exclusively available.
                    deques = (ManagedWorkStealingDeque[])scheduler.GetField("_deques", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
                    int executions = 0;
                    var task = new ManagedTileTask(new object(), (_, __, ___) => executions++, null, null, 0, 1);
                    deques[0].PushBottom(task);
                    bool assisted = (bool)scheduler.GetMethod("TryAssistOne", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
                    Assert.True(assisted);
                    Assert.Equal(1, executions);
                }
                finally
                {
                    if (deques != null) while (deques[0].StealTop(out _)) { }
                    release.Set();
                    foreach (var handle in handles) handle.Complete();
                }
            });
        }

        [Fact]
        public void AWaiter_StopsWhenItsCompletionSlotChangesOwner()
        {
            WithScheduler(() =>
            {
                var first = ManagedJobScheduler.RentCompletion();
                int generation = first.Generation;
                ManagedCompletion second = null;
                int secondGeneration = 0;
                bool returned = false;
                using var entered = new ManualResetEventSlim(false);
                using var finished = new ManualResetEventSlim(false);
                var waiter = new Thread(() =>
                {
                    entered.Set();
                    ManagedJobScheduler.CompleteSchedule(first, generation);
                    finished.Set();
                }) { IsBackground = true };
                try
                {
                    waiter.Start();
                    Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
                    Assert.False(finished.IsSet);
                    ManagedJobScheduler.ReturnCompletion(first, generation);
                    returned = true;
                    second = ManagedJobScheduler.RentCompletion();
                    secondGeneration = second.Generation;
                    Assert.Same(first, second);
                    Assert.True(finished.Wait(TimeSpan.FromSeconds(3)));
                    Assert.False(second.IsCompleted);
                }
                finally
                {
                    if (second != null)
                    {
                        second.Signal();
                        waiter.Join(TimeSpan.FromSeconds(3));
                        ManagedJobScheduler.ReturnCompletion(second, secondGeneration);
                    }
                    else if (!returned)
                    {
                        first.Signal();
                        waiter.Join(TimeSpan.FromSeconds(3));
                        ManagedJobScheduler.ReturnCompletion(first, generation);
                    }
                }
            });
        }

        [Fact]
        public void WrappedDeque_ConsumesEachPayloadOnce()
        {
            const int count = 200000;
            var deque = new ManagedWorkStealingDeque(8);
            var hits = new int[count];
            int done = 0, invalid = 0;
            void Record(ManagedTileTask task)
            {
                if ((uint)task.Start >= count) Interlocked.Increment(ref invalid);
                else Interlocked.Increment(ref hits[task.Start]);
            }
            var thief = new Thread(() =>
            {
                while (Volatile.Read(ref done) == 0 || !deque.IsEmpty)
                {
                    if (deque.StealTop(out var task)) Record(task);
                    else Thread.Yield();
                }
            }) { IsBackground = true };
            thief.Start();
            try
            {
                for (int i = 0; i < count; i++)
                {
                    while (deque.ApproxSize >= deque.Capacity)
                        if (deque.PopBottom(out var full)) Record(full);
                    deque.PushBottom(new ManagedTileTask { Start = i });
                    if ((i & 1) == 0 && deque.PopBottom(out var task)) Record(task);
                }
                while (deque.PopBottom(out var remaining)) Record(remaining);
            }
            finally
            {
                Volatile.Write(ref done, 1);
                Assert.True(thief.Join(TimeSpan.FromSeconds(10)));
            }
            Assert.Equal(0, invalid);
            foreach (int hit in hits) Assert.Equal(1, hit);
        }
    }
}
