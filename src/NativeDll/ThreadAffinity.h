#pragma once

#include <algorithm>
#include <cstdint>
#include <optional>
#include <thread>
#include <vector>

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

    // 每个物理核选一个代表逻辑 CPU（Windows: RelationProcessorCore，取组内最低置位；
    // 其它平台按 `hardware_concurrency` 顺序返回，即"假设无 SMT"）。
    //
    // 用途：`ENTJOY_JOB_PIN=core` 把 worker 摊到物理核上。SMT 兄弟核共享执行端口与
    // L1/L2，两个 worker 落到同一物理核时吞吐接近减半 —— 逻辑 CPU 编号不是核编号。
    inline std::vector<uint32_t> PhysicalCoreRepresentatives()
    {
        std::vector<uint32_t> reps;
#if defined(_WIN32)
        DWORD len = 0;
        ::GetLogicalProcessorInformationEx(RelationProcessorCore, nullptr, &len);
        if (len == 0) return reps;
        std::vector<unsigned char> buf(len);
        if (!::GetLogicalProcessorInformationEx(
                RelationProcessorCore,
                reinterpret_cast<PSYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX>(buf.data()),
                &len))
            return reps;
        for (DWORD off = 0; off + sizeof(SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX) <= len;)
        {
            auto* info = reinterpret_cast<PSYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX>(buf.data() + off);
            if (info->Size == 0) break;  // 防御：解析失配时不死循环
            if (info->Relationship == RelationProcessorCore && info->Processor.GroupCount > 0)
            {
                const GROUP_AFFINITY& ga = info->Processor.GroupMask[0];
                uint32_t base = 0;  // 该 group 之前所有 group 的逻辑 CPU 数
                for (WORD g = 0; g < ga.Group; ++g)
                    base += ::GetActiveProcessorCount(g);
                for (int bit = 0; bit < static_cast<int>(sizeof(KAFFINITY) * 8); ++bit)
                {
                    if (((ga.Mask >> bit) & static_cast<KAFFINITY>(1)) != 0)
                    {
                        reps.push_back(base + static_cast<uint32_t>(bit));
                        break;
                    }
                }
            }
            off += info->Size;
        }
        // 解析可能有记录缺口（实测某些固件只报 n-1 条 RelationProcessorCore）。
        // 未被任何记录覆盖的逻辑 CPU 补成独立的代表：保证"每个逻辑 CPU 恰好归属一个核"，
        // 否则 worker 多于解析出的核数时会被拒绑并回落到系统自选核，破坏核摊派。
        {
            const uint32_t totalLogical = static_cast<uint32_t>(::GetActiveProcessorCount(0));
            std::vector<bool> covered(totalLogical, false);
            for (DWORD off2 = 0; off2 + sizeof(SYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX) <= len;)
            {
                auto* info2 = reinterpret_cast<PSYSTEM_LOGICAL_PROCESSOR_INFORMATION_EX>(buf.data() + off2);
                if (info2->Size == 0) break;
                if (info2->Relationship == RelationProcessorCore && info2->Processor.GroupCount > 0)
                {
                    const GROUP_AFFINITY& ga2 = info2->Processor.GroupMask[0];
                    uint32_t base2 = 0;
                    for (WORD g = 0; g < ga2.Group; ++g) base2 += ::GetActiveProcessorCount(g);
                    for (int bit = 0; bit < static_cast<int>(sizeof(KAFFINITY) * 8); ++bit)
                    {
                        if (((ga2.Mask >> bit) & static_cast<KAFFINITY>(1)) != 0)
                        {
                            const uint32_t cpu = base2 + static_cast<uint32_t>(bit);
                            if (cpu < totalLogical) covered[cpu] = true;
                        }
                    }
                }
                off2 += info2->Size;
            }
            for (uint32_t cpu = 0; cpu < totalLogical; ++cpu)
            {
                if (covered[cpu]) continue;
                bool dup = false;
                for (uint32_t r : reps) if (r == cpu) { dup = true; break; }
                if (!dup) reps.push_back(cpu);
            }
            std::sort(reps.begin(), reps.end());
        }
#else
        const unsigned n = std::thread::hardware_concurrency();
        for (unsigned i = 0; i < n; ++i) reps.push_back(static_cast<uint32_t>(i));
#endif
        return reps;
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
