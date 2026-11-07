#pragma once
#include <cstdint>
#include <vector>

struct EntityFilterData {
    const uint64_t* relationshipBits;
    void** enabledBitMaps;
    int enabledCount;
};

// Combined at execution time so dependency jobs can update Enabled bits.
class EntityMatchMask {
    std::vector<uint64_t> storage;
    bool anyMatch = true;
public:
    const uint64_t* Bits = nullptr;
    int Length = 0;
    int UlongCount = 0;

    EntityMatchMask(const EntityFilterData& filter, int entityCount) {
        if (entityCount <= 0) { anyMatch = false; return; }
        if (filter.relationshipBits == nullptr && filter.enabledCount == 0) return;
        Length = entityCount;
        UlongCount = (entityCount + 63) / 64;
        storage.resize(UlongCount);
        anyMatch = false;
        for (int word = 0; word < UlongCount; ++word) {
            uint64_t value = filter.relationshipBits ? filter.relationshipBits[word] : ~uint64_t(0);
            for (int column = 0; column < filter.enabledCount; ++column) {
                auto* enabled = static_cast<const uint64_t*>(filter.enabledBitMaps[column]);
                if (enabled) value &= enabled[word];
            }
            if (word == UlongCount - 1 && (entityCount & 63)) value &= (uint64_t(1) << (entityCount & 63)) - 1;
            storage[word] = value;
            anyMatch |= value != 0;
        }
        Bits = storage.data();
    }

    bool HasMatches() const { return anyMatch; }
    bool IsEnabled(int index) const { return (Bits[index >> 6] & (uint64_t(1) << (index & 63))) != 0; }
    bool TryGetNextRange(int& start, int& first, int& end) const {
        while (start < Length && !IsEnabled(start)) ++start;
        if (start >= Length) { first = end = 0; return false; }
        first = start;
        while (start < Length && IsEnabled(start)) ++start;
        end = start;
        return true;
    }
};
