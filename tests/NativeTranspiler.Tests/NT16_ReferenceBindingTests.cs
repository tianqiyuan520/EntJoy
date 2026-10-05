using System;
using Xunit;
using Xunit.Abstractions;

namespace NativeTranspiler.Tests
{
    /// <summary>
    /// NT-16（引用绑定缺口）：`const T&amp; X = *X_ptr;` 的对象**仍可经同函数其它指针被写**
    /// （`X_ptr` 是非 const 的 `T*`）⇒ 编译器不能把它当循环不变量 ⇒ **依赖该值的循环行程数在编译期
    /// 不可知**（每轮重载 + 无法 unroll/向量化）。判据：**参与循环行程数**（`for` 初值/条件/步进、
    /// `while`/`do` 条件）的值类型字段按值绑定，其余保持引用绑定（后者按值只有代价没有收益：
    /// 载入本可折进操作数，却要求该值跨循环存活）。
    ///
    /// 本测试钉住的是**发射面**（判据与三个历史臂开关）；汇编层面的解锁由真机 A/B 负责
    /// （`ZeroCellsJob`：8 条标量指令 0 向量 → 一次 `memset` 尾调用）。
    ///
    /// ⚠ 夹具陷阱：`GeneratorHarness.EmitFor` 对"**一个 job 都没识别到**"**不报错**（返回空串）——
    /// 例如源码漏写 `[NativeTranspile]`（无属性的 struct 不是 job，托管 job 是受支持配置，故无诊断）。
    /// 所以本类的断言一律用 `MustContain`（期望的绑定文本找不到即失败），避免"空产物也算通过"的假绿。
    /// 本轮的教训：曾据此误报"同一语法树内多个 job ⇒ 静默空集"，真因就是测试源码漏了属性。
    /// </summary>
    public class NT16_ReferenceBindingTests
    {
        private readonly ITestOutputHelper _out;
        public NT16_ReferenceBindingTests(ITestOutputHelper output) => _out = output;

        private const string Head = "using NativeTranspiler; using EntJoy.JobSystem; using EntJoy.Collections;\n";

        /// <summary>行程数字段（Length）按值，仅循环体内使用的字段（Scale）按引用。</summary>
        private const string SingleJob = Head + @"
[NativeTranspile]
public struct TripCountJob : IJob {
    public NativeArray<int> Out;
    public int Length;
    public float Scale;
    public void Execute() {
        for (int i = 0; i < Length; i++) { Out[i] = (int)(i * Scale); }
    }
}";

        /// <summary>批处理路径同样按判据：ChunkSize 按值、Bias 按引用。</summary>
        private const string BatchJob = Head + @"
[NativeTranspile]
public struct BatchTripJob : IJobParallelFor {
    public NativeArray<int> Out;
    public int ChunkSize;
    public int Bias;
    public void Execute(int index) {
        for (int k = 0; k < ChunkSize; k++) { Out[index] = k + Bias; }
    }
}";

        /// <summary>字段被同名局部变量屏蔽 ⇒ 保守保持引用绑定（不误判为行程数字段）。</summary>
        private const string ShadowJob = Head + @"
[NativeTranspile]
public struct ShadowTripJob : IJob {
    public NativeArray<int> Out;
    public int Count;
    public void Execute() {
        for (int Count = 0; Count < 4; Count++) { Out[Count] = Count; }
    }
}";

        /// <summary>行程数字段是**结构体**（>16 B）⇒ 默认档也按值绑定（一整份拷贝换编译期行程数）。</summary>
        private const string StructTripJob = Head + @"
[NativeTranspile]
public struct StructTripJob : IJob {
    public NativeArray<int> Out;
    public NtOpt Opt;
    public void Execute() {
        for (int i = 0; i < Opt.Count; i++) { Out[i] = Opt.Mul * i; }
    }
}
public struct NtOpt { public int Count; public int Mul; public float Pad0; public float Pad1; public float Pad2; }";

        /// <summary>同名字段与局部**不同作用域**：按符号解析后必须仍按值绑定（旧的按名字兜底会漏）。</summary>
        private const string ScopedShadowJob = Head + @"
[NativeTranspile]
public struct ScopedShadowJob : IJob {
    public NativeArray<int> Out;
    public int Length;
    public void Execute() {
        for (int i = 0; i < Length; i++) { Out[i] = 0; }
        if (Out[0] > 0) { int Length = 1; Out[0] = Length; }
    }
}";

        /// <summary>NativeList 的长度决定行程数 ⇒ `_listData` 形参加 `__restrict`（局部仍是非 const 引用）。</summary>
        private const string ListTripJob = Head + @"
[NativeTranspile]
public struct ListTripJob : IJob {
    public NativeArray<int> Out;
    public NativeList<int> Items;
    public void Execute() {
        for (int i = 0; i < Items.Length; i++) { Out[i] = Items[i]; }
    }
}";

        /// <summary>两个 NativeList 字段可能指向同一份 UnsafeList ⇒ 保守地**不加** `__restrict`。</summary>
        private const string TwoListJob = Head + @"
[NativeTranspile]
public struct TwoListJob : IJob {
    public NativeArray<int> Out;
    public NativeList<int> A;
    public NativeList<int> B;
    public void Execute() {
        for (int i = 0; i < A.Length; i++) { Out[i] = A[i] + B[i]; }
    }
}";

        /// <summary>只有**出现在循环内**的字段形参加 `__restrict`（循环外的 `Bias` 不加）。</summary>
        private const string LoopBodyFieldJob = Head + @"
[NativeTranspile]
public struct LoopBodyFieldJob : IJob {
    public NativeArray<float> Out;
    public float Scale;
    public float Bias;
    public void Execute() {
        for (int i = 0; i < 8; i++) { Out[i] = Out[i] * Scale; }
        Out[0] = Out[0] + Bias;
    }
}";

        /// <summary>跑一次生成（默认判据）。
        /// 用 <see cref="GeneratorHarness.EmitForJob"/>：源码漏写 `[NativeTranspile]` 时**直接抛**，
        /// 不让"空产物"变成假绿（本轮踩过的坑）。</summary>
        private static string Emit(string src) => GeneratorHarness.EmitForJob(src).Cpp;

        private void MustContain(string cpp, string needle, string why)
        {
            _out.WriteLine(why + " -> " + needle);
            Assert.True(cpp.Contains(needle), $"{why}\n期望出现: {needle}\n" + Bindings(cpp));
        }

        private static void MustNotContain(string cpp, string needle, string why)
            => Assert.False(cpp.Contains(needle), $"{why}\n不该出现: {needle}");

        private static string Bindings(string cpp)
        {
            var sb = new System.Text.StringBuilder("现场（所有字段绑定行）:\n");
            foreach (var line in cpp.Split('\n'))
                if (line.Contains("_ptr;")) sb.AppendLine("  " + line.Trim());
            return sb.ToString();
        }

        [Fact]
        public void SingleJob_TripCountFieldByValue_BodyOnlyFieldByReference()
        {
            var cpp = Emit(SingleJob);
            MustContain(cpp, "const int Length = *Length_ptr;",
                "单 IJob 路径：参与循环行程数的字段必须按值绑定（否则行程数在编译期不可知）");
            MustContain(cpp, "const float& Scale = *Scale_ptr;",
                "只在循环体内出现一次的字段保持引用绑定");
            MustNotContain(cpp, "const int& Length = *Length_ptr;", "行程数字段不得再按引用绑定");
        }

        [Fact]
        public void BatchJob_TripCountFieldByValue_NotPathBased()
        {
            var cpp = Emit(BatchJob);
            MustContain(cpp, "const int ChunkSize = *ChunkSize_ptr;",
                "批处理路径中参与行程数的字段同样按值绑定（判据是用途，不是路径）");
            MustContain(cpp, "const int& Bias = *Bias_ptr;",
                "批处理路径中仅循环体内使用的字段保持引用绑定");
        }

        [Fact]
        public void ShadowedFieldName_StaysByReference()
        {
            var cpp = Emit(ShadowJob);
            MustContain(cpp, "const int& Count = *Count_ptr;",
                "被同名局部屏蔽的字段必须保守地保持引用绑定");
        }

        /// <summary>防假绿守卫：源码漏写 `[NativeTranspile]` 时 `EmitForJob` 必须**抛**，
        /// 而不是像 `EmitFor` 那样返回空产物（本轮误报"生成器静默空集"的直接原因）。</summary>
        [Fact]
        public void HarnessGuard_ThrowsWhenNoJobIsRecognized()
        {
            var noAttr = SingleJob.Replace("[NativeTranspile]", "// attribute removed on purpose");
            Assert.Throws<InvalidOperationException>(() => GeneratorHarness.EmitForJob(noAttr));
            // 同一份源码带属性时正常产出 —— 证明差别只在属性
            Assert.Contains("GENERATED_API", GeneratorHarness.EmitForJob(SingleJob).Cpp);
        }

        // ── (l11)(i) 登记的三项"结构性缺口"的修复 ──────────────────────────────────

        /// <summary>缺口①：行程数字段是 >16 B 的结构体 —— 默认档放宽类型判据，按值绑定一次拷贝。</summary>
        [Fact]
        public void Gap1_StructTripCountField_IsValueBound()
        {
            var cpp = Emit(StructTripJob);
            MustContain(cpp, "const NtOpt Opt = *Opt_ptr;", "行程数字段即使是结构体也应按值绑定（换取编译期行程数）");
            MustNotContain(cpp, "const NtOpt& Opt = *Opt_ptr;", "结构体行程数字段不应再按引用绑定");
        }

        /// <summary>缺口① 的边界：**非**行程数字段的结构体仍按引用（不引入无谓拷贝）。</summary>
        [Fact]
        public void Gap1_StructFieldUsedOutsideLoop_StaysByReference()
        {
            var src = Head + @"
[NativeTranspile]
public struct StructNonTripJob : IJob {
    public NativeArray<int> Out;
    public NtOpt Opt;
    public void Execute() {
        for (int i = 0; i < 8; i++) { Out[i] = i; }
        Out[0] = Opt.Count + Opt.Mul;
    }
}
public struct NtOpt { public int Count; public int Mul; public float Pad0; public float Pad1; public float Pad2; }";
            var cpp = Emit(src);
            MustContain(cpp, "const NtOpt& Opt = *Opt_ptr;", "不参与行程数的结构体字段保持引用绑定");
        }

        /// <summary>缺口⑤：同名局部在**别的作用域**时，字段按符号解析后仍能按值绑定。</summary>
        [Fact]
        public void Gap5_SameNameLocalInOtherScope_DoesNotHideField()
        {
            var cpp = Emit(ScopedShadowJob);
            MustContain(cpp, "const int Length = *Length_ptr;",
                "循环条件里的 Length 解析到字段 ⇒ 必须按值绑定（按名字兜底会因嵌套局部而漏掉）");
        }

        /// <summary>缺口②：NativeList 长度决定行程数 ⇒ `_listData` 形参加 `__restrict`（局部保持非 const 引用）。</summary>
        [Fact]
        public void Gap2_ListLengthTripCount_AddsRestrictToParameter()
        {
            var cpp = Emit(ListTripJob);
            MustContain(cpp, "UnsafeList<int>* __restrict Items_listData", "表长度决定行程数时形参应加 __restrict");
            MustContain(cpp, "UnsafeList<int>& Items = *Items_listData;", "局部仍是非 const 引用（写语义不变）");
        }

        /// <summary>缺口② 的安全护栏：两个 NativeList 字段可能指向同一份表 ⇒ 不加 `__restrict`（避免说谎）。</summary>
        [Fact]
        public void Gap2_TwoListFields_NoRestrict()
        {
            var cpp = Emit(TwoListJob);
            MustNotContain(cpp, "__restrict A_listData", "两个表字段可能别名 ⇒ 不得加 __restrict");
            MustNotContain(cpp, "__restrict B_listData", "两个表字段可能别名 ⇒ 不得加 __restrict");
        }

        /// <summary>缺口⑥（默认落地）：循环内出现的字段形参加 `__restrict`，循环外的字段不加。</summary>
        [Fact]
        public void Gap6_ScalarRestrict_DefaultIsLoopOnly()
        {
            var dflt = Emit(LoopBodyFieldJob);
            MustContain(dflt, "float* __restrict Scale_ptr", "默认档：循环内字段应加 __restrict");
            MustNotContain(dflt, "float* __restrict Bias_ptr", "默认档：循环外字段不应加");
        }
    }
}
