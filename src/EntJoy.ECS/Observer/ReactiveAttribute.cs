using System;

namespace EntJoy.ECS
{
    /// <summary>
    /// 标记 struct 为响应式事件处理器：组件生命周期事件 push 回调（对齐 Flecs Observer 的声明式用法）。
    /// 处理器须定义静态方法 <c>Execute(in ReadOnlySpan&lt;Entity&gt;, in ReadOnlySpan&lt;TComponent&gt;)</c>，
    /// 组件类型 TComponent 由源生成器从签名推导；一个类型可叠加多个 [Reactive] 订阅不同事件。
    /// </summary>
    [AttributeUsage(AttributeTargets.Struct, AllowMultiple = true, Inherited = false)]
    public sealed class ReactiveAttribute : Attribute
    {
        /// <summary>订阅的事件位（支持组合）。</summary>
        public ObserverEvents Events { get; }

        public ReactiveAttribute(ObserverEvents events) => Events = events;
    }
}
