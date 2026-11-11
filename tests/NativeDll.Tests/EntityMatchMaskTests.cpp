#include "../../src/NativeDll/EntityFilterData.h"
#include <cstdio>

static bool CheckMask(int count)
{
    uint64_t relationships[5] = {};
    uint64_t firstEnabled[5] = {};
    uint64_t secondEnabled[5] = {};
    for (int i = 0; i < count; ++i) {
        if (i % 2 == 0) relationships[i >> 6] |= uint64_t(1) << (i & 63);
        if (i % 3 != 0) firstEnabled[i >> 6] |= uint64_t(1) << (i & 63);
        if (i % 5 != 0) secondEnabled[i >> 6] |= uint64_t(1) << (i & 63);
    }
    void* columns[] = { firstEnabled, secondEnabled };
    EntityMatchMask mask({ relationships, columns, 2 }, count);
    int expectedCount = 0;
    for (int i = 0; i < count; ++i) {
        bool expected = i % 2 == 0 && i % 3 != 0 && i % 5 != 0;
        expectedCount += expected;
        if (mask.IsEnabled(i) != expected) return false;
    }
    if (mask.HasMatches() != (expectedCount != 0) || mask.Length != count) return false;
    if ((count & 63) && (mask.Bits[(count - 1) >> 6] >> (count & 63)) != 0) return false;

    int cursor = 0, first, end, previousEnd = 0, actualCount = 0;
    while (mask.TryGetNextRange(cursor, first, end)) {
        if (first < previousEnd || first >= end || end > count) return false;
        for (int i = first; i < end; ++i)
            if (!mask.IsEnabled(i)) return false;
        actualCount += end - first;
        previousEnd = end;
    }
    return cursor == count && actualCount == expectedCount;
}

int main()
{
    for (int count : { 1, 63, 64, 65, 255, 256, 257, 299 }) {
        if (!CheckMask(count)) {
            std::fprintf(stderr, "mask failed for %d entities\n", count);
            return 1;
        }
    }
    EntityMatchMask empty({ nullptr, nullptr, 0 }, 0);
    EntityMatchMask unfiltered({ nullptr, nullptr, 0 }, 1);
    uint64_t disabled = 0;
    void* columns[] = { &disabled };
    EntityMatchMask none({ nullptr, columns, 1 }, 1);
    if (empty.HasMatches() || !unfiltered.HasMatches() || unfiltered.Bits != nullptr || none.HasMatches()) return 1;
    disabled = 1;
    EntityMatchMask enabled({ nullptr, columns, 1 }, 1);
    if (!enabled.HasMatches() || !enabled.IsEnabled(0)) return 1;
    std::puts("EntityMatchMask tests passed");
}
