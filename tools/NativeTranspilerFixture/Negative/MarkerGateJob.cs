using EntJoy.Collections;
using EntJoy.JobSystem;
using NativeTranspiler;

namespace NativeTranspilerFixture.Negative
{
    /// <summary>
    /// **负向夹具**（默认不参与编译，需 -p:FixtureMarkerGate=true）：
    /// 体内含生成器无法转译的 `checked { }` 语句块 —— C# 的 checked 会在溢出时抛
    /// OverflowException，而 C++ 没有对应语义（生成代码是环绕算术）⇒ **不能**翻译成普通块，
    /// 必须写标记让构建失败，否则就是"编译通过但语义被静默改掉"。
    ///
    /// 历史事故 N-10 同类：语句块被静默丢弃 ⇒ 非 void 函数生成**空函数体**（C++ UB），构建照样成功。
    /// （`unchecked { }` 自 2026-09-26 起在 wrap-safe 路径**真的可译**，故不再用作负向样例。）
    ///
    /// 期望行为：构建**失败**，报错文本包含 "silently-degraded" 与
    /// `__ENTJOY_UNSUPPORTED_STMT__CheckedStatement`（见 tools/NativeTranspilerFixture/negative-check.ps1）。
    /// </summary>
    [NativeTranspile]
    public struct MarkerGateJob : IJobParallelFor
    {
        public NativeArray<int> A;

        public void Execute(int index)
        {
            checked
            {
                A[index] = index + 1;
            }
        }
    }
}
