using EntJoy.ECS;
using EntJoy.JobSystem;
using NativeTranspiler;

[NativeTranspile(Target = BackendTarget.Cpp)]
public struct EntDependencyCpp : IJobEntity
{
    public void Execute(ref A a, in B b)
    {
        int value = a.Value;
        value = ((value * 33) ^ b.Value) & 65535;
        value = ((value * 33) ^ b.Value) & 65535;
        value = ((value * 33) ^ b.Value) & 65535;
        value = ((value * 33) ^ b.Value) & 65535;
        a.Value = value;
    }
}

public static class DependencyComparison
{
    public static void Run()
    {
        const int count = 100_000;
        if (NativeJobScheduler.GetNativeExportPtr("Get_SharpNative_Job__global_namespace__EntDependencyCpp_Execute_AdapterPtr") == IntPtr.Zero)
            throw new InvalidOperationException("Native dependency job export missing");
        using var world = new EntJoy.ECS.World("dependency-comparison");
        for (int i = 0; i < count; i++)
        {
            var entity = world.EntityManager.NewEntity(typeof(A), typeof(B));
            world.EntityManager.Set(entity, new B { Value = 1 });
        }

        var query = new QueryBuilder().WithAll<A, B>();
        void Execute() => new EntDependencyCpp().Run(query, world);
        var times = ComparisonTiming.Measure(Execute);
        int expected = ExpectedValue();
        int actualCount = 0;
        foreach (var item in world.Query<A, B>())
        {
            if (item.Comp0.Value != expected || item.Comp1.Value != 1)
                throw new InvalidOperationException("Dependency result mismatch");
            actualCount++;
        }
        if (actualCount != count) throw new InvalidOperationException("Dependency entity count mismatch");
        int chunks = 0;
        foreach (var chunk in world.QueryChunks<A, B>()) chunks++;
        Console.WriteLine($"ENTJOY_DEPENDENCY chunks={chunks} expected={expected} {ComparisonTiming.Describe(times)}");
    }

    private static int ExpectedValue()
    {
        int value = 0;
        int steps = 4 * (ComparisonTiming.Warmup + ComparisonTiming.Samples * ComparisonTiming.Repetitions);
        for (int i = 0; i < steps; i++) value = ((value * 33) ^ 1) & 65535;
        return value;
    }
}
