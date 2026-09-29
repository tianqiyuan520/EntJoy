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
#include <cstdio>
#include <cstdlib>

namespace JobSystem
{
    // 诊断开关（定义在 JobSystem.cpp：`ENTJOY_JCC_VERBOSE=1`）。
    // 前置声明：本头在 JobSystemInternal.h:15 被包含，早于该 extern 的正式声明（L152）⇒ 必须在此可见。
    extern bool g_jobCostCacheVerbose;

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
    // 阈值 >1.0：perElem 改为纯执行口径后，compute-bound job 的细/粗分块
    // perElem 比值天然 ≈1（总执行量相同），0.85 会把 light/medium job 全误判为
    // mem-bound（tiles 固定 tpw，JCC 自适应失效）。1.15 = 细比粗慢 15% 才判带宽受限
    //（真 memory-bound 如 GridSearch 空间哈希 gather，多 tile 争带宽 ratio 远大于此）。
    constexpr double kMemBoundRatio = 1.15;

    // ── 健壮分类（`ENTJOY_JCC_ROBUST=1`；动机见 07 §7an 实测，默认关 ⇒ 关闭时逐位不变）──
    // 旧判据（kCoarseProbeSamples=3 / kMinFineSamples=2 + unknown→learned 单向锁死）在真实负载上不可靠：
    //   · 同一 job 相邻批的 execSpan 实测摆动 **3408µs ↔ 594µs（5.7×）**（`tools/gate-run/jcc/`）⇒ 2 个细样本
    //     就定终身的分类等于掷骰子；锁死后 mem-bound 分支**永不产出细样本** ⇒ 无法恢复。
    //   · 旧代码把"公式产出的 15 tiles"也记成细样本（比 tpw 的 60 还粗 4×）⇒ 系统性推高比值 ⇒ 全判 mem-bound。
    // 改法：① 每类环形窗取**中位数**（抢占只增不减 ⇒ 中位稳健）；② 每类 ≥16 样本才判；
    //   ③ 细/粗标注诚化（chunk 必须严格小于 tpw chunk 才算细）；④ 双向 + 迟滞 + 冷却；
    //   ⑤ mem-bound 期**周期探针**（每 32 次解析放一次公式分块），保证还能采到新证据。
    constexpr int kRobustWindow = 24;
    constexpr int kRobustMinSamples = 8;
    constexpr uint32_t kRobustCooldown = 16;
    constexpr uint32_t kRobustProbeInterval = 32;
    constexpr double kRobustParallelRatio = 0.95;   // 中位比 < 0.95 ⇒ 细粒度确实更快 ⇒ parallel
    constexpr double kRobustMemRatio = 1.15;        // 中位比 > 1.15 ⇒ mem-bound
    // ── 第二观测量：**每元素批墙钟**（墙钟口径守卫）──
    // 动机：`perElem = execSpan/N` 只量"首 tile 开始 → 末 tile 完成"，**不含**唤醒/认领/退役/令牌收尾；
    // 一个 job 完全可能"execSpan 变好、批墙钟变差"⇒ 单观测量会给错判决。
    // ── 已撤的第二观测量（记录，勿重做）──
    // 曾试过把"每元素**批墙钟**"（(finalize−publish)/N）作为第二道门槛（parallel 必须 exec<0.95 且 wall<0.98）。
    // 12 对 A/B 实测：Melee **由 +4.37 恶化到 +7.67（11/12）**、整步 **+8.08**。原因：wall 含唤醒+令牌收尾+退役链，
    // 对高频小 job 噪声极大 ⇒ parallel↔memBound 反复抖动，每批换一种粒度。**已回退**（见 07 §7an(c2)）。
    constexpr double kRobustScale = 1000.0;         // 定点：ns × 1000（uint32 可表 4.29e6 ns/元素）

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
        // 每 tile 固定开销（Q22 定点，单位 ns）—— 两因子模型第二因子：
        // 单 tile 执行时间 ≈ C_fixed + tileSize×C_elem。退役时从
        // execSpan = (tiles/wc)×C_fixed + (N/wc)×C_elem 反解，EWMA 学习。
        // 空体/超轻 job 的 C_elem≈0、C_fixed 主导 → 单因子 perElem 模型失效，
        // 必须显式建模 C_fixed（ManyJobsBench 8K/64K/1M 最优 tiles 各异的原因）。
        std::atomic<uint64_t> perTileEwmaNs[kJobCostSlots];
        // funcPtr hash 校验（碰撞时复用 → 重学）
        std::atomic<uint32_t> slotHash[kJobCostSlots];
        // 分类模式（学习期 / parallel / memory-bound）
        std::atomic<uint8_t> slotMode[kJobCostSlots];
        // 已采样的粗/细批次数（学习用）
        std::atomic<uint32_t> coarseSamples[kJobCostSlots];
        std::atomic<uint32_t> fineSamples[kJobCostSlots];

        // 健壮分类窗（仅 g_jobCostCache.IsRobust() 时写入/读取；关闭时零额外开销）
        uint32_t fineRingNs[kJobCostSlots][kRobustWindow];
        uint32_t coarseRingNs[kJobCostSlots][kRobustWindow];
        std::atomic<uint32_t> lastEvalTick[kJobCostSlots];   // 上次评估时的样本序号（冷却用）
        std::atomic<uint32_t> probeTick[kJobCostSlots];      // mem-bound 期探针计数

        // `ENTJOY_JCC_ROBUST=1` 时启用健壮分类；进程启动读一次，之后只读。
        bool robustMode = []() -> bool {
            const char* v = std::getenv("ENTJOY_JCC_ROBUST");
            return v != nullptr && v[0] == '1';
        }();
        bool IsRobust() const noexcept { return robustMode; }

        JobCostCache() noexcept { Init(); }

        void Init() noexcept
        {
            for (int i = 0; i < kJobCostSlots; ++i)
            {
                perElemEwmaNs[i].store(0, std::memory_order_relaxed);
                perElemCoarseNs[i].store(0, std::memory_order_relaxed);
                perTileEwmaNs[i].store(0, std::memory_order_relaxed);
                slotHash[i].store(0, std::memory_order_relaxed);
                slotMode[i].store(kModeUnknown, std::memory_order_relaxed);
                coarseSamples[i].store(0, std::memory_order_relaxed);
                fineSamples[i].store(0, std::memory_order_relaxed);
                lastEvalTick[i].store(0, std::memory_order_relaxed);
                probeTick[i].store(0, std::memory_order_relaxed);
                for (int j = 0; j < kRobustWindow; ++j)
                {
                    fineRingNs[i][j] = 0;
                    coarseRingNs[i][j] = 0;
                }
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

        // 每 tile 固定开销（C_fixed）读取：两因子决策用。0 = 未学习。
        double GetPerTileCost(uint32_t funcHash) const noexcept
        {
            const int slot = funcHash & (kJobCostSlots - 1);
            if (slotHash[slot].load(std::memory_order_relaxed) == funcHash)
            {
                return static_cast<double>(
                    perTileEwmaNs[slot].load(std::memory_order_relaxed))
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
        //   perElemNs   ：本次 batch 的每元素**执行跨度**成本（execSpan/N）
        //   tileCount   ：本次 batch 的 tile 数（≈ 并行粒度）
        //   targetCoarse：本次是否是粗粒度（tpw）分块（调度侧判定后传入）
        void UpdatePerElemCost(uint32_t funcHash, double perElemNs,
                               bool targetCoarse) noexcept
        {
            if (perElemNs < 0.0) return;
            const int slot = funcHash & (kJobCostSlots - 1);
            slotHash[slot].store(funcHash, std::memory_order_relaxed);
            const uint64_t sample = static_cast<uint64_t>(perElemNs * static_cast<double>(kJobCostQ22));

            uint32_t tick = 0;
            if (targetCoarse)
            {
                BlendedUpdate(perElemCoarseNs[slot], sample);
                // 粗样本累计（学习期 + 稳态都记，用于后续再分类）
                uint32_t c = coarseSamples[slot].load(std::memory_order_relaxed);
                while (c < UINT32_MAX &&
                       !coarseSamples[slot].compare_exchange_weak(c, c + 1,
                           std::memory_order_relaxed, std::memory_order_relaxed)) {}
                tick = c + 1;
                if (robustMode) RobustStore(coarseRingNs[slot], c, perElemNs);
            }
            else
            {
                BlendedUpdate(perElemEwmaNs[slot], sample);
                uint32_t f = fineSamples[slot].load(std::memory_order_relaxed);
                while (f < UINT32_MAX &&
                       !fineSamples[slot].compare_exchange_weak(f, f + 1,
                           std::memory_order_relaxed, std::memory_order_relaxed)) {}
                tick = f + 1;
                if (robustMode) RobustStore(fineRingNs[slot], f, perElemNs);
            }
            // 健壮模式：旧的一次性单向锁死判据被"中位数 + 双向迟滞"取代；否则保持原语义逐位不变。
            if (robustMode) RobustReclassify(slot, tick);
            else TryClassify(slot);
        }

        // mem-bound 期周期探针：每 kRobustProbeInterval 次解析放行一次"公式分块"，
        // 否则锁死后再也采不到细样本、永远无法纠错。单槽原子自增，无锁。
        bool ProbeDue(uint32_t funcHash) noexcept
        {
            const int slot = funcHash & (kJobCostSlots - 1);
            const uint32_t t = probeTick[slot].fetch_add(1, std::memory_order_relaxed) + 1;
            return (t % kRobustProbeInterval) == 0;
        }

        // 未分类期**交错**探针：奇偶交替放行"公式/细"与"tpw/粗"。
        // 没有它，重 job 的粗样本会永远停在两阶段学习的 3 个上 ⇒ 够不到 kRobustMinSamples
        // ⇒ mode 永为 unknown ⇒ 永远走公式（永远细粒度），使 Melee 受损而收益被抵消。
        bool ParityProbe(uint32_t funcHash) noexcept
        {
            const int slot = funcHash & (kJobCostSlots - 1);
            const uint32_t t = probeTick[slot].fetch_add(1, std::memory_order_relaxed);
            return (t & 1u) != 0u;
        }

        // 健壮判据当前的中位比（<=0 表示样本不足）。诊断用（`ENTJOY_JCC_VERBOSE` 路径）。
        double RobustRatio(uint32_t funcHash) noexcept
        {
            const int slot = funcHash & (kJobCostSlots - 1);
            const uint32_t fc = fineSamples[slot].load(std::memory_order_relaxed);
            const uint32_t cc = coarseSamples[slot].load(std::memory_order_relaxed);
            if (fc < kRobustMinSamples || cc < kRobustMinSamples) return 0.0;
            const uint32_t cm = MedianOfWindow(coarseRingNs[slot], cc);
            if (cm == 0) return 0.0;
            return static_cast<double>(MedianOfWindow(fineRingNs[slot], fc)) / static_cast<double>(cm);
        }

        // 更新每 tile 固定开销（C_fixed）EWMA（α=0.75，与 perElem 同策略）。
        // 退役侧反解后调用；无细/粗之分（固定开销与分块粒度无关）。
        void UpdatePerTileCost(uint32_t funcHash, double perTileNs) noexcept
        {
            if (perTileNs <= 0.0) return;
            const int slot = funcHash & (kJobCostSlots - 1);
            slotHash[slot].store(funcHash, std::memory_order_relaxed);
            const uint64_t sample = static_cast<uint64_t>(perTileNs * static_cast<double>(kJobCostQ22));
            BlendedUpdate(perTileEwmaNs[slot], sample);
        }

    private:
        // 写环形窗（定点 uint32，超出上限钳位）。写位置 = 该类样本序号 % W；
        // 多线程同槽并发写入是良性的（漏记一个样本无正确性影响）。
        static void RobustStore(uint32_t* ring, uint32_t idx, double perElemNs) noexcept
        {
            double s = perElemNs * kRobustScale;
            if (s < 0.0) s = 0.0;
            if (s > 4294967000.0) s = 4294967000.0;
            ring[idx % static_cast<uint32_t>(kRobustWindow)] = static_cast<uint32_t>(s);
        }

        // 窗内中位数。count < W 时只取已填充的 [0,count)；count >= W 时整个窗就是最近 W 个样本
        // （中位数与顺序无关 ⇒ 回绕无需重排）。
        static uint32_t MedianOfWindow(const uint32_t* w, uint32_t count) noexcept
        {
            const int n = (count >= static_cast<uint32_t>(kRobustWindow))
                ? kRobustWindow : static_cast<int>(count);
            if (n <= 0) return 0;
            uint32_t tmp[kRobustWindow];
            for (int i = 0; i < n; ++i) tmp[i] = w[i];
            for (int i = 1; i < n; ++i)
            {
                const uint32_t v = tmp[i];
                int j = i - 1;
                while (j >= 0 && tmp[j] > v) { tmp[j + 1] = tmp[j]; --j; }
                tmp[j + 1] = v;
            }
            return tmp[n / 2];
        }

        // 健壮重分类：样本够 + 冷却到点才评估；双向（含 unknown→learned）并带迟滞带
        // [kRobustParallelRatio, kRobustMemRatio] 内保持现状（避免抖动）。
        void RobustReclassify(int slot, uint32_t tick) noexcept
        {
            const uint32_t fc = fineSamples[slot].load(std::memory_order_relaxed);
            const uint32_t cc = coarseSamples[slot].load(std::memory_order_relaxed);
            if (fc < kRobustMinSamples || cc < kRobustMinSamples) return;
            uint32_t last = lastEvalTick[slot].load(std::memory_order_relaxed);
            if (tick - last < kRobustCooldown) return;
            if (!lastEvalTick[slot].compare_exchange_strong(last, tick,
                    std::memory_order_relaxed, std::memory_order_relaxed)) return;

            const uint32_t cm = MedianOfWindow(coarseRingNs[slot], cc);
            if (cm == 0) return;
            const uint32_t fm = MedianOfWindow(fineRingNs[slot], fc);
            const double ratio = static_cast<double>(fm) / static_cast<double>(cm);

            // 第二观测量（wallRatio）已撤：墙钟含唤醒/令牌收尾，对高频小 job 噪声极大，
            // 加进判决会让 parallel↔memBound 反复抖动（实测 Melee +7.67、整步 +8.08，见 07 §7an(c2)）。
            // ⇒ 判决只用 execSpan 口径的中位比。
            SlotMode cur = static_cast<SlotMode>(slotMode[slot].load(std::memory_order_relaxed));
            SlotMode want = cur;
            if (ratio < kRobustParallelRatio) want = kModeParallel;
            else if (ratio > kRobustMemRatio) want = kModeMemBound;
            // 中性带：只有"细粒度确实更快"才配得到细粒度；看不出正收益时保守取粗（= 现状 tpw）。
            else if (cur == kModeUnknown) want = kModeMemBound;
            if (want != cur)
            {
                slotMode[slot].store(static_cast<uint8_t>(want), std::memory_order_relaxed);
                if (g_jobCostCacheVerbose)
                    std::printf("[JCC-ROBUST] slot=%d hash=%08x mode %d→%d ratio=%.3f"
                                " (fineMed=%.3fns coarseMed=%.3fns fc=%u cc=%u)\n",
                        slot, slotHash[slot].load(std::memory_order_relaxed),
                        static_cast<int>(cur), static_cast<int>(want), ratio,
                        static_cast<double>(fm) / kRobustScale,
                        static_cast<double>(cm) / kRobustScale, fc, cc);
            }
        }

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