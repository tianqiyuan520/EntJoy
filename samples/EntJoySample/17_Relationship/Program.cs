using EntJoy.JobSystem;
using EntJoySample.ECS;

namespace EntJoySample.Relationship;

public static class Program
{
    public static int Main(string[] args)
    {
        JobScheduler.Initialize(2);
        try
        {
            // 默认运行五个基础场景；传入 --advanced 时，额外演示 Job、延迟修改、复制和快照。
            bool advanced = args.Contains("--advanced");
            // 进阶场景会运行 C++/ISPC Job，需要先构建原生库。
            if (advanced && !JobScheduler.IsNative)
                throw new InvalidOperationException("This sample requires the built NativeDll and NativeTranspiled libraries for its C++/ISPC scenarios.");
            Console.WriteLine("ECS 关系：谁属于谁，谁可以连接多个目标。\n");
            Console.WriteLine("所有规则用 [RelationshipComponent(...)] 声明，普通关系只需实现 IRelationComponent。\n");
            SingleTargetScenarios.Run(advanced);
            if (advanced) RelationScenarioDemo.Run();
            MultiTargetScenarios.Run(advanced);
            EdgeDataScenario.Run(advanced);
            Console.WriteLine("PASS：所有关系示例验证通过。用 --advanced 运行 Job、ECB、复制和快照示例。");
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
