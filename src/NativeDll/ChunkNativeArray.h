#pragma once

#include "ChunkJobData.h"
#include "NativeContainers.h"

namespace EntJoy
{
    namespace ChunkNativeArray
    {
        inline void* GetChunkComponentArray(const ChunkJobData* chunkData, int componentTypeId)
        {
            if (!chunkData) return nullptr;
            for (int i = 0; i < chunkData->componentCount; ++i)
            {
                if (chunkData->componentTypeIndices[i] == componentTypeId)
                {
                    return chunkData->componentArrays[i];
                }
            }

            return nullptr;
        }

        inline void* GetRequiredChunkComponentArray(const ChunkJobData* chunkData, int requiredSlot, int componentTypeId)
        {
            if (!chunkData) return nullptr;
            if (chunkData->requiredComponentArrays != nullptr &&
                requiredSlot >= 0 &&
                requiredSlot < chunkData->requiredComponentCount)
            {
                void* ptr = chunkData->requiredComponentArrays[requiredSlot];
                if (ptr != nullptr)
                {
                    return ptr;
                }
            }

            return GetChunkComponentArray(chunkData, componentTypeId);
        }

        template<typename T>
        inline EntJoy::Collections::NativeArray<T> GetChunkNativeArray(const ChunkJobData* chunkData, int componentTypeId)
        {
            if (!chunkData)
                return EntJoy::Collections::NativeArray<T>{ nullptr, 0, EntJoy::Collections::Allocator::None,
                    EntJoy::Collections::AtomicSafetyHandle{0}, false };
            for (int i = 0; i < chunkData->componentCount; ++i)
            {
                if (chunkData->componentTypeIndices[i] == componentTypeId)
                {
                    return EntJoy::Collections::NativeArray<T>{
                        chunkData->componentArrays[i],
                        chunkData->entityCount,
                        EntJoy::Collections::Allocator::None,
                        EntJoy::Collections::AtomicSafetyHandle{0},
                        false
                    };
                }
            }

            return EntJoy::Collections::NativeArray<T>{
                nullptr,
                0,
                EntJoy::Collections::Allocator::None,
                EntJoy::Collections::AtomicSafetyHandle{0},
                false
            };
        }
    }
}
