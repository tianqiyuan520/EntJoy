#pragma once

#include <cstdint>
#include <cstddef>

// ============================================================================
// 原生侧「跨 chunk 随机访问」原语 —— 与 C# 的
//   EntJoy.ECS.EntityLocateB / NativeComponentLookup<T> / NativeEntityLookup
// 一一对应（src/EntJoy.ECS/Storage/NativeEntityLocate.cs）。
//
// 为什么需要它：EntJoy 原生内核（NativeTranspile 生成）此前只拿到"当前 chunk"的
// 组件数组（ChunkJobData.componentArrays / requiredComponentArrays），无法访问别的 chunk；
// 而托管位置表 EntityIndexInWorld 含托管 Archetype 引用，原生读不到。
// 本文件提供 blittable 定位表的解析函数，使原生内核能做与 Unity DOTS
// ComponentLookup<T> 等价的随机访问。
//
// 解析公式（与 C# NativeComponentLookup<T>.UnsafeResolve 逐字一致）：
//   T* p = (T*)((char*)locate[entityId].ChunkMemory
//               + locate[entityId].ChunkOffsets[componentIndex]
//               + locate[entityId].SlotInChunk * sizeof(T));
// ============================================================================

struct EntityLocateB {
    void* ChunkMemory;   // +0   chunk 数据块首址（nullptr = 未分配 / 已销毁）
    int*  ChunkOffsets;  // +8   该 Archetype 的组件列字节偏移（非托管镜像），按 componentIndex 索引
    int   SlotInChunk;   // +16
    int   Version;       // +20
};

static_assert(sizeof(EntityLocateB) == 24, "EntityLocateB must be 24 bytes (layout mismatch with C#)");

namespace EntJoy
{
    namespace EntityLookup
    {
        /// <summary>实体槽位是否已被分配（越界一律 false）。</summary>
        inline bool IsAllocated(const EntityLocateB* locate, int length, int entityId)
        {
            if (locate == nullptr || entityId < 0 || entityId >= length) return false;
            const EntityLocateB& e = locate[entityId];
            return e.ChunkMemory != nullptr && e.SlotInChunk >= 0;
        }

        /// <summary>无校验解析（调用方保证实体有效）。热路径用。</summary>
        template <typename T>
        inline T* Resolve(const EntityLocateB* locate, int length, int componentIndex, int entityId)
        {
            if (!IsAllocated(locate, length, entityId)) return nullptr;
            const EntityLocateB& e = locate[entityId];
            return reinterpret_cast<T*>(
                reinterpret_cast<char*>(e.ChunkMemory)
                + static_cast<std::ptrdiff_t>(e.ChunkOffsets[componentIndex])
                + static_cast<std::ptrdiff_t>(e.SlotInChunk) * static_cast<std::ptrdiff_t>(sizeof(T)));
        }

        /// <summary>解析并校验版本（防悬垂 / 防读到 Id 被复用后的新实体）。</summary>
        template <typename T>
        inline T* ResolveValidated(const EntityLocateB* locate, int length, int componentIndex,
                                   int entityId, int version)
        {
            if (!IsAllocated(locate, length, entityId)) return nullptr;
            if (locate[entityId].Version != version) return nullptr;
            return Resolve<T>(locate, length, componentIndex, entityId);
        }

        /// <summary>取出 chunk 基址 / 槽位 / 版本（对齐 Unity EntityStorageInfoLookup 的用途）。</summary>
        inline bool TryGet(const EntityLocateB* locate, int length, int entityId,
                           void** chunkMemory, int* slotInChunk, int* version)
        {
            if (!IsAllocated(locate, length, entityId)) return false;
            const EntityLocateB& e = locate[entityId];
            *chunkMemory = e.ChunkMemory;
            *slotInChunk = e.SlotInChunk;
            *version = e.Version;
            return true;
        }
    }
}

/// job 字段用的值语义句柄（与 C# NativeComponentLookup<T> 对应）：
/// 只含裸指针与整数，无托管引用、无可变缓存 ⇒ 可按值传给并行 job 并被多线程只读共享。
/// componentIndex 由宿主预解析（Archetype.GetComponentTypeIndex<T>()）后作为字段传入。
template <typename T>
struct NativeComponentLookupB {
    const EntityLocateB* Locate;
    int ComponentIndex;
    int Length;

    inline bool IsAllocated(int entityId) const
    {
        return EntJoy::EntityLookup::IsAllocated(Locate, Length, entityId);
    }

    inline T* UnsafeResolve(int entityId) const
    {
        const EntityLocateB& e = Locate[entityId];
        return reinterpret_cast<T*>(
            reinterpret_cast<char*>(e.ChunkMemory)
            + static_cast<std::ptrdiff_t>(e.ChunkOffsets[ComponentIndex])
            + static_cast<std::ptrdiff_t>(e.SlotInChunk) * static_cast<std::ptrdiff_t>(sizeof(T)));
    }

    inline bool TryResolve(int entityId, T** out) const
    {
        if (!IsAllocated(entityId)) { *out = nullptr; return false; }
        *out = UnsafeResolve(entityId);
        return true;
    }

    inline bool TryResolveValidated(int entityId, int version, T** out) const
    {
        if (!IsAllocated(entityId)) { *out = nullptr; return false; }
        if (Locate[entityId].Version != version) { *out = nullptr; return false; }
        *out = UnsafeResolve(entityId);
        return true;
    }
};

/// 位置查询句柄（与 C# NativeEntityLookup 对应）。
struct NativeEntityLookupB {
    const EntityLocateB* Locate;
    int Length;

    inline bool IsAllocated(int entityId) const
    {
        return EntJoy::EntityLookup::IsAllocated(Locate, Length, entityId);
    }

    inline bool TryGet(int entityId, void** chunkMemory, int* slotInChunk, int* version) const
    {
        return EntJoy::EntityLookup::TryGet(Locate, Length, entityId, chunkMemory, slotInChunk, version);
    }
};
