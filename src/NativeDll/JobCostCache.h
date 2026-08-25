#pragma once

// JobCostCache — per-job 每元素成本 EWMA 缓存（自动 batch 核心）。
//
// 目的：tpw=4 对所有 job 一刀切；light job 需更少 tiles、heavy 需更多。
// 用每 job 的每元素执行成本 EWMA（batch 退役时从 wall-clock 反推），
// ResolveChunkSize 据此自动求解最优 tile 数。
//
// 自适应内存绑定检测（带宽/延迟绑定 job，如 GridSearch Query 空间哈希 gather）：
//   纯"每元素成本"模型假设成本随并行度线性摊平（compute-bound）。但对
//   memory-bound job，总耗时由共享 DRAM 带宽/访问延迟主导，多开 tile 不会
//   线性提速 —— 按每元素成本推 tile 数会系统性错标。本实现用「粗/细两种
//   粒度下的每元素成本 EWMA」自检测：若细粒度（JCC 公式选定的 chunk）成本
//   与粗粒度（tpw chunk）成本几乎相同（比值 > 0.85），说明加 tile 无增益，
//   判定 memory-bound，退役到固定 tpw 分块；否则判定 parallel，走原公式。
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

    // 每槽学习/分类阈值。
    //  - 粗粒度（tpw）探测次数：学到了粗成本，再放 JCC 公式（细）跑。
    //  - 细粒度最小样本：与粗成本对比前至少要有几批细样本（EWMA 稳定）。
    constexpr int kCoarseProbeSamples = 3;
    constexpr int kMinFineSamples = 2;
    // 细/粗成本比 > 此值 → 判 memory-bound（细粒度没带来提速）。
    constexpr double kMemBoundRatio = 0.85;

    // 每槽分类模式
    enum SlotMode : uint8_t
    {
        kModeUnknown = 0,   // 学习期：交替采样粗/细
        kModeParallel = 1,  // compute-bound：细粒度有增益，走 JCC 公式
        kModeMemBound = 2   // bandwidth/latency-bound：固定 tpw 分块
    };

    struct JobCostCache
    {
        // 每元素执行时间（Q22 定点，单位 ns）—— 细粒度（JCC 公式选定 chunk）
        std::atomic<uint64_t> perElemEwmaNs[kJobCostSlots];
        // 每元素执行时间（Q22 定点，单位 ns）—— 粗粒度（tpw chunk）参考
        std::atomic<uint64_t> perElemCoarseNs[kJobCostSlots];
        // funcPtr hash 校验（碰撞时复用 → 重学）
        std::atomic<uint32_t> slotHash[kJobCostSlots];
        // 分类模式（学习期 / parallel / memory-bound）
        std::atomic<uint8_t> slotMode[kJobCostSlots];
        // 已采样的粗/细批次数（学习用）
        std::atomic<uint32_t> coarseSamples[kJobCostSlots];
        std::atomic<uint32_t> fineSamples[kJobCostSlots];

        JobCostCache() noexcept { Init(); }

        void Init() noexcept
        {
            for (int i = 0; i < kJobCostSlots; ++i)
            {
                perElemEwmaNs[i].store(0, std::memory_order_relaxed);
                perElemCoarseNs[i].store(0, std::memory_order_relaxed);
                slotHash[i].store(0, std::memory_order_relaxed);
                slotMode[i].store(kModeUnknown, std::memory_order_relaxed);
                coarseSamples[i].store(0, std::memory_order_relaxed);
                fineSamples[i].store(0, std::memory_order_relaxed);
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

        // 粗粒度（tpw）每元素成本读取：学习中细 EWMA 尚未播种时，用粗成本作
        // 代理跑公式，产出细粒度分块以采集细样本。0 = 冷启动无数据。
        double GetCoarseCost(uint32_t funcHash) const noexcept
        {
            const int slot = funcHash & (kJobCostSlots - 1);
            if (slotHash[slot].load(std::memory_order_relaxed) == funcHash)
            {
                return static_cast<double>(
                    perElemCoarseNs[slot].load(std::memory_order_relaxed))
                    / static_cast<double>(kJobCostQ22);
            }
            return 0.0;
        }

        // 分类模式读取（ResolveChunkSize 用）：0 unknown / 1 parallel / 2 mem-bound。
        SlotMode GetMode(uint32_t funcHash) const noexcept
        {
            const int slot = funcHash & (kJobCostSlots - 1);
            if (slotHash[slot].load(std::memory_order_relaxed) == funcHash)
                return static_cast<SlotMode>(
                    slotMode[slot].load(std::memory_order_relaxed));
            return kModeUnknown;
        }

        // 粗粒度样本学习是否完成（ResolveChunkSize 学习期用）。
        bool HasLearnedCoarse(uint32_t funcHash) const noexcept
        {
            const int slot = funcHash & (kJobCostSlots - 1);
            if (slotHash[slot].load(std::memory_order_relaxed) != funcHash)
                return false;
            return coarseSamples[slot].load(std::memory_order_relaxed) >= kCoarseProbeSamples;
        }

        uint32_t FineSampleCount(uint32_t funcHash) const noexcept
        {
            const int slot = funcHash & (kJobCostSlots - 1);
            if (slotHash[slot].load(std::memory_order_relaxed) != funcHash) return 0;
            return fineSamples[slot].load(std::memory_order_relaxed);
        }

        // 更新 EWMA（α=0.75，双向对称）。仅由退役路径在 flag 开启时调用。
        // 无竞态：CAS 循环（多 worker 同槽并发不丢更新）。
        //   perElemNs   ：本次 batch 的每元素墙钟成本
        //   tileCount   ：本次 batch 的 tile 数（≈ 并行粒度）
        //   targetCoarse：本次是否是粗粒度（tpw）分块（调度侧判定后传入）
        void UpdatePerElemCost(uint32_t funcHash, double perElemNs,
                               bool targetCoarse) noexcept
        {
            if (perElemNs < 0.0) return;
            const int slot = funcHash & (kJobCostSlots - 1);
            slotHash[slot].store(funcHash, std::memory_order_relaxed);
            const uint64_t sample = static_cast<uint64_t>(perElemNs * static_cast<double>(kJobCostQ22));

            if (targetCoarse)
            {
                BlendedUpdate(perElemCoarseNs[slot], sample);
                // 粗样本累计（学习期 + 稳态都记，用于后续再分类）
                uint32_t c = coarseSamples[slot].load(std::memory_order_relaxed);
                while (c < UINT32_MAX &&
                       !coarseSamples[slot].compare_exchange_weak(c, c + 1,
                           std::memory_order_relaxed, std::memory_order_relaxed)) {}
            }
            else
            {
                BlendedUpdate(perElemEwmaNs[slot], sample);
                uint32_t f = fineSamples[slot].load(std::memory_order_relaxed);
                while (f < UINT32_MAX &&
                       !fineSamples[slot].compare_exchange_weak(f, f + 1,
                           std::memory_order_relaxed, std::memory_order_relaxed)) {}
                TryClassify(slot);
            }
        }

    private:
        static void BlendedUpdate(std::atomic<uint64_t>& ewma, uint64_t sample) noexcept
        {
            uint64_t oldVal = ewma.load(std::memory_order_relaxed);
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
                if (ewma.compare_exchange_weak(
                        oldVal, newVal, std::memory_order_relaxed, std::memory_order_relaxed))
                    break;
                // CAS 失败：oldVal 已更新为最新值，重算 blend
            }
        }

        // 粗/细样本都够了 → 用比值分类。
        void TryClassify(int slot) noexcept
        {
            const uint32_t coarse = coarseSamples[slot].load(std::memory_order_relaxed);
            const uint32_t fine = fineSamples[slot].load(std::memory_order_relaxed);
            if (coarse < kCoarseProbeSamples || fine < kMinFineSamples) return;

            const uint64_t cNs = perElemCoarseNs[slot].load(std::memory_order_relaxed);
            const uint64_t fNs = perElemEwmaNs[slot].load(std::memory_order_relaxed);
            if (cNs == 0 || fNs == 0) return;

            const double ratio = static_cast<double>(fNs) / static_cast<double>(cNs);
            const SlotMode newMode = (ratio > kMemBoundRatio) ? kModeMemBound : kModeParallel;
            // 只允许 unknown → learned 的单向跃迁；已学到的模式保持（避免抖动）。
            uint8_t cur = slotMode[slot].load(std::memory_order_relaxed);
            while (cur == kModeUnknown &&
                   !slotMode[slot].compare_exchange_weak(cur, static_cast<uint8_t>(newMode),
                       std::memory_order_relaxed, std::memory_order_relaxed)) {}
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