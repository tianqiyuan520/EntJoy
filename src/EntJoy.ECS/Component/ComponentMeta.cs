using System;
using System.Collections.Generic;

namespace EntJoy.ECS
{
    /// <summary>组件字段类型（叶子字段，嵌套 struct 已展开）。</summary>
    public enum FieldKind
    {
        Int8, Int16, Int32, Int64,
        UInt8, UInt16, UInt32, UInt64,
        Float32, Float64,
        Bool, Char, Decimal,
    }

    /// <summary>单个组件字段的元数据。</summary>
    public struct ComponentFieldMeta
    {
        /// <summary>字段名（嵌套 struct 展开后带路径，如 "Pos.X"）。</summary>
        public string Name;
        /// <summary>字段在组件内的字节偏移。</summary>
        public int Offset;
        /// <summary>字段字节大小。</summary>
        public int Size;
        public FieldKind Kind;
    }

    /// <summary>组件类型元数据（由 SourceGenerator 生成，AOT 安全，无反射）。</summary>
    public struct ComponentMeta
    {
        public int TypeId;
        public string TypeName;
        public int Size;
        public ComponentFieldMeta[] Fields;
    }

    /// <summary>组件元数据注册表（序列化 / 数据导航 / 调试共用）。</summary>
    public static class ComponentMetaRegistry
    {
        private static readonly Dictionary<int, ComponentMeta> _byId = new();
        private static readonly object _lock = new();

        public static void Register(ComponentMeta meta)
        {
            lock (_lock)
            {
                _byId[meta.TypeId] = meta;
            }
        }

        public static ComponentMeta Get(int typeId)
            => _byId.TryGetValue(typeId, out var meta) ? meta : default;

        public static ComponentMeta Get<T>() where T : struct
            => Get(ComponentTypeManager.GetComponentType(typeof(T)).Id);

        public static ComponentMeta Get(Type type)
            => Get(ComponentTypeManager.GetComponentType(type).Id);
    }
}
