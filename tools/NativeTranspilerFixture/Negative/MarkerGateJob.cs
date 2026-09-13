using EntJoy.Collections;
using EntJoy.JobSystem;
using NativeTranspiler;

namespace NativeTranspilerFixture.Negative
{
    /// <summary>
    /// **负向夹具**（默认不参与编译，需 -p:FixtureMarkerGate=true）：
    /// 体内含生成器无法转译的 `unchecked { }` 语句块 —— 历史事故 N-10：语句块被静默丢弃，
    /// 非 void 函数会生成**空函数体**（C++ UB，调用时随机访问违例），而构建照样成功。
    ///
    /// 期望行为：构建**失败**，报错文本包含 "silently-degraded" 与
    /// `__ENTJOY_UNSUPPORTED_STMT__UncheckedStatement`（见 tools/NativeTranspilerFixture/negative-check.ps1）。
    /// </summary>
    [NativeTranspile]
    public struct MarkerGateJob : IJobParallelFor
    {
        public NativeArray<int> A;

        public void Execute(int index)
        {
            unchecked
            {
                A[index] = index + 1;
            }
        }
    }
}
