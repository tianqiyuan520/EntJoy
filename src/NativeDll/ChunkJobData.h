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
};
