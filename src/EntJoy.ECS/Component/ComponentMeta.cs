using System;
using System.Collections.Concurrent;
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

        /// <summary>布局指纹（生成期算好的字面量；字段顺序/增删/改类型都会改它，不含 offset/size）。</summary>
        public ulong LayoutHash;

        /// <summary>布局指纹用的全限定类型名（与生成清单里的键逐字一致；`TypeName` 只是短名）。</summary>
        public string LayoutTypeName;
    }

    /// <summary>组件元数据注册表（序列化 / 数据导航 / 调试共用）。</summary>
    public static class ComponentMetaRegistry
    {
        private static readonly ConcurrentDictionary<int, ComponentMeta> _byId = new();

        public static void Register(ComponentMeta meta)
        {
            _byId[meta.TypeId] = meta;
            // 把布局指纹报到框架侧。键 = `程序集名|类型全名`，所以需要 owner 程序集名（由 TypeId 反查）。
            System.Reflection.Assembly owner = null;
            if (ComponentTypeManager.TryGetTypeById(meta.TypeId, out var ownerType) && ownerType != null)
                owner = ownerType.Assembly;
            EntJoy.JobSystem.NativeJobScheduler.RecordComponentLayout(
                owner,
                string.IsNullOrEmpty(meta.LayoutTypeName) ? meta.TypeName : meta.LayoutTypeName,
                meta.LayoutHash);
        }

        public static ComponentMeta Get(int typeId)
            => _byId.TryGetValue(typeId, out var meta) ? meta : default;

        public static ComponentMeta Get<T>() where T : struct
            => Get(ComponentTypeManager.GetComponentType(typeof(T)).Id);

        public static ComponentMeta Get(Type type)
            => Get(ComponentTypeManager.GetComponentType(type).Id);
    }
}
