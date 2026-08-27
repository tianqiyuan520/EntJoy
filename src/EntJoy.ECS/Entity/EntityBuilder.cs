using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace EntJoy.ECS
{
    /// <summary>
    /// 实体构造器：链式 API 消灭 CreateEntity + Set 样板代码
    /// 
    /// 使用方式：
    ///   var entity = world.Spawn()
    ///       .With(new Position { X = 1, Y = 1 })
    ///       .With(new Velocity { X = 0.1f, Y = 0.1f })
    ///       .Build();
    /// </summary>
    public struct EntityBuilder
    {
        private readonly EntityManager _entityManager;
        private readonly List<ComponentType> _types;
        private readonly List<ComponentSetter> _setters;
        private readonly List<(Type type, object value)> _sharedValues;

        internal EntityBuilder(EntityManager entityManager)
        {
            _entityManager = entityManager;
            _types = new List<ComponentType>();
            _setters = new List<ComponentSetter>();
            _sharedValues = new List<(Type, object)>();
        }

        /// <summary>
        /// 添加组件（带值）
        /// </summary>
        public EntityBuilder With<T>(T value) where T : struct
        {
            var type = ComponentTypeManager.GetComponentType(typeof(T));
            _types.Add(type);
            _setters.Add(new ComponentSetter { Type = typeof(T), Value = value });
            return this;
        }

        /// <summary>
        /// 添加组件（使用默认值）
        /// </summary>
        public EntityBuilder With<T>() where T : struct
        {
            var type = ComponentTypeManager.GetComponentType(typeof(T));
            _types.Add(type);
            _setters.Add(new ComponentSetter { Type = typeof(T), Value = default(T) });
            return this;
        }

        /// <summary>
        /// 添加共享组件。blittable shared 内联存于 chunk；managed shared 存 EntityManager 哈希桶（chunk 槽位存索引）。
        /// 同一值组合的实体进入同一 chunk。
        /// </summary>
        public EntityBuilder WithShared<T>(T value) where T : ISharedComponentData
        {
            var type = ComponentTypeManager.GetComponentType(typeof(T));
            _types.Add(type);
            _sharedValues.Add((typeof(T), value));
            return this;
        }

        /// <summary>
        /// 构建实体
        /// </summary>
        public Entity Build()
        {
            Entity entity;
            if (_sharedValues.Count > 0)
            {
                entity = _entityManager.NewEntity(_types.ToArray(), _sharedValues.ToArray());
            }
            else
            {
                entity = _entityManager.NewEntity(_types.ToArray());
            }
            foreach (var setter in _setters)
            {
                _entityManager.SetRaw(entity, setter.Type, setter.Value);
            }
            return entity;
        }

        private struct ComponentSetter
        {
            public Type Type;
            public object Value;
        }
    }

    /// <summary>
    /// World 扩展：Spawn() 返回 EntityBuilder
    /// </summary>
    public static class WorldEntityBuilderExtensions
    {
        public static EntityBuilder Spawn(this World world)
        {
            return new EntityBuilder(world.EntityManager);
        }
    }
}