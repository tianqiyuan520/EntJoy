using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace EntJoy.ECS
{
    public unsafe sealed partial class Archetype : IDisposable
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ref T GetComponent<T>(int chunkIndex, int slotInChunk) where T : struct
        {
            var componentIndex = GetComponentTypeIndex<T>();
            ref var chunk = ref CollectionsMarshal.AsSpan(_chunkList)[chunkIndex];
            return ref chunk.GetComponent<T>(slotInChunk, componentIndex);
        }

        public ref readonly T GetComponentReadOnly<T>(int chunkIndex, int slotInChunk) where T : struct
        {
            CheckReadable();
            int componentIndex = GetComponentTypeIndex<T>();
            ref readonly var chunk = ref CollectionsMarshal.AsSpan(_chunkList)[chunkIndex];
            return ref chunk.GetComponentReadOnly<T>(slotInChunk, componentIndex);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Set<T>(int chunkIndex, int slotInChunk, T value) where T : struct
        {
            var componentIndex = GetComponentTypeIndex<T>();
            ref var chunk = ref CollectionsMarshal.AsSpan(_chunkList)[chunkIndex];
            chunk.GetComponent<T>(slotInChunk, componentIndex) = value;
            // 同一个块引用上直接标变更，不再按块号重取一次。
            chunk.MarkEntityChanged(slotInChunk);
        }

        /// <summary>按 boxed 值写入组件槽位；关系列必须走 EntityManager 或 DeferredCommandBuffer。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public unsafe void SetRaw(int chunkIndex, int slotInChunk, Type componentType, object value)
        {
            if (ComponentTypeManager.GetComponentType(componentType).IsRelation)
                throw new InvalidOperationException("Relationship writes must use EntityManager or DeferredCommandBuffer.");
            SetStorageRaw(chunkIndex, slotInChunk, componentType, value);
        }

        internal unsafe void SetStorageRaw(int chunkIndex, int slotInChunk, Type componentType, object value)
        {
            SetStorageRaw(chunkIndex, slotInChunk, ComponentTypeManager.GetComponentType(componentType), value);
        }

        internal unsafe void SetStorageRaw(int chunkIndex, int slotInChunk, ComponentType compType, object value)
        {
            var componentIndex = GetComponentTypeIndex(compType);
            ref readonly var chunk = ref CollectionsMarshal.AsSpan(_chunkList)[chunkIndex];
            var compSize = compType.Size;
            var compPtr = (byte*)chunk.GetComponentArrayPointer(componentIndex) + slotInChunk * compSize;

            // 覆盖写：先销毁旧值（持有原生资源的组件释放旧资源），再转移新值进来。
            // 非 IDisposable 组件此调用为 no-op。
            ComponentTypeManager.DestroyComponentValue(compType, compPtr);

            var handle = GCHandle.Alloc(value, GCHandleType.Pinned);
            try
            {
                var srcPtr = handle.AddrOfPinnedObject();
                ComponentTypeManager.MoveComponentValue(compType, (void*)srcPtr, compPtr);
            }
            finally
            {
                handle.Free();
            }
            NotifyComponentChanged(chunkIndex, slotInChunk);
        }

        /// <summary>组件写入后标记该实体已变更（变更位图供 WithChanged 过滤）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void NotifyComponentChanged(int chunkIndex, int slotInChunk)
        {
            ref var chunk = ref CollectionsMarshal.AsSpan(_chunkList)[chunkIndex];
            chunk.MarkEntityChanged(slotInChunk);
        }

        /// <summary>把本源型某个槽位的全部非 shared 组件搬到另一个原型；可启用组件的启用位同时搬移。</summary>
        public unsafe void CopyComponentsTo(int sourceChunkIndex, int sourceSlot, Archetype target, int targetChunkIndex, int targetSlot)
        {
            ref readonly var sourceChunk = ref CollectionsMarshal.AsSpan(_chunkList)[sourceChunkIndex];
            ref var targetChunk = ref CollectionsMarshal.AsSpan(target._chunkList)[targetChunkIndex];
            // shared 列不随实体迁移；可启用位、析构标记按原型定，三者都不必逐列查类型表。
            bool sharedColumns = HasSharedColumns;
            bool disposables = HasDisposableComponents;
            bool enableables = sourceChunk.Meta.HasEnableables;

            // 列序与本原型 types 顺序一致（构造时按同一顺序建 _componentColumns），源列下标即 i。
            for (int i = 0; i < types.Length; i++)
            {
                var type = types[i];
                if (sharedColumns && type.IsShared) continue;
                if (!target.TryGetComponentTypeIndex(type, out int targetComponentIndex)) continue;
                var sourcePtr = (byte*)sourceChunk.GetComponentArrayPointer(i) + sourceSlot * type.Size;
                var targetPtr = (byte*)targetChunk.GetComponentArrayPointer(targetComponentIndex) + targetSlot * type.Size;
                // move 转移所有权：可析构组件拷贝后清空源，避免源被销毁时释放目标仍在用的内存。
                // 非析构组件（绝大多数）只需位拷贝，不必进 ComponentTypeManager 的 move 分支。
                if (disposables && type.IsDisposable) ComponentTypeManager.MoveComponentValue(type, sourcePtr, targetPtr);
                else BufferCopy.Copy(targetPtr, sourcePtr, type.Size);
                if (enableables && type.IsEnableable)
                    targetChunk.SetComponentEnabled(targetComponentIndex, targetSlot, sourceChunk.GetComponentEnabled(i, sourceSlot));
            }
        }
    }
}
