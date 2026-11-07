using EntJoy.JobSystem;
using EntJoySample.ECS;

namespace EntJoySample.Relationship;

public static class Program
{
    public static int Main()
    {
        JobScheduler.Initialize(2);
        try
        {
            if (!JobScheduler.IsNative)
                throw new InvalidOperationException("This sample requires the built NativeDll and NativeTranspiled libraries for its C++/ISPC scenarios.");
            Console.WriteLine($"Relationship scenarios; scheduler={(JobScheduler.IsNative ? "Native" : "Managed")}");
            Console.WriteLine("Single target: N:1. ExclusiveRelation: 1:1. MultiRelation: multiple targets.\n");
            SingleTargetScenarios.Run();
            RelationScenarioDemo.Run();
            MultiTargetScenarios.Run();
            EdgeDataScenario.Run();
            Console.WriteLine("PASS: all Relationship scenarios.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine($"FAIL: {error}");
            return 1;
        }
        finally { JobScheduler.Shutdown(); }
    }
}
