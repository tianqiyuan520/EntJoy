namespace EntJoy.ECS
{
    /// <summary>
    /// Prefab 模板标记组件（对齐 Unity DOTS 的 Unity.Entities.Prefab）。
    /// 含此组件的实体是「模板」，默认被所有查询排除（除非显式 WithAll&lt;Prefab&gt;），
    /// 用于 SpawnFrom 批量实例化。
    /// </summary>
    public struct Prefab : IComponentData
    {
    }
}
