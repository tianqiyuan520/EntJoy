#pragma once

#include <cstdint>

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
