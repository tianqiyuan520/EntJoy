using Microsoft.CodeAnalysis;

namespace EntJoy.ECS.SourceGenerator
{
    /// <summary>
    /// ECS 源生成器总入口（复合生成器）。
    /// 聚合/调度所有 ECS 相关子生成器；后续新增 ECS 生成器在此登记即可，
    /// 各子生成器内部各自通过 IIncrementalGenerator 注册自己的增量管线和 SourceOutput。
    /// </summary>
    [Generator]
    public sealed class ECSSourceGenerator : IIncrementalGenerator
    {
        public void Initialize(IncrementalGeneratorInitializationContext context)
        {
            // 登记并调度各 ECS 子生成器
            new IJobEntitySourceGenerator().Initialize(context);
            new QueryTupleSourceGenerator().Initialize(context);
        }
    }
}
