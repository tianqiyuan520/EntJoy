#pragma once

#include "ChunkJobData.h"
#include "NativeContainers.h"

namespace EntJoy
{
    namespace ChunkNativeArray
    {
        inline void* GetChunkComponentArray(const ChunkJobData* chunkData, int componentTypeId)
        {
            for (int i = 0; i < chunkData->componentCount; ++i)
            {
                if (chunkData->componentTypeIndices[i] == componentTypeId)
                {
                    return chunkData->componentArrays[i];
                }
            }

            return nullptr;
        }

        template<typename T>
        inline EntJoy::Collections::NativeArray<T> GetChunkNativeArray(const ChunkJobData* chunkData, int componentTypeId)
        {
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
