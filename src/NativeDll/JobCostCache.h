#pragma once

// JobCostCache — per-job 每元素成本 EWMA 缓存（自动 batch 核心）。
//
// 目的：tpw=4 对所有 job 一刀切；light job 需更少 tiles、heavy 需更多。
// 用每 job 的每元素执行成本 EWMA（batch 退役时从 wall-clock 反推），
// ResolveChunkSize 据此自动求解最优 tile 数。
//
// 设计：
//   - 固定 256 槽数组（2KB），无锁（槽位独立 atomic）；funcHash（FNV-1a 32-bit）
//     定位，碰撞复用重学（无正确性风险）
//   - Q22 定点存储（uint64），避免 double 原子读写
//   - flag 关闭时零开销（Get 返回 0 → tpw 兜底；Update 不调用）
//   - 无上升阻尼：成本波动（同 job 依赖外部参数可达 1000x）需快速跟随，
//     单次 GC/抢占尖峰经 EWMA 双向 α=0.75 在 1-2 轮内自愈
//   - 必须用有符号分支：sample < oldVal 时无符号减法下溢会把 EWMA 炸到 ~2^64

#include <atomic>
#include <cstddef>
#include <cstdint>

namespace JobSystem
{
    // 槽位数。256 对典型游戏（<50 job 类型）足够；碰撞 → EWMA 重学。
    constexpr int kJobCostSlots = 256;
    // Q22 定点比例：perElemNs(ns) × 2^22。1ns 分辨率 @ 0.24ns 精度。
    constexpr uint64_t kJobCostQ22 = 1ull << 22;

    struct JobCostCache
    {
        // 每元素执行时间（Q22 定点，单位 ns）
        std::atomic<uint64_t> perElemEwmaNs[kJobCostSlots];
        // funcPtr hash 校验（碰撞时复用 → 重学）
        std::atomic<uint32_t> slotHash[kJobCostSlots];

        JobCostCache() noexcept { Init(); }

        void Init() noexcept
        {
            for (int i = 0; i < kJobCostSlots; ++i)
            {
                perElemEwmaNs[i].store(0, std::memory_order_relaxed);
                slotHash[i].store(0, std::memory_order_relaxed);
            }
        }

        // 热路径读取：返回每元素 ns；0 = 冷启动无数据（调用方走 tpw=4 兜底）。
        double GetPerElemCost(uint32_t funcHash) const noexcept
        {
            const int slot = funcHash & (kJobCostSlots - 1);
            if (slotHash[slot].load(std::memory_order_relaxed) == funcHash)
            {
                return static_cast<double>(
                    perElemEwmaNs[slot].load(std::memory_order_relaxed))
                    / static_cast<double>(kJobCostQ22);
            }
            return 0.0;
        }

        // 更新 EWMA（α=0.75，双向对称）。仅由退役路径在 flag 开启时调用。
        // 无竞态：CAS 循环（多 worker 同槽并发不丢更新）。
        void UpdatePerElemCost(uint32_t funcHash, double perElemNs) noexcept
        {
            if (perElemNs < 0.0) return;
            const int slot = funcHash & (kJobCostSlots - 1);
            slotHash[slot].store(funcHash, std::memory_order_relaxed);
            const uint64_t sample = static_cast<uint64_t>(perElemNs * static_cast<double>(kJobCostQ22));
            uint64_t oldVal = perElemEwmaNs[slot].load(std::memory_order_relaxed);
            while (true)
            {
                uint64_t newVal;
                if (oldVal == 0)
                {
                    newVal = sample;   // 冷启动直取
                }
                else if (sample > oldVal)
                {
                    newVal = oldVal + (((sample - oldVal) * 3) >> 2);
                }
                else
                {
                    newVal = oldVal - (((oldVal - sample) * 3) >> 2);
                }
                if (perElemEwmaNs[slot].compare_exchange_weak(
                        oldVal, newVal, std::memory_order_relaxed, std::memory_order_relaxed))
                    break;
                // CAS 失败：oldVal 已更新为最新值，重算 blend
            }
        }
    };

    // 全局实例（inline：各 TU 共享一份，无 ODR 问题）
    inline JobCostCache g_jobCostCache;

    // funcPtr → hash（FNV-1a 32-bit）。函数指针地址在进程内稳定，
    // 同一 job 类型每次 Schedule 都得到同一 hash → 稳定映射到同一槽位。
    inline uint32_t HashFuncPtr(void (*func)() noexcept) noexcept
    {
        uint32_t h = 2166136261u;
        const auto* p = reinterpret_cast<const uint8_t*>(&func);
        for (std::size_t i = 0; i < sizeof(func); ++i)
        {
            h ^= static_cast<uint32_t>(p[i]);
            h *= 16777619u;
        }
        return h;
    }
} // namespace JobSystem