using System;

namespace EntJoy.ECS
{
    public unsafe partial class EntityManager
    {
        // 每个 Manager 独立的内部测试入口，不作为用户可配置的关系策略。
        internal Action<string> RelationshipFailureInjector;
        internal void CheckRelationshipPreparation(string checkpoint) => RelationshipFailureInjector?.Invoke(checkpoint);
    }
}
