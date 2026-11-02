using System;
using System.Runtime.CompilerServices;
using EntJoy.Collections;
using Xunit;

namespace EntJoy.ECS.Tests
{
    public class NativeColumnTests
    {
        [Theory]
        [InlineData(4)]
        [InlineData(3)]
        public unsafe void ChunkViews_CrossBoundariesAndCopyWithoutOwningMemory(int capacity)
        {
            var first = new NativeArray<int>(capacity);
            var last = new NativeArray<int>(2);
            var pointers = new NativeArray<nint>(2);
            var copy = new NativeArray<int>(capacity + 2);
            try
            {
                pointers[0] = (nint)first.GetUnsafePtr(); pointers[1] = (nint)last.GetUnsafePtr();
                var view = NativeColumn<int>.CreateView((void**)pointers.GetUnsafePtr(), capacity, capacity + 2);
                Assert.Equal(32, Unsafe.SizeOf<NativeColumn<int>>());
                for (int i = 0; i < view.Length; i++) view[i] = i * 7;
                Assert.Equal(capacity * 7, last[0]);
                Assert.Equal((nint)last.GetUnsafePtr(), (nint)view.GetElementPtr(capacity));
                view.CopyTo(copy, view.Length);
                Assert.Equal(view.ToArray(), copy.ToArray());
                Assert.Throws<InvalidOperationException>(() => { view.GetUnsafePtr(); });
                view.Dispose();
                Assert.False(view.IsCreated);
                Assert.Equal(7, first[1]);
            }
            finally { first.Dispose(); last.Dispose(); pointers.Dispose(); copy.Dispose(); }
        }

        [Fact]
        public unsafe void ContiguousView_AliasesItsArray()
        {
            var values = new NativeArray<int>(3);
            try
            {
                NativeColumn<int> view = values;
                view[2] = 19;
                Assert.Equal(19, values[2]);
                Assert.Equal((nint)values.GetUnsafePtr(), (nint)view.GetElementPtr(0));
                Assert.Throws<ArgumentOutOfRangeException>(() => view.CopyTo((int*)values.GetUnsafePtr(), 4));
            }
            finally { values.Dispose(); }
        }
    }
}
