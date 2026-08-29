using System;

namespace EntJoy.ECS
{
    /// <summary>
    /// 标记 struct 为 ECS 组件，源生成器自动补齐 <see cref="IComponentData"/> 接口。
    /// 要求被标记类型为 <c>partial struct</c> 且 blittable（无托管引用字段）。
    /// </summary>
    [AttributeUsage(AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
    public sealed class ECSComponentAttribute : Attribute
    { }
}
