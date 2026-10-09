#pragma once

#include <cstdint>
#include <optional>

#if defined(_WIN32)
#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>
#elif defined(__linux__)
#include <sched.h>
#endif

namespace JobSystem
{
    // 亲和掩码的底层位宽类型：Windows 为 KAFFINITY（x64=64 位、x86=32 位），
    // 其它平台无此类型，用 uint64_t 作等价替身（仅用于纯计算/单测）。
#if defined(_WIN32)
    using AffinityMask = KAFFINITY;
#else
    using AffinityMask = uint64_t;
#endif
    inline constexpr int kAffinityMaskBits = static_cast<int>(sizeof(AffinityMask) * 8);

    // 纯函数：worker i 绑定的逻辑核心为 `1 + i`（主线程占 core 0），返回该核心的掩码。
    //
    // 返回 `std::nullopt` 表示"应当跳过设置亲和性"（保持系统自选核心），而不是
    // 返回 0 或饱和值：`AffinityMask(1) << cpuIndex` 在 `cpuIndex >= 位宽` 时既是 UB
    // （移位量不小于类型位宽），结果掩码也为 0 ⇒ SetThreadGroupAffinity 会静默失败。
    // 历史上 worker 数可达数百（用户显式请求），因此这个越界分支必须有测试覆盖
    // （见 tests/NativeDll.Tests/AffinityMaskTests.cpp：索引 {0,1,62,63,64,1000}）。
    //
    // 该函数不读任何全局/线程状态，可在任意平台单测。
    inline std::optional<AffinityMask> ComputeAffinityMask(uint64_t workerIndex) noexcept
    {
        // 先比较、后相加：`workerIndex + 1` 在 workerIndex == UINT64_MAX 时会回绕成 0，
        // 于是"越界"被误判成"合法且绑到 core 0"（把 worker 钉到提交线程的核心）。
        // 因此用 `i >= kAffinityMaskBits - 1` 表达 `1 + i >= kAffinityMaskBits`，全程无加法回绕。
        constexpr uint64_t kMaxValidWorkerIndex =
            static_cast<uint64_t>(kAffinityMaskBits) - 1u;
        if (workerIndex >= kMaxValidWorkerIndex)
            return std::nullopt;
        return static_cast<AffinityMask>(
            static_cast<AffinityMask>(1) << (workerIndex + 1u));
    }

    inline bool BindCurrentThreadToLogicalProcessor(
        uint32_t logicalProcessorIndex) noexcept
    {
#if defined(_WIN32)
        const WORD groupCount = ::GetActiveProcessorGroupCount();
        uint32_t remaining = logicalProcessorIndex;
        for (WORD group = 0; group < groupCount; ++group)
        {
            const DWORD processorCount =
                ::GetActiveProcessorCount(group);
            if (processorCount == 0 || processorCount == 0xffffffffu)
                continue;
            if (remaining >= processorCount)
            {
                remaining -= processorCount;
                continue;
            }

            GROUP_AFFINITY affinity{};
            affinity.Group = group;
            affinity.Mask = static_cast<KAFFINITY>(1) << remaining;
            return ::SetThreadGroupAffinity(
                ::GetCurrentThread(), &affinity, nullptr) != FALSE;
        }
        return false;
#elif defined(__linux__)
        if (logicalProcessorIndex >= CPU_SETSIZE) return false;
        cpu_set_t affinity;
        CPU_ZERO(&affinity);
        CPU_SET(logicalProcessorIndex, &affinity);
        return ::sched_setaffinity(0, sizeof(affinity), &affinity) == 0;
#else
        (void)logicalProcessorIndex;
        return false;
#endif
    }

    // 清除当前线程的 CPU 亲和性（恢复允许所有核心）。
    inline bool ClearCurrentThreadAffinity() noexcept
    {
#if defined(_WIN32)
        // 恢复允许当前 group 所有核心：遍历线程所在 group 设置全 mask。
        for (WORD group = 0; group < ::GetActiveProcessorGroupCount(); ++group)
        {
            GROUP_AFFINITY affinity{};
            affinity.Group = group;
            affinity.Mask = static_cast<KAFFINITY>(~static_cast<KAFFINITY>(0));
            if (::SetThreadGroupAffinity(
                    ::GetCurrentThread(), &affinity, nullptr) != FALSE)
                return true;
        }
        return false;
#elif defined(__linux__)
        cpu_set_t affinity;
        CPU_ZERO(&affinity);
        for (int i = 0; i < CPU_SETSIZE; ++i) CPU_SET(i, &affinity);
        return ::sched_setaffinity(0, sizeof(affinity), &affinity) == 0;
#else
        return false;
#endif
    }
}
