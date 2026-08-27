#pragma once

#include <cstdint>

// 跨语言共享的 Chunk 任务数据结构
// C# 端必须使用 [StructLayout(LayoutKind.Sequential)] 保证内存布局一致
struct ChunkJobData {
    void*   entityArray;        // Entity 数组首地址
    int     entityCount;        // 实体数量
    int     componentCount;     // 组件种类数
    void**  componentArrays;    // 每个组件数组首地址（长度为 componentCount）
    int*    componentSizes;     // 每个组件大小（字节，长度为 componentCount）
    void**  enableBitMaps;      // 每个 enableable 组件位图指针（可为 nullptr，长度为 componentCount）
    int*    componentTypeIndices;   // 组件类型索引数组（用于 C# 端按类型查找）
    void*   chunkHandle;        // GCHandle IntPtr，用于在 C# 回调中恢复 Chunk 对象
    void**  requiredComponentArrays; // NativeTranspile IJobChunk required component arrays
    int     requiredComponentCount;  // requiredComponentArrays length
    void**  sharedValuePtrs;     // SharedComponent blittable 值指针 [sharedValueCount]（2026-08-26）
    int     sharedValueCount;    // sharedValuePtrs 数量，0 = 无 shared 组件
};

// NativeTranspile 轻量 Chunk 数据结构
// 只包含 NativeTranspile 作业实际需要的字段，跳过 ChunkJobData 的冗余信息。
// enableBitMaps 预留为将来支持 IEnableComponent 使用。
// sharedValuePtrs: 每个 blittable SharedComponent 的单值指针（per-chunk，非 per-entity）。
struct ChunkData {
    void**  componentArrays;    // 组件数组指针 [requiredCount]，编译时已知索引
    int     entityCount;        // 实体数量
    int     requiredComponentCount; // 组件数组数量
    void**  enableBitMaps;      // enable 位图 [enableCount]，无过滤时为 nullptr（预留）
    int     enableBitmapCount;  // enable 位图数量，0 表示无过滤（预留）
    void**  sharedValuePtrs;    // SharedComponent blittable 值指针 [sharedValueCount]（2026-08-26）
    int     sharedValueCount;   // sharedValuePtrs 数量，0 = 无 shared 组件
};
