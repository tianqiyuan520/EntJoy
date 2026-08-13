using System;

namespace EntJoy
{
    /// <summary>
    /// HotField 实体标记接口：生成器产出的 HotField 实体（class 稀疏句柄 / struct 密集句柄）都会实现它。
    /// 只声明实体契约（绑定/生命周期），不包含业务方法（如 Update 只是测试场景，不属于契约）。
    /// </summary>
    public interface IHotFieldEntity : IDisposable
    {
        /// <summary>阶段边界刷新：把缓存组件指针解析到 entity 当前所在的 chunk 元素（结构变更后调用）。</summary>
        void Bind(World world, Entity entity);
    }
}
