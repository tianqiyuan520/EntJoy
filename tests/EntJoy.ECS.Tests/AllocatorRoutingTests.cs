using System;
using System.Runtime.InteropServices;
using EntJoy.Collections;
using Xunit;

namespace EntJoy.ECS.Tests
{
    public unsafe class AllocatorRoutingTests
    {
        [Fact]
        public void PersistentAllocator_UsesTrackedPointerAndIgnoresUnknownPointers()
        {
            var foreign = Marshal.AllocHGlobal(64);
            try
            {
                var before = PersistentAllocator.GetStats();
                PersistentAllocator.Free((void*)foreign);
                Assert.True(PersistentAllocator.GetStats().Foreign > before.Foreign);
                Marshal.WriteInt32(foreign, 42);
                Assert.Equal(42, Marshal.ReadInt32(foreign));

                var p = (byte*)PersistentAllocator.Alloc(33);
                p[0] = 7;
                p[32] = 9;
                Assert.Equal(7, p[0]);
                Assert.Equal(9, p[32]);
                PersistentAllocator.Free(p);
                before = PersistentAllocator.GetStats();
                PersistentAllocator.Free(p);
                Assert.True(PersistentAllocator.GetStats().Foreign > before.Foreign);
            }
            finally { Marshal.FreeHGlobal(foreign); }
        }

        [Fact]
        public void NativeFixture_RoutesEachAllocatorAcrossDlls()
        {
            string path = Environment.GetEnvironmentVariable("ENTJOY_ALLOCATOR_FIXTURE");
            if (string.IsNullOrEmpty(path)) return;
            TestBackend.EnsureInitialized();
            Assert.True(TestBackend.IsNative);
            nint library = NativeLibrary.Load(path);
            try
            {
                var run = (delegate* unmanaged[Cdecl]<int, byte, int>)NativeLibrary.GetExport(library, "RunAllocatorFixture");
                var persistent = PersistentAllocator.GetStats();
                Assert.Equal(1, run((int)Allocator.Persistent, 1));
                Assert.True(PersistentAllocator.GetStats().Allocs > persistent.Allocs);
                foreach (var label in new[] { Allocator.Temp, Allocator.TempJob })
                {
                    var temp = TempAllocator.GetStats();
                    Assert.Equal(1, run((int)label, 1));
                    Assert.Equal(1, run((int)label, 0));
                    Assert.True(TempAllocator.GetStats().allocs > temp.allocs);
                    TempAllocator.Reset();
                    Assert.Equal(1, run((int)label, 1));
                }
                TempAllocator.Reset();
            }
            finally { NativeLibrary.Free(library); }
        }
    }
}
