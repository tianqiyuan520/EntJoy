using System;

namespace EntJoy.ECS
{
    /// <summary>
    /// Observer 订阅的事件位。
    /// </summary>
    [Flags]
    public enum ObserverEvents
    {
        None      = 0,
        /// <summary>实体获得某组件（含新实体带组件创建）。</summary>
        Added     = 1 << 0,
        /// <summary>实体失去某组件（含 DestroyEntity 时对每个已订阅组件触发）。</summary>
        Removed   = 1 << 1,
        /// <summary>组件值被写入（仅主线程 Set/SetRaw 路径，见 Observer 设计文档 §5）。</summary>
        Set       = 1 << 2,
        /// <summary>实体销毁（对实体拥有的每个已订阅组件触发 Removed）。</summary>
        Destroyed = 1 << 3,
    }
}
