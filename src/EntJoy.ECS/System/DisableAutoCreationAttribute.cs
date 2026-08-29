using System;

namespace EntJoy.ECS
{
    /// <summary>
    /// 标记 System 不参与自动注册（<c>SystemRegistry.RegisterAll</c> 跳过），
    /// 需手动 <c>runner.RegisterSystem&lt;T&gt;()</c>。对齐 Unity DOTS 的
    /// DisableAutoCreation 特性语义。
    /// </summary>
    [AttributeUsage(AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
    public sealed class DisableAutoCreationAttribute : Attribute
    { }
}
