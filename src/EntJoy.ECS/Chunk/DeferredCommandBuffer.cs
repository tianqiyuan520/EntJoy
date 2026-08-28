using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace EntJoy.ECS
{
    /// <summary>
    /// 手动延迟命令缓冲区（ECB）。
    /// 在 Job 或主线程中记录结构变更命令，帧末在主线程统一 Playback。
    ///
    /// 用法：
    ///   var ecb = new DeferredCommandBuffer();
    ///   ecb.CreateEntity(typeof(Position), typeof(Velocity));
    ///   ecb.DestroyEntity(entity);
    ///   ecb.AddComponent(entity, new Position { X = 1 });
    ///   ecb.RemoveComponent<Velocity>(entity);
    ///   // ... Job 完成后 ...
    ///   ecb.Playback(world.EntityManager);
    ///   ecb.Dispose();
    ///
    /// Observer 集成：Playback 内部调用 EntityManager 主入口（NewEntity/DestroyEntity/AddComponentRaw/
    /// RemoveComponentRaw），主线程结构变更入口统一挂 observer 派发 → ECB Playback 天然触发事件，无需额外扩展。
    /// </summary>
    public unsafe class DeferredCommandBuffer : IDisposable
    {
        private byte* _staging;
        private int _stagingOffset;
        private int _stagingCapacity;
        private bool _disposed;

        private const int InitialCapacity = 64 * 1024;

        // OpCodes
        private const int OP_CREATE_ENTITY = 1;
        private const int OP_DESTROY_ENTITY = 2;
        private const int OP_ADD_COMPONENT = 3;
        private const int OP_REMOVE_COMPONENT = 4;

        public int CommandCount { get; private set; }

        public DeferredCommandBuffer()
        {
            _staging = (byte*)Marshal.AllocHGlobal(InitialCapacity);
            _stagingCapacity = InitialCapacity;
            _stagingOffset = 0;
            CommandCount = 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void EnsureCapacity(int additionalBytes)
        {
            if (_stagingOffset + additionalBytes > _stagingCapacity)
            {
                int newCapacity = _stagingCapacity * 2;
                while (newCapacity < _stagingOffset + additionalBytes)
                    newCapacity *= 2;
                var newStaging = (byte*)Marshal.AllocHGlobal(newCapacity);
                Buffer.MemoryCopy(_staging, newStaging, newCapacity, _stagingOffset);
                Marshal.FreeHGlobal((IntPtr)_staging);
                _staging = newStaging;
                _stagingCapacity = newCapacity;
            }
        }

        /// <summary>记录 CreateEntity 命令</summary>
        public void CreateEntity(params ComponentType[] componentTypes)
        {
            int typeCount = componentTypes.Length;
            int totalSize = sizeof(int) + sizeof(int) + typeCount * sizeof(int);
            EnsureCapacity(totalSize);

            *(int*)(_staging + _stagingOffset) = OP_CREATE_ENTITY;
            _stagingOffset += sizeof(int);
            *(int*)(_staging + _stagingOffset) = typeCount;
            _stagingOffset += sizeof(int);
            for (int i = 0; i < typeCount; i++)
            {
                *(int*)(_staging + _stagingOffset) = componentTypes[i].Id;
                _stagingOffset += sizeof(int);
            }
            CommandCount++;
        }

        /// <summary>记录 DestroyEntity 命令</summary>
        public void DestroyEntity(Entity entity)
        {
            int totalSize = sizeof(int) + sizeof(Entity);
            EnsureCapacity(totalSize);

            *(int*)(_staging + _stagingOffset) = OP_DESTROY_ENTITY;
            _stagingOffset += sizeof(int);
            *(Entity*)(_staging + _stagingOffset) = entity;
            _stagingOffset += sizeof(Entity);
            CommandCount++;
        }

        /// <summary>记录 AddComponent 命令</summary>
        public void AddComponent<T>(Entity entity, T value) where T : struct, IComponentData
        {
            int compSize = Unsafe.SizeOf<T>();
            int totalSize = sizeof(int) + sizeof(Entity) + sizeof(int) + sizeof(int) + compSize;
            EnsureCapacity(totalSize);

            *(int*)(_staging + _stagingOffset) = OP_ADD_COMPONENT;
            _stagingOffset += sizeof(int);
            *(Entity*)(_staging + _stagingOffset) = entity;
            _stagingOffset += sizeof(Entity);
            *(int*)(_staging + _stagingOffset) = ComponentTypeManager.GetComponentType(typeof(T)).Id;
            _stagingOffset += sizeof(int);
            *(int*)(_staging + _stagingOffset) = compSize;
            _stagingOffset += sizeof(int);
            Unsafe.CopyBlock(_staging + _stagingOffset, &value, (uint)compSize);
            _stagingOffset += compSize;
            CommandCount++;
        }

        /// <summary>记录 RemoveComponent 命令</summary>
        public void RemoveComponent<T>(Entity entity) where T : struct
        {
            int totalSize = sizeof(int) + sizeof(Entity) + sizeof(int);
            EnsureCapacity(totalSize);

            *(int*)(_staging + _stagingOffset) = OP_REMOVE_COMPONENT;
            _stagingOffset += sizeof(int);
            *(Entity*)(_staging + _stagingOffset) = entity;
            _stagingOffset += sizeof(Entity);
            *(int*)(_staging + _stagingOffset) = ComponentTypeManager.GetComponentType(typeof(T)).Id;
            _stagingOffset += sizeof(int);
            CommandCount++;
        }

        /// <summary>
        /// 主线程回放所有命令。无需注册，直接调用 EntityManager 的非泛型方法。
        /// </summary>
        public unsafe void Playback(EntityManager entityManager)
        {
            int offset = 0;
            while (offset < _stagingOffset)
            {
                int opCode = *(int*)(_staging + offset);
                offset += sizeof(int);

                switch (opCode)
                {
                    case OP_CREATE_ENTITY:
                    {
                        int typeCount = *(int*)(_staging + offset);
                        offset += sizeof(int);
                        var types = new ComponentType[typeCount];
                        for (int i = 0; i < typeCount; i++)
                        {
                            int typeId = *(int*)(_staging + offset);
                            offset += sizeof(int);
                            types[i] = new ComponentType(typeId);
                        }
                        entityManager.NewEntity(types);
                        break;
                    }
                    case OP_DESTROY_ENTITY:
                    {
                        var entity = *(Entity*)(_staging + offset);
                        offset += sizeof(Entity);
                        entityManager.DestroyEntity(entity);
                        break;
                    }
                    case OP_ADD_COMPONENT:
                    {
                        var entity = *(Entity*)(_staging + offset);
                        offset += sizeof(Entity);
                        int typeId = *(int*)(_staging + offset);
                        offset += sizeof(int);
                        int compSize = *(int*)(_staging + offset);
                        offset += sizeof(int);
                        
                        // 获取组件类型并从指针还原值
                        var compType = ComponentTypeManager.GetTypeByComponentType(typeId);
                        var value = Marshal.PtrToStructure((IntPtr)(_staging + offset), compType);
                        entityManager.AddComponentRaw(entity, compType, value);
                        
                        offset += compSize;
                        break;
                    }
                    case OP_REMOVE_COMPONENT:
                    {
                        var entity = *(Entity*)(_staging + offset);
                        offset += sizeof(Entity);
                        int typeId = *(int*)(_staging + offset);
                        offset += sizeof(int);
                        
                        // 获取组件类型
                        var compType = ComponentTypeManager.GetTypeByComponentType(typeId);
                        entityManager.RemoveComponentRaw(entity, compType);
                        break;
                    }
                }
            }

            _stagingOffset = 0;
            CommandCount = 0;
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                if (_staging != null)
                {
                    Marshal.FreeHGlobal((IntPtr)_staging);
                    _staging = null;
                }
                _disposed = true;
            }
        }

        ~DeferredCommandBuffer()
        {
            Dispose();
        }
    }
}
