using System;
using EntJoy.Collections;
using Xunit;

namespace EntJoy.ECS.Tests
{
    /// <summary>
    /// Collections 层三条 P2 加固（独立验收 F-05 / F-06 / F-07）：
    ///
    /// **F-05**：`NativeArray&lt;T&gt;.ReadOnly.ToArray()` 此前不做安全句柄检查 —— 视图在容器 Dispose
    /// 或帧末 Temp Reset 之后仍能**静默**拷贝已释放内存（索引器与拥有者的 ToArray 都检查，只有这条漏了）。
    ///
    /// **F-06**：`TempAllocator` 头部曾是 8 字节，payload = base+8，而 `Marshal.AllocHGlobal` 只保证 16 字节对齐
    /// ⇒ Temp payload 落在 8 mod 16 ⇒ `float4` / `Vector128` / `Vector256` 等需要 16/32 字节对齐的元素会被错位访问。
    ///
    /// **F-07**：`NativeList.TrimExcess()` 在空列表上只 `Resize(0)`（只把 Length 置 0，缓冲区仍持有），
    /// 与"容量缩减到 0"的语义不符 —— 曾经很大的列表会一直占着内存。
    /// </summary>
    public class CollectionsHardeningTests
    {
        [Fact]
        public void ReadOnlyToArray_AfterDispose_MustThrow()
        {
            var arr = new NativeArray<int>(4, Allocator.Persistent);
            arr[0] = 42;
            var readOnly = arr.AsReadOnly();
            Assert.Equal(42, readOnly.ToArray()[0]);   // 存活时正常

            arr.Dispose();                             // 视图不延长生命周期

            // 修复前：静默拷贝已释放内存（不抛异常）
            Assert.Throws<ObjectDisposedException>(() => readOnly.ToArray());
        }

        [Fact]
        public unsafe void TempAllocator_Payload_MustBeAtLeast16ByteAligned()
        {
            // 取一个 Temp 容器，检查其 payload 地址对齐（SIMD 元素需要 16/32 字节对齐）
            var t = new NativeArray<int>(8, Allocator.Temp);
            long addr = (long)t.GetUnsafePtr();
            Assert.Equal(0, addr % 16);
        }

        [Fact]
        public void NativeList_TrimExcess_OnEmptyList_MustFreeCapacity()
        {
            var list = new NativeList<int>(16, Allocator.Persistent);
            try
            {
                for (int i = 0; i < 100; i++) list.Add(i);
                Assert.True(list.Capacity >= 100);

                list.Clear();
                Assert.Equal(0, list.Length);

                list.TrimExcess();

                // 修复前：Capacity 仍 ≥100（Resize(0) 不释放缓冲区）
                Assert.Equal(0, list.Capacity);
            }
            finally
            {
                list.Dispose();
            }
        }
    }
}
