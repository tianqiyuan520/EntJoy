using EntJoy.JobSystem;
using EntJoy.Collections;

namespace JobLibsBenchmark
{
    // =====================================================================
    // NativeTranspiler 对比基准 —— 被 [NativeTranspile] 标记的 job。
    //
    // 这些 struct 会由 NativeTranspiler 源生成器翻译成 C++/ISPC，并生成
    // NativeTranspiler.Bindings.g.cs（含 Schedule 扩展方法），最终在
    // NativeTranspiled.dll 中经原生 Adapter 直跑（原生→原生，绕过托管委托）。
    //
    // ⚠ 两个硬性约束（踩坑备忘）：
    //  1) Execute 必须用「块体」写法（{ ... }），不能写成表达式体（=> ...）。
    //     源生成器通过 `methodSyntax.Body` 取方法体，表达式体 Body 为 null →
    //     会生成 "// Error: no Execute body found." 的占位（ISPC 头为空、链接失败）。
    //  2) 字段必须是非托管类型：标量/枚举/指针/EntJoy 容器(NativeArray<T>/
    //     NativeList<T>/UnsafeList<T>)/float2,int2,uint2/纯值 struct。
    //     不能用 int[]，必须用 NativeArray<int>（固定缓冲才能真正被 C++/ISPC 直读）。
    //  3) ISPC 后端不支持 64 位 long/int64（会把 long 误翻译成非法 ISPC），重算用 int。
    // =====================================================================

    // ── S1: 分片加法 ──
    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Cpp)]
    public struct NativeAddCpp : IJobParallelFor
    {
        public NativeArray<int> Values;
        public void Execute(int index)
        {
            Values[index] = Values[index] + 1;
        }
    }

    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Cpp,
        AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
    public struct NativeAddAutoSIMD : IJobParallelFor
    {
        public NativeArray<int> Values;
        public void Execute(int index)
        {
            Values[index] = Values[index] + 1;
        }
    }

    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc, MathLib = NativeTranspiler.IspcMathLib.fast)]
    public struct NativeAddIspc : IJobParallelFor
    {
        public NativeArray<int> Values;
        public void Execute(int index)
        {
            Values[index] = Values[index] + 1;
        }
    }

    // ── S2: 空任务 ──
    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Cpp)]
    public struct NativeEmptyCpp : IJobParallelFor
    {
        public void Execute(int index) { }
    }

    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc, MathLib = NativeTranspiler.IspcMathLib.fast)]
    public struct NativeEmptyIspc : IJobParallelFor
    {
        public void Execute(int index) { }
    }

    // ── S3: 依赖链 (+1 → x2 → -3) ──
    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Cpp)]
    public struct NativeChainCpp1 : IJobParallelFor
    {
        public NativeArray<int> Values;
        public void Execute(int index) { Values[index] = Values[index] + 1; }
    }

    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Cpp)]
    public struct NativeChainCpp2 : IJobParallelFor
    {
        public NativeArray<int> Values;
        public void Execute(int index) { Values[index] = Values[index] * 2; }
    }

    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Cpp)]
    public struct NativeChainCpp3 : IJobParallelFor
    {
        public NativeArray<int> Values;
        public void Execute(int index) { Values[index] = Values[index] - 3; }
    }

    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Cpp,
        AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
    public struct NativeChainAutoSIMD1 : IJobParallelFor
    {
        public NativeArray<int> Values;
        public void Execute(int index) { Values[index] = Values[index] + 1; }
    }

    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Cpp,
        AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
    public struct NativeChainAutoSIMD2 : IJobParallelFor
    {
        public NativeArray<int> Values;
        public void Execute(int index) { Values[index] = Values[index] * 2; }
    }

    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Cpp,
        AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
    public struct NativeChainAutoSIMD3 : IJobParallelFor
    {
        public NativeArray<int> Values;
        public void Execute(int index) { Values[index] = Values[index] - 3; }
    }

    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc, MathLib = NativeTranspiler.IspcMathLib.fast)]
    public struct NativeChainIspc1 : IJobParallelFor
    {
        public NativeArray<int> Values;
        public void Execute(int index) { Values[index] = Values[index] + 1; }
    }

    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc, MathLib = NativeTranspiler.IspcMathLib.fast)]
    public struct NativeChainIspc2 : IJobParallelFor
    {
        public NativeArray<int> Values;
        public void Execute(int index) { Values[index] = Values[index] * 2; }
    }

    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc, MathLib = NativeTranspiler.IspcMathLib.fast)]
    public struct NativeChainIspc3 : IJobParallelFor
    {
        public NativeArray<int> Values;
        public void Execute(int index) { Values[index] = Values[index] - 3; }
    }

    // ── S5: 高竞争重计算（每元素 1000 次内循环）──
    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Cpp)]
    public struct NativeHeavyCpp : IJobParallelFor
    {
        public NativeArray<int> Results;
        public void Execute(int index)
        {
            int sum = 0;
            for (int j = 0; j < 1000; j++) sum += index * j;
            Results[index] = sum;
        }
    }

    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc, MathLib = NativeTranspiler.IspcMathLib.fast)]
    public struct NativeHeavyIspc : IJobParallelFor
    {
        public NativeArray<int> Results;
        public void Execute(int index)
        {
            int sum = 0;
            for (int j = 0; j < 1000; j++) sum += index * j;
            Results[index] = sum;
        }
    }

    // ── S6: 控制流 + 重运算（LCG 伪随机 → 数据依赖分支，编译器无法代数简化）──
    // 每元素 1000 次 LCG 迭代 + 分支累加。只用 uint/int（ISPC 不支持 int64）。
    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Cpp)]
    public struct NativeCtrlCpp : IJobParallelFor
    {
        public NativeArray<int> Results;
        public void Execute(int index)
        {
            int sum = 0;
            uint x = (uint)(index * 2654435761u) + 1u;
            for (int j = 0; j < 1000; j++)
            {
                x = x * 1664525u + 1013904223u;
                uint r = x % 13u;
                if (r < 4u) sum += (int)x;
                else if (r < 8u) sum ^= (int)x;
                else sum -= (int)(x >> 3);
                if ((x & 7u) == 0u) sum += j;
            }
            Results[index] = sum;
        }
    }

    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc, MathLib = NativeTranspiler.IspcMathLib.fast)]
    public struct NativeCtrlIspc : IJobParallelFor
    {
        public NativeArray<int> Results;
        public void Execute(int index)
        {
            int sum = 0;
            uint x = (uint)(index * 2654435761u) + 1u;
            for (int j = 0; j < 1000; j++)
            {
                x = x * 1664525u + 1013904223u;
                uint r = x % 13u;
                if (r < 4u) sum += (int)x;
                else if (r < 8u) sum ^= (int)x;
                else sum -= (int)(x >> 3);
                if ((x & 7u) == 0u) sum += j;
            }
            Results[index] = sum;
        }
    }

    // ── S5 AutoSIMD：C++ 编译器 SIMD 向量化（同一 scalar 代码 + AutoSIMD 标记）──
    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Cpp,
        AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
    public struct NativeHeavyAutoSIMD : IJobParallelFor
    {
        public NativeArray<int> Results;
        public void Execute(int index)
        {
            int sum = 0;
            for (int j = 0; j < 1000; j++) sum += index * j;
            Results[index] = sum;
        }
    }

        // ── S6 AutoSIMD：控制流 + 重运算 + SIMD 向量化 ──
    [NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Cpp,
        AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
    public struct NativeCtrlAutoSIMD : IJobParallelFor
    {
        public NativeArray<int> Results;
        public void Execute(int index)
        {
            int sum = 0;
            uint x = (uint)(index * 2654435761u) + 1u;
            for (int j = 0; j < 1000; j++)
            {
                x = x * 1664525u + 1013904223u;
                uint r = x % 13u;
                if (r < 4u) sum += (int)x;
                else if (r < 8u) sum ^= (int)x;
                else sum -= (int)(x >> 3);
                if ((x & 7u) == 0u) sum += j;
            }
            Results[index] = sum;
        }
    }
}
