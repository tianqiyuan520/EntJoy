using Xunit;
using Xunit.Abstractions;

namespace NativeTranspiler.Tests
{
    /// <summary>
    /// IJobChunk 翻译器的三处"响的缺口"（不是静默错值，但会漏出未声明的 C# 名/不支持的形）。
    /// 由第三轮独立验收的 C21/C25 探针与索引表达式审计发现：
    ///   1. `arr.GetUnsafePtr()`（chunk 数组**局部**）原样输出 C# 文本 ⇒ C++ 编译失败；
    ///   2. `this.X`（合法的字段访问、遮蔽时必写）落到基类 default 分支 ⇒ 产物是
    ///      `/*__ENTJOY_UNSUPPORTED_EXPR__ThisExpression*/` ⇒ 构建失败；
    ///   3. 别名把索引表达式**原样抄文本** ⇒ `arr[Idx[k]]`（NativeArray 字段索引）漏出未声明的 `Idx`。
    /// </summary>
    public class ChunkTranslatorGapTests
    {
        private readonly ITestOutputHelper _out;
        public ChunkTranslatorGapTests(ITestOutputHelper output) => _out = output;

        /// <summary>
        /// 从产物里取某个 job 的**函数体**：必须先定位 `<name>.cpp` 文件段（同一份产物里
        /// 头文件/适配器也含函数名，直接 IndexOf 会命中声明行，导致窗口切错而假通过/假失败）。
        /// </summary>
        private static string CppFunctionBody(string cpp, string jobFunctionName)
        {
            string text = cpp.Replace("\r\n", "\n");
            string marker = "// ===== " + jobFunctionName + ".cpp =====";
            int fileStart = text.IndexOf(marker, System.StringComparison.Ordinal);
            if (fileStart < 0) return "";
            int fn = text.IndexOf("GENERATED_API void CALLINGCONVENTION", fileStart, System.StringComparison.Ordinal);
            if (fn < 0) return "";
            int end = text.IndexOf("\n}", fn, System.StringComparison.Ordinal);
            return end > fn ? text.Substring(fn, end - fn) : text.Substring(fn);
        }

        [Fact]
        public void UnsafePtrOnChunkArrayLocal_TranslatesToPtr()
        {
            // ⚠ 不能用 `unsafe { ... }` 语句块：它本身不被转译（发
            // `// __ENTJOY_UNSUPPORTED_STMT__UnsafeStatement` 标记）⇒ 断言会因为没有这句而假通过。
            // 这里把 `unsafe` 放在**方法修饰符**上，确保 `arr.GetUnsafePtr()` 真的进入翻译。
            var result = GeneratorHarness.EmitFor(@"
using NativeTranspiler;
using EntJoy.ECS;

[NativeTranspile]
public struct UnsafePtrJob : IJobChunk
{
    public NativeArray<float> Result;
    public unsafe void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
    {
        var arr = chunk.GetComponentDataNativeArray<float>();
        ((float*)arr.GetUnsafePtr())[0] = 1.0f;
        Result[0] = 2.0f;
    }
}
");
            _out.WriteLine(result.DiagnosticSummary);
            _out.WriteLine(result.Cpp);
            string text = result.Cpp.Replace("\r\n", "\n");

            int fn = text.IndexOf("GENERATED_API void CALLINGCONVENTION SharpNative_Job__global_namespace__UnsafePtrJob_Execute(",
                System.StringComparison.Ordinal);
            Assert.True(fn >= 0, GeneratorHarness.Fail(result, "形状没有被生成"));
            int end = text.IndexOf("\n}", fn, System.StringComparison.Ordinal);
            string body = end > fn ? text.Substring(fn, end - fn) : text.Substring(fn);

            Assert.DoesNotContain("GetUnsafePtr", body);
            Assert.Contains("arr_ptr", body);
            Assert.Contains("= 1.0f;", body);
        }

        [Fact]
        public void ThisQualifiedFieldAccess_Translates()
        {
            var result = GeneratorHarness.EmitFor(@"
using NativeTranspiler;
using EntJoy.ECS;

[NativeTranspile]
public struct ThisFieldJob : IJobChunk
{
    public NativeArray<float> Src;
    public NativeArray<float> Result;
    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
    {
        var arr = chunk.GetComponentDataNativeArray<float>();
        for (int k = 0; k < arr.Length; k++)
        {
            var e = arr[k];
            this.Src[k] = e;
            Result[k] = this.Src[k];
        }
    }
}
");
            _out.WriteLine(result.DiagnosticSummary);
            _out.WriteLine(result.Cpp);
            string text = result.Cpp.Replace("\r\n", "\n");

            Assert.True(text.Contains("ThisFieldJob_Execute"), GeneratorHarness.Fail(result, "形状没有被生成"));
            // 只看**产物函数体**：CMakeLists.txt 样板里本来就含 `__ENTJOY_UNSUPPORTED` 字样。
            int fn = text.IndexOf("GENERATED_API void CALLINGCONVENTION SharpNative_Job__global_namespace__ThisFieldJob_Execute(",
                System.StringComparison.Ordinal);
            Assert.True(fn >= 0, GeneratorHarness.Fail(result, "找不到产物函数定义"));
            int end = text.IndexOf("\n}", fn, System.StringComparison.Ordinal);
            string body = end > fn ? text.Substring(fn, end - fn) : text.Substring(fn);
            Assert.DoesNotContain("__ENTJOY_UNSUPPORTED", body);
            // 与裸字段访问**逐字一致**（该 ABI 下字段名被绑定为引用：裸写 `Src[k]` → `Src[k]`）
            Assert.Contains("Src[k]", body);
            Assert.DoesNotContain("this.", body);
        }

        [Fact]
        public void AliasIndexExpressions_AreReTranslated()
        {
            // 索引是 NativeArray 字段：必须译成 `Idx_ptr[k]`，不能抄成 `Idx[k]`（未声明的 C# 名）。
            var result = GeneratorHarness.EmitFor(@"
using NativeTranspiler;
using EntJoy.ECS;
using EntJoy.Collections;

[NativeTranspile]
public struct FieldIndexJob : IJobChunk
{
    public NativeArray<int> Idx;
    public NativeArray<float> Result;
    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
    {
        var arr = chunk.GetComponentDataNativeArray<float>();
        for (int k = 0; k < arr.Length; k++)
        {
            var e = arr[Idx[k]];
            Result[k] = e;
        }
    }
}
");
            _out.WriteLine(result.Cpp);
            string text = result.Cpp.Replace("\r\n", "\n");

            Assert.True(text.Contains("FieldIndexJob_Execute"), GeneratorHarness.Fail(result, "形状没有被生成"));
            Assert.DoesNotContain("arr_ptr[Idx[k]]", text);
            Assert.Contains("Idx_ptr[k]", text);
        }

        [Fact]
        public void UnsafeBlock_IsTranslatedLikePlainBlock()
        {
            // `unsafe { ... }` 只是编译期许可，无运行期语义 ⇒ 必须按普通块翻译。
            // （旧实现发 `__ENTJOY_UNSUPPORTED_STMT__UnsafeStatement` 标记 ⇒ 完全可译的代码构建失败。）
            var result = GeneratorHarness.EmitFor(@"
using NativeTranspiler;
using EntJoy.ECS;
using EntJoy.Collections;

[NativeTranspile]
public struct UnsafeBlockJob : IJobChunk
{
    public NativeArray<float> Result;
    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
    {
        var arr = chunk.GetComponentDataNativeArray<float>();
        unsafe
        {
            ((float*)arr.GetUnsafePtr())[0] = 3.0f;
        }
        Result[0] = arr[0];
    }
}
");
            _out.WriteLine(result.DiagnosticSummary);
            _out.WriteLine(result.Cpp);
            string text = result.Cpp.Replace("\r\n", "\n");

            int fn = text.IndexOf("GENERATED_API void CALLINGCONVENTION SharpNative_Job__global_namespace__UnsafeBlockJob_Execute(",
                System.StringComparison.Ordinal);
            Assert.True(fn >= 0, GeneratorHarness.Fail(result, "形状没有被生成"));
            int end = text.IndexOf("\n}", fn, System.StringComparison.Ordinal);
            string body = end > fn ? text.Substring(fn, end - fn) : text.Substring(fn);

            Assert.DoesNotContain("__ENTJOY_UNSUPPORTED", body);
            Assert.DoesNotContain("GetUnsafePtr", body);
            Assert.Contains("= 3.0f;", body);
        }

        [Fact]
        public void UncheckedBlock_TranslatesInWrapSafePath_ButEntityKeepsMarker()
        {
            // C++ 后端（wrap-safe 算术开启）里 `unchecked { }` = 普通块；实体路径（wrap-safe 关闭）
            // 不满足前提 ⇒ 必须继续发标记（不得静默丢掉 checked/unchecked 的语义区别）。
            var chunk = GeneratorHarness.EmitFor(@"
using NativeTranspiler;
using EntJoy.ECS;
using EntJoy.Collections;

[NativeTranspile]
public struct UncheckedChunkJob : IJobChunk
{
    public NativeArray<int> Result;
    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
    {
        var arr = chunk.GetComponentDataNativeArray<int>();
        unchecked
        {
            Result[0] = arr[0] + 1;
        }
    }
}
");
            _out.WriteLine(chunk.Cpp);
            string text = chunk.Cpp.Replace("\r\n", "\n");
            int fn = text.IndexOf("GENERATED_API void CALLINGCONVENTION SharpNative_Job__global_namespace__UncheckedChunkJob_Execute(",
                System.StringComparison.Ordinal);
            Assert.True(fn >= 0, GeneratorHarness.Fail(chunk, "chunk 形状没有被生成"));
            int end = text.IndexOf("\n}", fn, System.StringComparison.Ordinal);
            string body = end > fn ? text.Substring(fn, end - fn) : text.Substring(fn);
            Assert.DoesNotContain("__ENTJOY_UNSUPPORTED", body);
            Assert.Contains("Result_ptr[0] =", body);

            var entity = GeneratorHarness.EmitFor(@"
using NativeTranspiler;
using EntJoy.ECS;
using EntJoy.Collections;

public struct LUnc { public int V; }

[NativeTranspile]
public struct UncheckedEntityJob : IJobEntity
{
    public void Execute(ref LUnc p)
    {
        unchecked
        {
            p.V = p.V + 1;
        }
    }
}
");
            _out.WriteLine(entity.Cpp);
            string entityBody = CppFunctionBody(entity.Cpp, "SharpNative_Job__global_namespace__UncheckedEntityJob_Execute");
            Assert.True(entityBody.Length > 0, GeneratorHarness.Fail(entity, "entity 形状没有被生成"));
            Assert.Contains("__ENTJOY_UNSUPPORTED", entityBody);
        }

        [Fact]
        public void SwitchStatement_ConstantCases_Translates_PatternCaseKeepsMarker()
        {
            var constant = GeneratorHarness.EmitFor(@"
using NativeTranspiler;
using EntJoy.ECS;
using EntJoy.Collections;

[NativeTranspile]
public struct SwitchConstJob : IJobChunk
{
    public NativeArray<int> Result;
    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
    {
        var arr = chunk.GetComponentDataNativeArray<int>();
        for (int k = 0; k < arr.Length; k++)
        {
            switch (arr[k])
            {
                case 1:
                    Result[k] = 11;
                    break;
                case 2:
                case 3:
                    Result[k] = 22;
                    break;
                default:
                    Result[k] = 33;
                    break;
            }
        }
    }
}
");
            _out.WriteLine(constant.Cpp);
            string text = constant.Cpp.Replace("\r\n", "\n");
            int fn = text.IndexOf("GENERATED_API void CALLINGCONVENTION SharpNative_Job__global_namespace__SwitchConstJob_Execute(",
                System.StringComparison.Ordinal);
            Assert.True(fn >= 0, GeneratorHarness.Fail(constant, "switch 形状没有被生成"));
            int end = text.IndexOf("\n}", fn, System.StringComparison.Ordinal);
            string body = end > fn ? text.Substring(fn, end - fn) : text.Substring(fn);
            Assert.DoesNotContain("__ENTJOY_UNSUPPORTED", body);
            Assert.Contains("switch (arr_ptr[k])", body);
            Assert.Contains("case 1:", body);
            Assert.Contains("case 2:", body);
            Assert.Contains("case 3:", body);
            Assert.Contains("default:", body);

            // 模式 case（`case int x when ...`）→ 必须继续标记，不得猜译。
            var pattern = GeneratorHarness.EmitFor(@"
using NativeTranspiler;
using EntJoy.ECS;
using EntJoy.Collections;

[NativeTranspile]
public struct SwitchPatternJob : IJobChunk
{
    public NativeArray<int> Result;
    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
    {
        var arr = chunk.GetComponentDataNativeArray<int>();
        switch (arr[0])
        {
            case int x when x > 0:
                Result[0] = 1;
                break;
            default:
                Result[0] = 2;
                break;
        }
    }
}
");
            string patternBody = CppFunctionBody(pattern.Cpp, "SharpNative_Job__global_namespace__SwitchPatternJob_Execute");
            Assert.True(patternBody.Length > 0, GeneratorHarness.Fail(pattern, "pattern 形状没有被生成"));
            Assert.Contains("__ENTJOY_UNSUPPORTED", patternBody);
        }

        [Fact]
        public void PropertyIndex_IsNotAliased()
        {
            // 属性 getter 是方法调用（每次取值可能不同/有副作用）⇒ 不得别名。
            var result = GeneratorHarness.EmitFor(@"
using NativeTranspiler;
using EntJoy.ECS;

[NativeTranspile]
public struct PropertyIndexJob : IJobChunk
{
    public NativeArray<float> Result;
    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
    {
        var arr = chunk.GetComponentDataNativeArray<float>();
        for (int k = 0; k < arr.Length; k++)
        {
            var e = arr[chunk.Count];
            Result[k] = e;
        }
    }
}
");
            _out.WriteLine(result.DiagnosticSummary);
            _out.WriteLine(result.Cpp);
            string text = result.Cpp.Replace("\r\n", "\n");

            Assert.True(text.Contains("PropertyIndexJob_Execute"), GeneratorHarness.Fail(result, "形状没有被生成"));
            Assert.Contains("e = arr_ptr[", text);
            Assert.DoesNotContain("Result_ptr[k] = arr_ptr[", text);
        }
    }
}
