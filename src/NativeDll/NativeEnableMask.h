#pragma once

#include "ChunkJobData.h"

#include <cstdint>

// ============================================================================
// enableable 组件位图的**原生读写原语**（P1-6 / P1-7）。
//
// 数据来源（2026-09-13 打通）：
//   C# 侧 `ChunkJobScheduler` 在构建 chunk 调度数据时填 `ChunkJobData.enableBitMaps[i]`
//   —— 与 `componentArrays[i]` **同序**（= Archetype.Types 序），仅 enableable 组件非 nullptr。
//   生成的 C++ 包装把它拷进轻量 `ChunkData.enableBitMaps` / `enableBitmapCount`
//   （此前恒为 `nullptr` / `0`，注释写着"预留"）。
//
// 位图布局：**1 bit / 实体**，位 k = 该 chunk 内槽位 k；按 64 位字存储
//   （与 C# `ChunkEnabledMask.Bits`（`Span<ulong>`）`ArchetypeChunk.GetEnabledMask<T>()` 一致）。
//
// ⚠ 写并发纪律：同一 64 位字内的两个位若由**不同 lane**写会丢更新（读改写非原子）。
//   要么保证"同字同 lane"（按 64 实体切分工作），要么整字重算后一次性写回
//   —— 这与 C# 侧 `AliveBitJob` 的"按字整写"策略同源。
// ============================================================================

namespace EntJoy
{
    namespace EnableMask
    {
        /// <summary>取该 chunk 第 componentIndex 个组件的 enable 位图（非 enableable 组件返回 nullptr）。</summary>
        inline void* ForComponent(const ChunkData* chunk, int componentIndex)
        {
            if (chunk == nullptr || chunk->enableBitMaps == nullptr) return nullptr;
            if (componentIndex < 0 || componentIndex >= chunk->enableBitmapCount) return nullptr;
            return chunk->enableBitMaps[componentIndex];
        }

        /// <summary>位图判位。bitmap == nullptr（非 enableable）一律视为**启用**。</summary>
        inline bool IsEnabled(const void* bitmap, int index)
        {
            if (bitmap == nullptr) return true;
            const uint64_t* words = static_cast<const uint64_t*>(bitmap);
            return ((words[index >> 6] >> (index & 63)) & 1ull) != 0ull;
        }

        inline void SetEnabled(void* bitmap, int index, bool enabled)
        {
            if (bitmap == nullptr) return;
            uint64_t* words = static_cast<uint64_t*>(bitmap);
            const uint64_t mask = 1ull << (index & 63);
            if (enabled) words[index >> 6] |= mask;
            else words[index >> 6] &= ~mask;
        }

        /// <summary>按组件下标判位（常用于 `Execute(index)` 内跳过被禁用实体）。</summary>
        inline bool IsEnabledFor(const ChunkData* chunk, int componentIndex, int index)
        {
            return IsEnabled(ForComponent(chunk, componentIndex), index);
        }
    }
}
