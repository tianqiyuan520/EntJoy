// Item 8：亲和掩码计算的 native 单测。
//
// 背景：`ChaseLevScheduler::ApplyAffinity` 里 `KAFFINITY(1) << (1 + i)` 在 `1+i >= 位宽`
// 时是 UB（移位量不小于类型位宽）且掩码为 0 ⇒ SetThreadGroupAffinity 静默失败。
// 该防御分支此前**没有任何测试**（缺失的 RED）。此处把掩码计算提取为纯函数
// `JobSystem::ComputeAffinityMask`（src/NativeDll/ThreadAffinity.h）并在此覆盖
// 索引 {0, 1, 62, 63, 64, 1000}（含边界与越界）。
//
// 本目标只编译这一个 TU（纯头文件函数），不链接 JobSystem 源码。
#include "ThreadAffinity.h"

#include <cstdint>
#include <cstdio>
#include <optional>

namespace {

int Failures = 0;

#define CHECK(cond, name)                                                    \
    do {                                                                     \
        if (!(cond)) {                                                       \
            printf("FAIL %s (line %d)\n", name, __LINE__);                   \
            ++Failures;                                                      \
        }                                                                    \
        else {                                                               \
            printf("PASS %s\n", name);                                       \
        }                                                                    \
    } while (0)

using JobSystem::AffinityMask;
using JobSystem::ComputeAffinityMask;
using JobSystem::kAffinityMaskBits;

// 位宽无关的期望：i 合法 ⇔ 1+i < 位宽。
bool ExpectValid(uint64_t i)
{
    return (i + 1u) < static_cast<uint64_t>(kAffinityMaskBits);
}

AffinityMask ExpectMask(uint64_t i)
{
    return static_cast<AffinityMask>(static_cast<AffinityMask>(1) << (i + 1u));
}

void CheckIndex(uint64_t i)
{
    const auto mask = ComputeAffinityMask(i);
    char label[96];
    std::snprintf(label, sizeof(label), "ComputeAffinityMask(%llu) valid=%d",
        static_cast<unsigned long long>(i), ExpectValid(i) ? 1 : 0);
    CHECK(mask.has_value() == ExpectValid(i), label);
    if (mask.has_value() != ExpectValid(i)) return;

    if (ExpectValid(i))
    {
        std::snprintf(label, sizeof(label), "ComputeAffinityMask(%llu) == 1<<(1+%llu)",
            static_cast<unsigned long long>(i), static_cast<unsigned long long>(i));
        CHECK(*mask == ExpectMask(i), label);

        // 关键不变量：绑定到 core 1+i（core 0 留给主线程）⇒ 掩码恰好一个 bit，且不在 bit0。
        std::snprintf(label, sizeof(label), "ComputeAffinityMask(%llu) binds core %llu (single bit, not bit0)",
            static_cast<unsigned long long>(i), static_cast<unsigned long long>(i + 1u));
        CHECK(*mask != 0 && (*mask & (*mask - 1)) == 0 && (*mask & 1) == 0, label);
    }
}

} // namespace

int main()
{
    std::printf("kAffinityMaskBits=%d sizeof(AffinityMask)=%zu\n",
        kAffinityMaskBits, sizeof(AffinityMask));

    // 任务指定的索引集合：0/1（低端）、62/63（边界内侧）、64（首个越界）、1000（远越界）。
    const uint64_t indices[] = { 0, 1, 62, 63, 64, 1000 };
    for (uint64_t i : indices)
        CheckIndex(i);

    // 明确的边界断言（64 位平台上与上表一致；32 位平台自动退化为"更大范围都越界"）。
    if (kAffinityMaskBits == 64)
    {
        CHECK(ComputeAffinityMask(62).has_value() &&
              *ComputeAffinityMask(62) == (static_cast<AffinityMask>(1) << 63),
            "core 63 mask == 1<<63 (highest usable bit)");
        CHECK(!ComputeAffinityMask(63).has_value(),
            "index 63 -> core 64 -> out of width -> nullopt (skip, no UB)");
        CHECK(!ComputeAffinityMask(64).has_value(),
            "index 64 -> core 65 -> out of width -> nullopt");
        CHECK(!ComputeAffinityMask(1000).has_value(),
            "index 1000 -> out of width -> nullopt");
        CHECK(*ComputeAffinityMask(0) == static_cast<AffinityMask>(2),
            "index 0 -> core 1 -> mask 0b10 (core 0 reserved for the submitting thread)");
        CHECK(*ComputeAffinityMask(1) == static_cast<AffinityMask>(4),
            "index 1 -> core 2 -> mask 0b100");
    }

    // 回绕防御：UINT32_MAX 作输入不得因 32 位 `1 + i` 回绕而产生"合法"掩码。
    CHECK(!ComputeAffinityMask(static_cast<uint64_t>(UINT32_MAX)).has_value(),
        "index UINT32_MAX -> nullopt (no 32-bit wraparound)");
    CHECK(!ComputeAffinityMask(UINT64_MAX).has_value(),
        "index UINT64_MAX -> nullopt (no 64-bit wraparound)");

    if (Failures == 0)
    {
        std::printf("AffinityMaskTests: ALL PASS\n");
        return 0;
    }
    std::printf("AffinityMaskTests: %d FAILURES\n", Failures);
    return 1;
}
