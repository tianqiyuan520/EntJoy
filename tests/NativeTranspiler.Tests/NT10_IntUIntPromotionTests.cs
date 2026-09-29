using System.Linq;
using Xunit;
using Xunit.Abstractions;

namespace NativeTranspiler.Tests
{
    /// <summary>
    /// NT-10（Critical）：`int` ↔ `uint` 的**二元数值提升**被误当成 32 位回绕。
    ///
    /// C# 的 `int op uint`（op ∈ * + -）按规范把**两侧都提升为 `long`** 再运算
    /// （C# spec 12.4.7：一边是 uint、另一边是 sbyte/short/int ⇒ 两侧转 long）。
    /// 生成器旧实现把 `UInt32` 也算作"int32"，于是发成
    /// <c>(int)((unsigned)left op (unsigned)right)</c> —— 32 位无符号算术 ⇒ **静默错值**：
    /// <c>100000 * 100000u</c> 在 C# 是 10,000,000,000，在生成物里是 1,410,065,408。
    /// 二阶后果：回绕后的表达式节点类型成了 `int`，随后的 `&gt;&gt;` / `%` 变成有符号语义
    /// （C# 侧是 long 上的非负值 ⇒ 逻辑位移）。
    ///
    /// 反例（必须保持不变）：`int op int` 与 `uint op uint` 的 C# 结果**仍是 32 位**，
    /// 回绕不是 UB ⇒ 保留 `<c>(int)((unsigned)…)</c>` 的无符号回绕写法。
    /// </summary>
    public class NT10_IntUIntPromotionTests
    {
        private readonly ITestOutputHelper _out;
        public NT10_IntUIntPromotionTests(ITestOutputHelper output) => _out = output;

        private const string MixedMulJob = @"
using NativeTranspiler; using EntJoy.JobSystem; using EntJoy.Collections;
[NativeTranspile]
public struct NT10MulJob : IJobParallelFor {
    public NativeArray<long> Out; public uint U;
    public void Execute(int i) { Out[i] = i * U; }
}";

        private const string MixedShiftJob = @"
using NativeTranspiler; using EntJoy.JobSystem; using EntJoy.Collections;
[NativeTranspile]
public struct NT10ShiftJob : IJobParallelFor {
    public NativeArray<long> Out; public uint U;
    public void Execute(int i) { Out[i] = (i * U) >> 3; }
}";

        private const string IntIntJob = @"
using NativeTranspiler; using EntJoy.JobSystem; using EntJoy.Collections;
[NativeTranspile]
public struct NT10IntJob : IJobParallelFor {
    public NativeArray<int> Out; public int U;
    public void Execute(int i) { Out[i] = i * U; }
}";

        private const string NegUIntJob = @"
using NativeTranspiler; using EntJoy.JobSystem; using EntJoy.Collections;
[NativeTranspile]
public struct NT10NegJob : IJobParallelFor {
    public NativeArray<long> Out; public uint U;
    public void Execute(int i) { Out[i] = -U; }
}";

        /// <summary>取出含指定锚点的第一条生成语句（去掉缩进）。</summary>
        private static string StmtWith(EmitResult r, string anchor)
        {
            var line = r.Cpp.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Contains(anchor));
            Assert.True(line != null, GeneratorHarness.Fail(r, $"产物里找不到含 '{anchor}' 的语句"));
            return line;
        }

        /// <summary>缺陷锚点：证明"C# 语义"与"生成器旧形态"确实不是同一个数。</summary>
        [Fact]
        public void DefectAnchor_CSharpPromotesMixedSignToLong()
        {
            int i = 100000;
            uint u = 100000u;
            Assert.Equal(10_000_000_000L, i * u);                  // C# 语义
            Assert.Equal(1_410_065_408, (int)((uint)i * u));        // 生成器旧形态（32 位回绕）
            Assert.NotEqual(i * u, (long)(int)((uint)i * u));       // ⇒ 静默错值
        }

        /// <summary>`i * U`（int × uint）必须以 64 位运算发出。</summary>
        [Fact]
        public void MixedSignMultiplication_IsWidenedTo64Bit()
        {
            var r = GeneratorHarness.EmitFor(MixedMulJob);
            _out.WriteLine(r.Cpp);
            var stmt = StmtWith(r, "Out_ptr[i]");

            Assert.False(stmt.Contains("(int)((unsigned)"),
                GeneratorHarness.Fail(r, "int×uint 被发成 32 位无符号回绕 ⇒ 静默错值: " + stmt));
            Assert.False(stmt.Contains("(unsigned)("),
                GeneratorHarness.Fail(r, "int×uint 的任一操作数被 32 位无符号重解释 ⇒ C++ 侧仍是 32 位算术: " + stmt));
            Assert.True(stmt.Contains("(long)(i)") && stmt.Contains("(long)(U)"),
                GeneratorHarness.Fail(r, "int×uint 未把两侧提升到 64 位: " + stmt));
            Assert.Contains("*", stmt);
        }

        /// <summary>二阶：回绕节点把 `&gt;&gt;` 变成有符号语义 ⇒ 混合符号提升后不得再出现 32 位节点。</summary>
        [Fact]
        public void MixedSignShift_DoesNotShiftA32BitNode()
        {
            var r = GeneratorHarness.EmitFor(MixedShiftJob);
            _out.WriteLine(r.Cpp);
            var stmt = StmtWith(r, "Out_ptr[i]");

            Assert.False(stmt.Contains("(unsigned)("),
                GeneratorHarness.Fail(r, ">> 的左操作数仍是 32 位回绕节点（有符号位移 / 已截断）: " + stmt));
            Assert.True(stmt.Contains(">> 3"), GeneratorHarness.Fail(r, ">> 3 丢失: " + stmt));
        }

        /// <summary>反例：`int * int` 的 C# 结果是 32 位 ⇒ 必须保留无符号回绕写法（不得回归）。</summary>
        [Fact]
        public void SameSignInt32_KeepsWrapSafeForm()
        {
            var r = GeneratorHarness.EmitFor(IntIntJob);
            var stmt = StmtWith(r, "Out_ptr[i]");
            Assert.Contains("(int)((unsigned)(i) * (unsigned)(U))", stmt);
        }

        /// <summary>一元负号同族缺陷：C# `-uint` 的结果类型是 **long**（旧实现按 32 位回绕发出）。</summary>
        [Fact]
        public void UnaryMinusOnUInt_IsWidenedTo64Bit()
        {
            var r = GeneratorHarness.EmitFor(NegUIntJob);
            _out.WriteLine(r.Cpp);
            var stmt = StmtWith(r, "Out_ptr[i]");

            // 反例锚点：uint.MaxValue 的取负在 C# 是 -4294967295L，32 位回绕会得到 1。
            uint umax = uint.MaxValue;                       // 用变量避免 checked 常量折叠
            Assert.Equal(-4294967295L, -(long)umax);
            Assert.Equal(1, unchecked((int)(0u - umax)));

            Assert.False(stmt.Contains("(int)(0u - "),
                GeneratorHarness.Fail(r, "-uint 被发成 32 位回绕 ⇒ 静默错值: " + stmt));
            Assert.Contains("(long)", stmt);
        }
    }
}
