using System;

namespace EntJoy.ECS
{
    /// <summary>
    /// 声明 System 读取的组件类型（支持不定量）
    /// </summary>
    [AttributeUsage(AttributeTargets.Struct, AllowMultiple = true)]
    public class ReadAttribute : Attribute
    {
        public Type[] ComponentTypes { get; }
        
        /// <summary>任意数量组件（params）</summary>
        public ReadAttribute(params Type[] componentTypes) => ComponentTypes = componentTypes;
    }

    /// <summary>
    /// 声明 System 写入的组件类型（支持不定量）
    /// </summary>
    [AttributeUsage(AttributeTargets.Struct, AllowMultiple = true)]
    public class WriteAttribute : Attribute
    {
        public Type[] ComponentTypes { get; }
        
        /// <summary>任意数量组件（params）</summary>
        public WriteAttribute(params Type[] componentTypes) => ComponentTypes = componentTypes;
    }

    /// <summary>
    /// 标记 System 的执行顺序优先级（数值小的先执行）
    /// </summary>
    [AttributeUsage(AttributeTargets.Struct)]
    public class OrderAttribute : Attribute
    {
        public int Priority { get; }
        public OrderAttribute(int priority) => Priority = priority;
    }

    /// <summary>
    /// 条件执行：当指定事件计数器 > 0 时才执行该 System
    /// </summary>
    [AttributeUsage(AttributeTargets.Struct)]
    public class RunWhenAttribute : Attribute
    {
        public Type EventType { get; }
        public RunWhenAttribute(Type eventType) => EventType = eventType;
    }

    /// <summary>
    /// 手动指定执行顺序：本 System 必须在 targetSystem 之前执行
    /// </summary>
    [AttributeUsage(AttributeTargets.Struct, AllowMultiple = true)]
    public class OrderBeforeAttribute : Attribute
    {
        public Type TargetSystem { get; }
        public OrderBeforeAttribute(Type targetSystem) => TargetSystem = targetSystem;
    }

    /// <summary>
    /// 手动指定执行顺序：本 System 必须在 targetSystem 之后执行
    /// </summary>
    [AttributeUsage(AttributeTargets.Struct, AllowMultiple = true)]
    public class OrderAfterAttribute : Attribute
    {
        public Type TargetSystem { get; }
        public OrderAfterAttribute(Type targetSystem) => TargetSystem = targetSystem;
    }
}