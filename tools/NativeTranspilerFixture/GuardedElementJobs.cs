using EntJoy.Collections;
using EntJoy.JobSystem;
using NativeTranspiler;

namespace NativeTranspilerFixture;

[NativeTranspile]
public struct GuardedElementJob : IJobParallelFor
{
    public NativeArray<int> Before, Values, After;
    public int Limit;

    public void Execute(int index)
    {
        Before[index] = index + 1;
        if (index < Limit && (index & 1) == 0) Values[index] = 7;
        After[index] = index + 2;
    }
}

[NativeTranspile]
public struct GuardedVariantJob : IJobParallelFor
{
    public NativeArray<int> Before, Values, After;
    public int Limit;
    public bool Enabled;

    public void Execute(int index)
    {
        Before[index] = index + 1;
        if (index < Limit && Enabled) Values[index] = 7;
        After[index] = index + 2;
    }
}

// 纯守卫体：没有体外副作用，也必须与普通 C# 的条件执行结果一致。
[NativeTranspile]
public struct GuardedStoreJob : IJobParallelFor
{
    public NativeArray<int> Input, Output;
    public int Limit;

    public void Execute(int index)
    {
        if (index < Limit && Input[index] > 0) Output[index] = Input[index] * 3 + 1;
    }
}

public static class GuardedElementFixture
{
    public static void Run()
    {
        int mismatches = 0;
        using var before = new NativeArray<int>(19);
        using var values = new NativeArray<int>(19);
        using var after = new NativeArray<int>(19);
        foreach (int limit in new[] { -1, 0, 7, 19, 23 })
        foreach (int batch in new[] { 1, 8, 32 })
        for (int path = 0; path < 2; path++)
        {
            Clear();
            var job = new GuardedElementJob { Before = before, Values = values, After = after, Limit = limit };
            if (path == 0) job.Run(before.Length); else job.Schedule(before.Length, batch).Complete();
            Check(limit, false, false);

            foreach (bool enabled in new[] { false, true })
            {
                Clear();
                var variant = new GuardedVariantJob { Before = before, Values = values, After = after, Limit = limit, Enabled = enabled };
                if (path == 0) variant.Run(before.Length); else variant.Schedule(before.Length, batch).Complete();
                Check(limit, true, enabled);
            }

            Clear();
            var store = new GuardedStoreJob { Input = before, Output = values, Limit = limit };
            for (int i = 0; i < before.Length; i++) store.Input[i] = i - 5;
            if (path == 0) store.Run(before.Length); else store.Schedule(before.Length, batch).Complete();
            for (int i = 0; i < before.Length; i++)
                if (values[i] != (i < limit && i > 5 ? (i - 5) * 3 + 1 : 0)) mismatches++;
        }
        if (mismatches != 0) throw new Exception($"Guarded Execute skipped or changed {mismatches} element results.");
        Console.WriteLine("PASS: guarded element bodies preserve before/after side effects, bool variants, Run and Schedule tails.");

        void Clear()
        {
            // 避免通过 using 变量的可写索引器赋值；直接初始化独立测试缓冲。
            unsafe
            {
                new Span<int>(before.GetUnsafePtr(), before.Length).Clear();
                new Span<int>(values.GetUnsafePtr(), values.Length).Clear();
                new Span<int>(after.GetUnsafePtr(), after.Length).Clear();
            }
        }

        void Check(int limit, bool variant, bool enabled)
        {
            for (int i = 0; i < before.Length; i++)
            {
                bool write = i < limit && (variant ? enabled : (i & 1) == 0);
                if (before[i] != i + 1 || after[i] != i + 2 || values[i] != (write ? 7 : 0)) mismatches++;
            }
        }
    }
}
