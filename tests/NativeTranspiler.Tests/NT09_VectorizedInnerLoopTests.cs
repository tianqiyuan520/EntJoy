using Xunit;
using Xunit.Abstractions;

namespace NativeTranspiler.Tests
{
    /// <summary>
    /// NT-09（Critical）：<c>GenerateVectorizedInnerLoop</c> 凭空捏造"对**原始输入数组**做 min/max"的归约。
    ///
    /// 旧实现：在体内扫到**第一个** <c>if (x &lt; y)</c> / <c>if (x &gt; y)</c> 就据此挑出
    /// <c>n_min_ps</c> / <c>n_max_ps</c>，然后对体内**读到的每一个** NativeArray 的原始元素做归约，
    /// 再把结果写回外层最后一条赋值 —— **体内真正的计算从未被使用**。
    ///
    /// 最近点形态（本用例）：
    /// <code>
    /// for (j…) { float best = FLT_MAX;
    ///   for (i…) { float d = arr[j*8+i] * arr[j*8+i]; if (d &lt; best) { best = d; bestIdx[j] = i; } }
    ///   res[j] = best; }
    /// </code>
    /// 旧产物 = <c>v_best = n_min_ps(v_best, n_load_ps(arr_ptr + base + i))</c>（对原始 arr 元素取 min，
    /// 丢掉平方与 bestIdx）⇒ **错值 + 丢掉 argmin**。
    ///
    /// 修法：只有在**能证明**"内层体 = 把单个 NativeArray 的原始元素 min/max 到累加量、
    /// 累加量初始化恰为对应单位元、随后写回 <c>ra[ov]</c>"时才向量化；证明不了就老实退
    /// <c>FallbackScalarTranslation</c>。绝不从"比较扫描"合成归约。
    /// </summary>
    public class NT09_VectorizedInnerLoopTests
    {
        private readonly ITestOutputHelper _out;
        public NT09_VectorizedInnerLoopTests(ITestOutputHelper output) => _out = output;

        /// <summary>最近点/argmin 形态：内层体的比较对象是**派生值** <c>d</c>，不是原始元素。</summary>
        private const string ClosestShape = @"
using NativeTranspiler; using EntJoy.JobSystem; using EntJoy.Collections;
public static class NT09Closest {
    [NativeTranspile(AutoSIMD = AutoSIMD.Enabled)]
    public static void Closest(NativeArray<float> arr, NativeArray<int> bestIdx, NativeArray<float> res) {
        for (int j = 0; j < 4; j++) {
            float best = 3.402823466e+38f;
            for (int i = 0; i < 8; i++) {
                float d = arr[j * 8 + i] * arr[j * 8 + i];
                if (d < best) { best = d; bestIdx[j] = i; }
            }
            res[j] = best;
        }
    }
}";

        /// <summary>真·逐行原始元素 min：这一条**必须**继续向量化（守卫，防止把功能删掉了事）。</summary>
        private const string RowMinShape = @"
using NativeTranspiler; using EntJoy.JobSystem; using EntJoy.Collections;
public static class NT09RowMin {
    [NativeTranspile(AutoSIMD = AutoSIMD.Enabled)]
    public static void RowMin(NativeArray<float> arr) {
        for (int j = 0; j < 4; j++) {
            float best = 3.402823466e+38f;
            for (int i = 0; i < 8; i++) {
                if (arr[j * 8 + i] < best) { best = arr[j * 8 + i]; }
            }
            arr[j] = best;
        }
    }
}";

        /// <summary>同一形态但累加量初值不是单位元 ⇒ 发射器硬编码的 FLT_MAX 初值会改语义 ⇒ 必须退标量。</summary>
        private const string RowMinNonIdentityInitShape = @"
using NativeTranspiler; using EntJoy.JobSystem; using EntJoy.Collections;
public static class NT09RowMinInit {
    [NativeTranspile(AutoSIMD = AutoSIMD.Enabled)]
    public static void RowMinInit(NativeArray<float> arr) {
        for (int j = 0; j < 4; j++) {
            float best = 5.0f;
            for (int i = 0; i < 8; i++) {
                if (arr[j * 8 + i] < best) { best = arr[j * 8 + i]; }
            }
            arr[j] = best;
        }
    }
}";

        private static EmitResult Emit(string src) => GeneratorHarness.EmitFor(src);

        [Fact]
        public void ClosestShape_DoesNotSynthesizeRawArrayReduction()
        {
            var r = Emit(ClosestShape);
            _out.WriteLine(r.Cpp);

            Assert.False(r.Cpp.Contains("n_min_ps") || r.Cpp.Contains("n_max_ps"),
                GeneratorHarness.Fail(r, "最近点形态被捏造成\"对原始 arr 元素取 min\"的归约（错值）"));

            // 体内真正的计算必须还在：平方、比较、argmin 写回。
            Assert.True(r.Cpp.Contains("arr_ptr["),
                GeneratorHarness.Fail(r, "标量回退后应当仍有 arr_ptr[] 读取"));
            Assert.True(r.Cpp.Contains("bestIdx_ptr["),
                GeneratorHarness.Fail(r, "最近点形态丢掉了 argmin 写回 bestIdx[j] = i（旧实现整段丢掉内层体）"));
        }

        [Fact]
        public void ClosestShape_KeepsInnerComputation()
        {
            var r = Emit(ClosestShape);
            Assert.False(r.Cpp.Contains("vec_count"),
                GeneratorHarness.Fail(r, "证明不了就不该走 SIMD 批/内层向量路径"));

            // 内层体的 d < best 比较（有符号标量）必须保留 —— 旧产物的 n_min_ps 里没有它。
            Assert.True(r.Cpp.Contains("d < best"),
                GeneratorHarness.Fail(r, "内层体的 `d < best` 计算被丢弃"));
            Assert.True(r.Cpp.Contains("res_ptr[j] = best"),
                GeneratorHarness.Fail(r, "外层写回 res[j] = best 丢失"));
        }

        [Fact]
        public void RawRowMin_StillVectorizes()
        {
            // 守卫：这一条**能证明**恰好是"逐行原始元素 min"（初值 = 单位元、then 分支只更新累加量、
            // 写回 rr[ov]）⇒ 向量路径必须保留（本次修复不是"删功能"）。
            var r = Emit(RowMinShape);
            _out.WriteLine(r.Cpp);
            Assert.Contains("n_min_ps", r.Cpp);
            Assert.Contains("arr_ptr[j] = h;", r.Cpp);
        }

        [Fact]
        public void RowMinWithNonIdentityInit_FallsBack()
        {
            // 发射器硬编码 FLT_MAX 作初值；`float best = 5.0f` 时这会改变结果（所有元素 > 5 时）
            // ⇒ 证明不成立 ⇒ 退标量。
            var r = Emit(RowMinNonIdentityInitShape);
            _out.WriteLine(r.Cpp);
            Assert.False(r.Cpp.Contains("n_min_ps"),
                GeneratorHarness.Fail(r, "累加量初值不是单位元，向量化会改语义（硬编码 FLT_MAX）"));
            Assert.True(r.Cpp.Contains("best = 5.0f") || r.Cpp.Contains("best = 5"),
                GeneratorHarness.Fail(r, "标量回退后应保留 C# 的初值 5.0f"));
        }
    }
}
