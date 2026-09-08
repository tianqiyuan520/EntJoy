using System;
using EntJoy.Collections;
using Xunit;

namespace EntJoy.ECS.Tests
{
    /// <summary>
    /// Release UAF 兜底验收：安全句柄检测在 Debug(ENTJOY_SAFETY) 与 Release(ENTJOY_SAFETY_BOUNDS) 下均应生效。
    /// 若在 S22 全关档（-p:DefineConstants= 清空）下构建，本测试预期失败属正常（该档本就不做安全检查）。
    /// </summary>
    public unsafe class NativeCollectionSafetyTests
    {
        [Fact]
        public void Dispose_ThenRead_ThrowsOnDisposedHandle()
        {
            var arr = new NativeArray<int>(8);
            arr.Dispose();
            // 正常 Dispose 将句柄 index 置 -1，命中"Invalid handle index"分支（InvalidOperationException）；
            // MarkReleased/TempAllocator 清理路径（state=Released 且 index 有效）才走 ObjectDisposedException。
            Assert.Throws<InvalidOperationException>(() => { var _ = arr[0]; });
        }

        [Fact]
        public void Dispose_ThenWrite_ThrowsOnDisposedHandle()
        {
            var arr = new NativeArray<int>(8);
            arr.Dispose();
            Assert.Throws<InvalidOperationException>(() => arr[0] = 1);
        }

        [Fact]
        public void Dispose_ThenToArray_ThrowsOnDisposedHandle()
        {
            var arr = new NativeArray<int>(8);
            arr.Dispose();
            Assert.Throws<InvalidOperationException>(() => arr.ToArray());
        }

        [Fact]
        public void NativeList_Dispose_ThenRead_ThrowsOnDisposedHandle()
        {
            var list = new NativeList<int>(8);
            list.Add(1);
            list.Dispose();
            Assert.Throws<InvalidOperationException>(() => { var _ = list[0]; });
        }
    }
}