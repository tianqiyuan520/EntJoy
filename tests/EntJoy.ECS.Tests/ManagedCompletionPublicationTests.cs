using System;
using System.Reflection;
using System.Threading;
using EntJoy.JobSystem.Managed;
using Xunit;

namespace EntJoy.ECS.Tests
{
    public class ManagedCompletionPublicationTests
    {
        [Fact]
        public void AFinishingSignal_CannotConsumeCallbacksFromTheNextPoolOwner()
        {
            var initialized = typeof(ManagedJobScheduler).GetField("_isInitialized", BindingFlags.Static | BindingFlags.NonPublic);
            bool wasInitialized = (bool)initialized.GetValue(null);
            ManagedJobScheduler.Initialize(2);
            ManagedCompletion first = null, second = null;
            int firstGeneration = 0, secondGeneration = 0, callbacks = 0;
            bool returnedFirst = false;
            Thread signal = null;
            try
            {
                first = ManagedJobScheduler.RentCompletion();
                firstGeneration = first.Generation;
                lock (first.SyncRoot)
                {
                    signal = new Thread(first.Signal) { IsBackground = true };
                    signal.Start();
                    Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref first.Remaining) == 0
                        && (signal.ThreadState & ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(3)));
                    if (first.IsCompleted)
                    {
                        ManagedJobScheduler.ReturnCompletion(first, firstGeneration);
                        returnedFirst = true;
                    }
                    second = ManagedJobScheduler.RentCompletion();
                    secondGeneration = second.Generation;
                    second.OnCompleted(() => Interlocked.Increment(ref callbacks), secondGeneration);
                }
                Assert.True(signal.Join(TimeSpan.FromSeconds(3)));
                Assert.Equal(0, Volatile.Read(ref callbacks));
                second.Signal();
                Assert.Equal(1, Volatile.Read(ref callbacks));
                ManagedJobScheduler.ReturnCompletion(second, secondGeneration);
                second = null;
            }
            finally
            {
                signal?.Join(TimeSpan.FromSeconds(3));
                if (second != null)
                {
                    if (Volatile.Read(ref second.Remaining) > 0) second.Signal();
                    ManagedJobScheduler.ReturnCompletion(second, secondGeneration);
                }
                if (first != null && !returnedFirst) ManagedJobScheduler.ReturnCompletion(first, firstGeneration);
                if (!wasInitialized) ManagedJobScheduler.Shutdown();
            }
        }
    }
}
