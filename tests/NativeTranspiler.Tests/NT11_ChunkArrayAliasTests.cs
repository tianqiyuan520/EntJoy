using Xunit;
using Xunit.Abstractions;

namespace NativeTranspiler.Tests
{
    /// <summary>
    /// NT-11（Critical）：CppChunkStatementTranslator 把 `var e = arr[i];`（arr = chunk 数组）在
    /// "看起来只读"时**别名**成 `arr_ptr[i]`（省一次拷贝）。但只读判定 `BlockWritesChunkArray`
    /// 只看 `block.Statements`（直接语句）⇒ 写在 if/for 体内时看不见。
    /// C# 里 `var e = arr[i];` 是**值拷贝**；别名后读的是被改写过的数组（静默错值）。
    ///
    /// 另外：索引表达式有副作用（`arr[cursor++]`）时也不能别名 —— 别名会让副作用在每次使用时重复求值。
    /// </summary>
    public class NT11_ChunkArrayAliasTests
    {
        private readonly ITestOutputHelper _out;
        public NT11_ChunkArrayAliasTests(ITestOutputHelper output) => _out = output;

        [Fact]
        public void IndexVariableReassigned_IsNotAliased()
        {
            // NT-11 相邻洞：索引变量在别名作用域内被改写 ⇒ 别名把 `arr[i]` 的求值推迟到每次使用，
            // 读到的是**另一个**元素（C# 里 `e` 是声明处的值拷贝）。
            var result = GeneratorHarness.EmitFor(@"
using NativeTranspiler;
using EntJoy.ECS;
using EntJoy.Collections;

public struct LPod5 { public float V; }

[NativeTranspile]
public struct ReassignedIndexJob : IJobChunk
{
    public NativeArray<float> Result;
    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
    {
        var arr = chunk.GetComponentDataNativeArray<LPod5>();
        for (int i = 0; i < 4; i++)
        {
            var e = arr[i];
            i = i + 1;
            Result[i] = e.V;
        }
    }
}
");
            _out.WriteLine(result.Cpp);
            string text = result.Cpp.Replace("\r\n", "\n");
            Assert.DoesNotContain("arr_ptr[i].V", text);
            Assert.Contains("e = arr_ptr[i];", text);
        }

        [Fact]
        public void WriteBackPattern_StillUsesAlias()
        {
            // 反向守卫：读-改-写回（`e.V = ...; arr[i] = e;`）下"别名 + 省略写回"与 C# 等价，
            // 优化必须保留 —— 不得因为上述加固而一律退回真拷贝。
            var result = GeneratorHarness.EmitFor(@"
using NativeTranspiler;
using EntJoy.ECS;
using EntJoy.Collections;

public struct LPod6 { public float V; }

[NativeTranspile]
public struct WriteBackAliasJob : IJobChunk
{
    public NativeArray<float> Result;
    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
    {
        var arr = chunk.GetComponentDataNativeArray<LPod6>();
        for (int i = 0; i < arr.Length; i++)
        {
            var e = arr[i];
            e.V = e.V * 2.0f;
            arr[i] = e;
        }
    }
}
");
            _out.WriteLine(result.Cpp);
            string text = result.Cpp.Replace("\r\n", "\n");

            // 真拷贝不得出现；写回语句必须被省略（否则别名下是自赋值）
            Assert.DoesNotContain("auto e = arr_ptr[i];", text);
            Assert.DoesNotContain("arr_ptr[i] = e;", text);
            Assert.Contains("arr_ptr[i].V", text);
        }

        [Fact]
        public void SameColumnBoundBySecondLocal_WriteIsStillSeen()
        {
            // 独立验收反例（2026-09-26）：同一分量列被**两个局部名**绑定（都映射到
            // requiredComponentArrays[0]，即同一块内存）。旧实现按**局部名**统计写入
            // ⇒ 透过 `arr2` 的写看不见 ⇒ 别名读到被改写后的值（原生实测 4093/4093 错）。
            // ⚠ 写入必须来自**另一块内存**（`Src`：job 字段 NativeArray）才是真反例：
            //   `arr2[k] = e;`（同值自写）下别名其实是等价的。
            var result = GeneratorHarness.EmitFor(@"
using NativeTranspiler;
using EntJoy.ECS;
using EntJoy.Collections;

public struct LPod7 { public float V; }

[NativeTranspile]
public struct AliasTwoLocalsJob : IJobChunk
{
    public NativeArray<float> Result;
    public NativeArray<LPod7> Src;
    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
    {
        var arr = chunk.GetComponentDataNativeArray<LPod7>();
        var arr2 = chunk.GetComponentDataNativeArray<LPod7>();
        for (int k = 0; k < arr.Length; k++)
        {
            var e = arr[k];
            arr2[k] = Src[k];
            Result[k] = e.V;
        }
    }
}
");
            _out.WriteLine(result.Cpp);
            string text = result.Cpp.Replace("\r\n", "\n");

            Assert.Contains("e = arr_ptr[k];", text);
            Assert.Contains("Result_ptr[k] = e.V;", text);
        }

        [Fact]
        public void SameColumnBoundAfterAlias_IsNotAliased()
        {
            // 独立验收反例 F1（第二次验收）：同一分量列的第二个局部名声明在**别名之后**。
            // 别名判定在块入口执行，而局部名是惰性登记的 ⇒ 旧实现看不见这次写（原生 4093/4093 错）。
            // 修法：块入口先按语法**预扫描**整棵子树的 chunk 数组局部 → 存储身份，判定与翻译顺序无关。
            var result = GeneratorHarness.EmitFor(@"
using NativeTranspiler;
using EntJoy.ECS;
using EntJoy.Collections;

public struct LPod9 { public float V; }

[NativeTranspile]
public struct AliasThenBindJob : IJobChunk
{
    public NativeArray<float> Result;
    public NativeArray<LPod9> Src;
    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
    {
        var arr = chunk.GetComponentDataNativeArray<LPod9>();
        for (int k = 0; k < arr.Length; k++)
        {
            var e = arr[k];
            var arr2 = chunk.GetComponentDataNativeArray<LPod9>();
            arr2[k] = Src[k];
            Result[k] = e.V;
        }
    }
}
");
            _out.WriteLine(result.Cpp);
            string text = result.Cpp.Replace("\r\n", "\n");

            Assert.Contains("e = arr_ptr[k];", text);
            Assert.Contains("Result_ptr[k] = e.V;", text);
        }

        [Fact]
        public void SameColumnBoundInNestedBlock_IsNotAliased()
        {
            // 独立验收反例 F2：同一分量列的第二个局部名声明在**嵌套块**里
            // （块入口预扫描必须覆盖整棵子树，而不是只看直接语句）。
            var result = GeneratorHarness.EmitFor(@"
using NativeTranspiler;
using EntJoy.ECS;
using EntJoy.Collections;

public struct LPod10 { public float V; }

[NativeTranspile]
public struct AliasNestedBindJob : IJobChunk
{
    public NativeArray<float> Result;
    public NativeArray<LPod10> Src;
    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
    {
        var arr = chunk.GetComponentDataNativeArray<LPod10>();
        for (int k = 0; k < arr.Length; k++)
        {
            var e = arr[k];
            if (e.V > 0.0f)
            {
                var arr2 = chunk.GetComponentDataNativeArray<LPod10>();
                arr2[k] = Src[k];
            }
            Result[k] = e.V;
        }
    }
}
");
            _out.WriteLine(result.Cpp);
            string text = result.Cpp.Replace("\r\n", "\n");

            Assert.Contains("e = arr_ptr[k];", text);
            Assert.Contains("Result_ptr[k] = e.V;", text);
        }

        [Fact]
        public void ElementFieldWrite_IsTreatedAsArrayWrite()
        {
            // 同一类的第三个面：写的是**元素字段**（`arr[k].V = x`）。旧实现在这里只认 `arr[k] = …`，
            // 字段写不算"数组被改写" ⇒ 别名读到被改写的值（静默错值）。
            var result = GeneratorHarness.EmitFor(@"
using NativeTranspiler;
using EntJoy.ECS;
using EntJoy.Collections;

public struct LPod11 { public float V; }

[NativeTranspile]
public struct AliasFieldWriteJob : IJobChunk
{
    public NativeArray<float> Result;
    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
    {
        var arr = chunk.GetComponentDataNativeArray<LPod11>();
        for (int k = 0; k < arr.Length; k++)
        {
            var e = arr[k];
            arr[k].V = 9.0f;
            Result[k] = e.V;
        }
    }
}
");
            _out.WriteLine(result.Cpp);
            string text = result.Cpp.Replace("\r\n", "\n");

            Assert.Contains("e = arr_ptr[k];", text);
            Assert.Contains("Result_ptr[k] = e.V;", text);
        }

        [Fact]
        public void WritesInExpressionContexts_AreSeen()
        {
            // 独立验收（第三次）：写可以嵌在**任何**表达式上下文里，只扫 ExpressionStatement 会漏判。
            // 覆盖：调用实参 / 局部初始化器 / if 条件 / while 条件 / do 条件 / for 增量器 /
            //       for 初始化器 / 三元表达式 / 赋值链结果。
            var shapes = new (string name, string body)[]
            {
                ("invocation-arg", "Eat(arr[k] = Src[k]);"),
                ("local-initializer", "float t = arr[k] = 9.0f; sum += t;"),
                ("if-condition", "if ((arr[k] = 9.0f) > 0.0f) { sum += 1.0f; }"),
                ("while-condition", "while ((arr[k] = 9.0f) > 1.0f) { go = false; }"),
                ("do-condition", "do { } while ((arr[k] = 9.0f) > 1.0f);"),
                ("for-incrementor", "for (int j = 0; j < 1; arr[k] = 9.0f, j++) { sum += 1.0f; }"),
                ("for-initializer", "for (arr[k] = 9.0f; false;) { }"),
                ("ternary", "sum += (go ? (arr[k] = 9.0f) : arr[k]);"),
                ("assign-chain", "float t = (arr[k] = 9.0f) + 1.0f; sum += t;"),
            };

            foreach (var (name, body) in shapes)
            {
                var result = GeneratorHarness.EmitFor(@"
using NativeTranspiler;
using EntJoy.ECS;
using EntJoy.Collections;

public static class HH { public static void Eat(float v) { } }

[NativeTranspile]
public struct LExprCtxJob : IJobChunk
{
    public NativeArray<float> Result;
    public NativeArray<float> Src;
    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
    {
        var arr = chunk.GetComponentDataNativeArray<float>();
        var sum = 0.0f;
        var go = true;
        for (int k = 0; k < arr.Length; k++)
        {
            var e = arr[k];
            " + body + @"
            Result[k] = e + sum;
        }
    }
}
");
                string text = result.Cpp.Replace("\r\n", "\n");
                Assert.True(text.Contains("LExprCtxJob_Execute"),
                    GeneratorHarness.Fail(result, $"[{name}] 形状没有被生成 ⇒ 断言是空跑"));
                Assert.True(text.Contains("float e = arr_ptr[k];"),
                    GeneratorHarness.Fail(result, $"[{name}] 同一列在表达式里被写 ⇒ 必须是真拷贝：{body}"));
                Assert.True(text.Contains("Result_ptr[k] = e + sum;"),
                    GeneratorHarness.Fail(result, $"[{name}] 必须读拷贝 e：{body}"));
            }
        }

        [Fact]
        public void RefAliasToSameColumn_IsNotAliased()
        {
            // 独立验收（第三次）：`ref r = ref s[k]` 是**指向该列的引用别名**，之后 `r = x` 改的是同一块内存。
            // 引用别名的写入目标是普通局部，按名字/存储身份都认不出来 ⇒ 必须把"引用逃逸"本身当成写。
            var result = GeneratorHarness.EmitFor(@"
using NativeTranspiler;
using EntJoy.ECS;
using EntJoy.Collections;

public struct LRefPod { public float V; }

[NativeTranspile]
public struct RefAliasJob : IJobChunk
{
    public NativeArray<float> Result;
    public NativeArray<LRefPod> Src;
    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
    {
        var arr = chunk.GetComponentDataNativeArray<LRefPod>();
        var s = chunk.GetComponentDataSpan<LRefPod>();
        for (int k = 0; k < arr.Length; k++)
        {
            var e = arr[k];
            ref LRefPod r = ref s[k];
            r = Src[k];
            Result[k] = e.V;
        }
    }
}
");
            _out.WriteLine(result.Cpp);
            string text = result.Cpp.Replace("\r\n", "\n");

            Assert.True(text.Contains("LRefPod e = arr_ptr[k];"),
                GeneratorHarness.Fail(result, "引用别名会写到同一列 ⇒ 必须退回真拷贝"));
            Assert.Contains("Result_ptr[k] = e.V;", text);
        }

        [Fact]
        public void DirectInvocationBaseWrite_IsSeen()
        {
            // 独立验收（第三次，C10）：元素访问的基是**直接调用**
            // `chunk.GetComponentDataSpan<T>()[k] = x`（没有局部名）。旧实现只认"局部名[i]"
            // ⇒ 这次写完全看不见 ⇒ 别名读到被改写的值（静默错值）。
            var result = GeneratorHarness.EmitFor(@"
using NativeTranspiler;
using EntJoy.ECS;
using EntJoy.Collections;

public struct LInvPod { public float V; }

[NativeTranspile]
public struct InvocationBaseJob : IJobChunk
{
    public NativeArray<float> Result;
    public NativeArray<LInvPod> Src;
    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
    {
        var arr = chunk.GetComponentDataNativeArray<LInvPod>();
        for (int k = 0; k < arr.Length; k++)
        {
            var e = arr[k];
            chunk.GetComponentDataSpan<LInvPod>()[k] = Src[k];
            Result[k] = e.V;
        }
    }
}
");
            _out.WriteLine(result.Cpp);
            string text = result.Cpp.Replace("\r\n", "\n");

            Assert.True(text.Contains("LInvPod e = arr_ptr[k];"),
                GeneratorHarness.Fail(result, "直接调用为基的写也改同一列 ⇒ 必须退回真拷贝"));
            Assert.Contains("Result_ptr[k] = e.V;", text);
        }

        [Fact]
        public void WriteContextMatrix_NoAliasWhenColumnWritten()
        {
            // 系统性矩阵（枚举，不是抽样）：{写上下文} × {同一列的两个绑定形态} 全部必须退回真拷贝；
            // 无写（只读）必须保留别名。写上下文这一维是三轮验收里出洞最多的维（嵌在表达式里的写）。
            // 说明：这里只做**发射文本**断言（不编译 C++），因为不变量是"有写 ⇒ 必须有拷贝声明"。
            string[] writeContexts =
            {
                "{T} = Src[k];",
                "Eat({T} = Src[k]);",
                "float t = {T} = Src[k]; sum += t;",
                "if (({T} = Src[k]) > 0.0f) { sum += 1.0f; }",
                "while (({T} = Src[k]) > 1.0f) { go = false; }",
                "do { } while (({T} = Src[k]) > 1.0f);",
                "for (int j = 0; j < 1; {T} = Src[k], j++) { sum += 1.0f; }",
                "for ({T} = Src[k]; false;) { }",
                "sum += (go ? ({T} = Src[k]) : {T});",
                "if (go) { {T} = Src[k]; } else { sum += 1.0f; }",
                "{T}++;",
            };
            string[] bindings =
            {
                "same-local",   // 写通过别名自己的局部名
                "second-local", // 写通过同一列的第二个局部名（同块、别名之前声明）
            };

            foreach (var binding in bindings)
            {
                foreach (var context in writeContexts)
                {
                    string target = binding == "same-local" ? "arr[k]" : "arr2[k]";
                    string secondDecl = binding == "same-local" ? "" : "var arr2 = chunk.GetComponentDataNativeArray<float>();\n        ";
                    string body = context.Replace("{T}", target);

                    var result = GeneratorHarness.EmitFor(@"
using NativeTranspiler;
using EntJoy.ECS;
using EntJoy.Collections;

public static class Eat0 { public static void Eat(float v) { } }

[NativeTranspile]
public struct LMatrixJob : IJobChunk
{
    public NativeArray<float> Result;
    public NativeArray<float> Src;
    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
    {
        var arr = chunk.GetComponentDataNativeArray<float>();
        " + secondDecl + @"var sum = 0.0f;
        var go = true;
        for (int k = 0; k < arr.Length; k++)
        {
            var e = arr[k];
            " + body + @"
            Result[k] = e + sum;
        }
    }
}
");
                    string text = result.Cpp.Replace("\r\n", "\n");
                    string label = $"[{binding} / {context}]";

                    Assert.True(text.Contains("LMatrixJob_Execute"),
                        GeneratorHarness.Fail(result, $"{label} 形状没有被生成 ⇒ 断言是空跑"));
                    Assert.True(text.Contains("float e = arr_ptr[k];"),
                        GeneratorHarness.Fail(result, $"{label} 同一列被写 ⇒ 必须是真拷贝"));
                    Assert.True(text.Contains("Result_ptr[k] = e + sum;"),
                        GeneratorHarness.Fail(result, $"{label} 必须读拷贝 e"));
                }

                // 只读控制：同一绑定形态下**没有**写 ⇒ 别名必须保留（不得一律退拷贝）
                string controlTarget = binding == "same-local" ? "arr[k]" : "arr2[k]";
                string controlSecond = binding == "same-local" ? "" : "var arr2 = chunk.GetComponentDataNativeArray<float>();\n        ";
                var control = GeneratorHarness.EmitFor(@"
using NativeTranspiler;
using EntJoy.ECS;
using EntJoy.Collections;

[NativeTranspile]
public struct LMatrixRoJob : IJobChunk
{
    public NativeArray<float> Result;
    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
    {
        var arr = chunk.GetComponentDataNativeArray<float>();
        " + controlSecond + @"for (int k = 0; k < arr.Length; k++)
        {
            var e = arr[k];
            Result[k] = e + 1.0f;
        }
    }
}
");
                string controlText = control.Cpp.Replace("\r\n", "\n");
                Assert.True(controlText.Contains("Result_ptr[k] = arr_ptr[k] + 1.0f;"),
                    GeneratorHarness.Fail(control, $"[{binding}] 只读形态必须保留别名（否则是无谓的性能回退）"));
            }
        }

        [Fact]
        public void ArrayWrittenInsideIf_KeepsRealCopyOfElement()
        {
            var result = GeneratorHarness.EmitFor(@"
using NativeTranspiler;
using EntJoy.ECS;
using EntJoy.Collections;

public struct LPod { public float V; }

[NativeTranspile]
public struct AliasInsideIfJob : IJobChunk
{
    public NativeArray<float> Result;
    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
    {
        var arr = chunk.GetComponentDataNativeArray<LPod>();
        var other = chunk.GetComponentDataNativeArray<LPod>();
        for (int i = 0; i < arr.Length; i++)
        {
            var e = arr[i];
            if (e.V > 0.0f)
            {
                arr[i] = other[i];
            }
            Result[i] = e.V;
        }
    }
}
");
            _out.WriteLine(result.DiagnosticSummary);
            _out.WriteLine(result.Cpp);
            string text = result.Cpp.Replace("\r\n", "\n");

            // 缺陷点：`e` 被别名成 `arr_ptr[i]` ⇒ 读到的是 if 里刚写过的值（C# 是值拷贝）
            Assert.DoesNotContain("arr_ptr[i].V", text);

            // 正确形态：保留真拷贝 + 后续用 e
            Assert.Contains("e = arr_ptr[i];", text);
            Assert.Contains("= e.V;", text);
        }

        [Fact]
        public void ArrayWrittenInsideFor_KeepsRealCopyOfElement()
        {
            var result = GeneratorHarness.EmitFor(@"
using NativeTranspiler;
using EntJoy.ECS;
using EntJoy.Collections;

public struct LPod2 { public float V; }

[NativeTranspile]
public struct AliasInsideForJob : IJobChunk
{
    public NativeArray<float> Result;
    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
    {
        var arr = chunk.GetComponentDataNativeArray<LPod2>();
        var other = chunk.GetComponentDataNativeArray<LPod2>();
        for (int i = 0; i < arr.Length; i++)
        {
            var e = arr[i];
            for (int k = 0; k < 2; k = k + 1)
            {
                arr[i] = other[i];
            }
            Result[i] = e.V;
        }
    }
}
");
            _out.WriteLine(result.Cpp);
            string text = result.Cpp.Replace("\r\n", "\n");
            Assert.DoesNotContain("arr_ptr[i].V", text);
            Assert.Contains("e = arr_ptr[i];", text);
        }

        [Fact]
        public void ReadOnlySource_StillUsesAlias()
        {
            // 反向守卫：数组在本块内确实只读时，别名优化必须保留（不得一律退回拷贝）。
            var result = GeneratorHarness.EmitFor(@"
using NativeTranspiler;
using EntJoy.ECS;
using EntJoy.Collections;

public struct LPod3 { public float V; }

[NativeTranspile]
public struct ReadOnlyAliasJob : IJobChunk
{
    public NativeArray<float> Result;
    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
    {
        var arr = chunk.GetComponentDataNativeArray<LPod3>();
        for (int i = 0; i < arr.Length; i++)
        {
            var e = arr[i];
            Result[i] = e.V;
        }
    }
}
");
            _out.WriteLine(result.Cpp);
            string text = result.Cpp.Replace("\r\n", "\n");
            Assert.Contains("Result_ptr[i] = arr_ptr[i].V;", text);
        }

        [Fact]
        public void IndexWithSideEffects_IsNotAliased()
        {
            var result = GeneratorHarness.EmitFor(@"
using NativeTranspiler;
using EntJoy.ECS;
using EntJoy.Collections;

public struct LPod4 { public float V; }

[NativeTranspile]
public struct SideEffectIndexJob : IJobChunk
{
    public NativeArray<float> Result;
    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
    {
        var arr = chunk.GetComponentDataNativeArray<LPod4>();
        int cursor = 0;
        for (int i = 0; i < 4; i++)
        {
            var e = arr[cursor++];
            Result[i] = e.V;
        }
    }
}
");
            _out.WriteLine(result.Cpp);
            string text = result.Cpp.Replace("\r\n", "\n");

            // 别名会把 `cursor++` 推迟到每次使用 ⇒ 副作用重复求值
            Assert.DoesNotContain("arr_ptr[cursor++].V", text);
            Assert.Contains("e = arr_ptr[cursor++];", text);
        }
    }
}
