#include <atomic>
#include <cstdint>
#include <cstdio>
#include <limits>
#include <iterator>
#include "SimdValue.h"

int main()
{
    struct Case { int value, shift, expected; };
    constexpr int min = std::numeric_limits<int>::min();
    constexpr int max = std::numeric_limits<int>::max();
    constexpr Case cases[] = {
        {1, 0, 1}, {1, 31, min}, {1, 32, 1}, {1, 33, 2},
        {1, -1, min}, {-1, 1, -2}, {min, 0, min}, {min, 1, 0},
        {max, 1, -2}, {0, max, 0}, {-1, -32, -1},
        {0x12345678, 4, 0x23456780}, {1, 35, 8}
    };
    for (int start = 0; start < static_cast<int>(std::size(cases)); start++)
    {
        int values[NSIMD_WIDTH], shifts[NSIMD_WIDTH], result[NSIMD_WIDTH];
        for (int lane = 0; lane < NSIMD_WIDTH; lane++)
        {
            const auto& test = cases[(start + lane) % std::size(cases)];
            values[lane] = test.value;
            shifts[lane] = test.shift;
        }
        auto actual = simd_value<int>{n_load_epi32(values)} << simd_value<int>{n_load_epi32(shifts)};
        actual.store(result);
        for (int lane = 0; lane < NSIMD_WIDTH; lane++)
        {
            const auto& test = cases[(start + lane) % std::size(cases)];
            if (result[lane] != test.expected)
            {
                std::fprintf(stderr, "FAIL: value=%d shift=%d actual=%d expected=%d lane=%d\n",
                    test.value, test.shift, result[lane], test.expected, lane);
                return 1;
            }
        }
    }
    std::printf("PASS: varying left shifts preserve C# count masking and overflow, width=%d.\n", NSIMD_WIDTH);
}
