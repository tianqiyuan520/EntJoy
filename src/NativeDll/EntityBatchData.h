#pragma once

struct EntityBatchData {
    void** componentArrays;   // 0: 8 bytes
    void** enableBitMaps;     // 8: 8 bytes (per-component, nullptr = all enabled)
    int entityCount;          // 16: 4 bytes
    int enableBitmapCount;    // 20: 4 bytes (entries in enableBitMaps, 0 = no filtering)
}; // total 24 bytes, no padding — matches C# Sequential layout exactly
