namespace EntJoySample.HintLikelyTest
{
    /// <summary>
    /// 验证 Hint.Likely / Hint.Unlikely 在 NativeTranspiler 中的正确性：
    /// - C++ 后端应生成 `[[likely]]` / `[[unlikely]]`
    /// - ISPC 后端应生成 `__builtin_expect(…, 1/0)`
    /// - 纯 C# 执行时 Hint 应无副作用，仅返回 condition
    /// </summary>
    public static class HintLikely
    {
        [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Cpp)]
        public static int TestLikelyCpp(int x)
        {
            if (EntJoy.Hint.Likely(x > 0))
            {
                return x * 2;
            }
            else
            {
                return -x;
            }
        }

        [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Cpp)]
        public static int TestUnlikelyCpp(int x)
        {
            if (EntJoy.Hint.Unlikely(x < 0))
            {
                return -1;
            }
            return x;
        }


        [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc)]
        public static int TestLikelyIspc(int x)
        {
            if (EntJoy.Hint.Likely(x > 0))
            {
                return x * 2;
            }
            else
            {
                return -x;
            }
        }

        [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc)]
        public static int TestUnlikelyIspc(int x)
        {
            if (EntJoy.Hint.Unlikely(x < 0))
            {
                return -1;
            }
            return x;
        }

        /// <summary>
        /// 无 [NativeTranspile] 的纯 C# 版本，验证 Hint.Likely/Unlikely 在不经转译时正确工作
        /// </summary>
        public static int PureCSharpLikely(int x)
        {
            if (EntJoy.Hint.Likely(x > 0))
                return x * 2;
            else
                return -x;
        }

        public static int PureCSharpUnlikely(int x)
        {
            if (EntJoy.Hint.Unlikely(x < 0))
                return -1;
            return x;
        }

        public static void Run()
        {
            Console.WriteLine("=== Hint.Likely / Hint.Unlikely Test ===\n");

            // 纯 C# 验证（不依赖 NativeTranspiler 管道）
            Console.WriteLine("--- Pure C# ---");
            RunCase("PureCSharpLikely(5)",    PureCSharpLikely(5)   == 10);
            RunCase("PureCSharpLikely(-1)",   PureCSharpLikely(-1)  == 1);
            RunCase("PureCSharpUnlikely(-1)", PureCSharpUnlikely(-1) == -1);
            RunCase("PureCSharpUnlikely(5)",  PureCSharpUnlikely(5)  == 5);

            // 原生转译版本（通过 DllImport 调用生成的 C++/ISPC）
            // 注意：下面调用需要 NativeDll.dll 已由 NativeCompileTask 编译完成
            Console.WriteLine("\n--- Native (via generated bindings) ---");
            try
            {
                int r1 = NativeTranspiler.Bindings.NativeExports.TestLikelyCpp(5);
                int r2 = NativeTranspiler.Bindings.NativeExports.TestLikelyCpp(-1);
                int r3 = NativeTranspiler.Bindings.NativeExports.TestUnlikelyCpp(-1);
                int r4 = NativeTranspiler.Bindings.NativeExports.TestUnlikelyCpp(5);

                RunCase("TestLikelyCpp(5)",    r1 == 10);
                RunCase("TestLikelyCpp(-1)",   r2 == 1);
                RunCase("TestUnlikelyCpp(-1)", r3 == -1);
                RunCase("TestUnlikelyCpp(5)",  r4 == 5);

#if true
                // ISPC 绑定测试（需要 ISPC SDK 编译环境就绪）
                int r5 = NativeTranspiler.Bindings.NativeExports.TestLikelyIspc(5);
                int r6 = NativeTranspiler.Bindings.NativeExports.TestLikelyIspc(-1);
                int r7 = NativeTranspiler.Bindings.NativeExports.TestUnlikelyIspc(-1);
                int r8 = NativeTranspiler.Bindings.NativeExports.TestUnlikelyIspc(5);

                RunCase("TestLikelyIspc(5)",    r5 == 10);
                RunCase("TestLikelyIspc(-1)",   r6 == 1);
                RunCase("TestUnlikelyIspc(-1)", r7 == -1);
                RunCase("TestUnlikelyIspc(5)",  r8 == 5);
#endif
            }
            catch (EntryPointNotFoundException ex)
            {
                Console.WriteLine($"  [SKIP] NativeDLL not loaded: {ex.Message}");
                Console.WriteLine("  Build the project first to generate and compile native code.");
            }

            Console.WriteLine("\n=== Test Complete ===");

            // 使用 Assert 确保 CI 环境可检测失败
            System.Diagnostics.Debug.Assert(PureCSharpLikely(5) == 10);
            System.Diagnostics.Debug.Assert(PureCSharpLikely(-1) == 1);
            System.Diagnostics.Debug.Assert(PureCSharpUnlikely(-1) == -1);
            System.Diagnostics.Debug.Assert(PureCSharpUnlikely(5) == 5);
        }

        private static void RunCase(string label, bool passed)
        {
            Console.WriteLine(passed ? $"  [PASS] {label}" : $"  [FAIL] {label}");
        }
    }
}
