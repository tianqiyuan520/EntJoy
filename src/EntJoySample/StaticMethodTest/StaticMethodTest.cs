//using EntJoy.Collections;
//using System.Diagnostics;
//using System.Runtime.CompilerServices;

//public partial class StaticMethodTest
//{
//    [NativeTranspiler.NativeTranspile]
//    public static long Cal(long a)
//    {
//        for (int i = 1; i <= 10000000; i++) a += i;
//        return a;
//    }

//    public unsafe static void Main()
//    {
//        // 预热 JIT 和原生库
//        WarmUp();

//        const int iterations = 100;
//        double totalNative = 0, totalCSharp = 0;

//        for (int round = 0; round < iterations; round++)
//        {
//            long resultNative = 0, resultCSharp = 0;

//            // 原生转译版本
//            var swNative = Stopwatch.StartNew();
//            resultNative = NativeTranspiler.Bindings.NativeExports.Cal(0);
//            swNative.Stop();
//            totalNative += swNative.Elapsed.TotalMilliseconds;

//            // 纯 C# 版本（防止优化）
//            var swCSharp = Stopwatch.StartNew();
//            long a = 0;
//            for (int i = 1; i <= 10000000; i++) a += i;
//            resultCSharp = a;
//            swCSharp.Stop();
//            totalCSharp += swCSharp.Elapsed.TotalMilliseconds;

//            // 强制使用计算结果，防止编译器优化掉整个循环
//            if (resultNative != resultCSharp)
//                Console.WriteLine($"Error: results mismatch!\nres: {resultNative} - {resultCSharp}");

//            // 交替执行顺序，消除缓存预热偏差
//            if (round % 2 == 1)
//            {
//                // 下一轮先测C#再测原生
//            }
//        }

//        Console.WriteLine($"Native average: {totalNative / iterations:F3} ms");
//        Console.WriteLine($"C# average:     {totalCSharp / iterations:F3} ms");
//        Console.WriteLine($"Native is {totalCSharp / totalNative:F2}x faster than C#");

//        Console.ReadLine();
//    }

//    [MethodImpl(MethodImplOptions.NoInlining)]
//    private static void WarmUp()
//    {
//        // 触发 JIT 编译
//        long dummy = 0;
//        for (int i = 1; i <= 10000; i++) dummy += i;
//        // 触发原生库加载
//        NativeTranspiler.Bindings.NativeExports.Cal(0);
//    }
//}