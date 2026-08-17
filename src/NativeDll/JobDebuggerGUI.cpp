// ============================================================
// EntJoy JobSystem 调试面板（Dear ImGui，独立 Win32 + D3D11 窗口）
//
// 作为 NativeDll 的一个后台线程运行：创建自己的 HWND + D3D11 设备，
// 用 Dear ImGui 渲染 JobSystem 的实时 Worker 状态（哪个 worker 在跑哪个
// job / 进度条）与统计信息。数据直接读 JobSystem 的进程内全局原子状态，
// 零跨进程通信。
//
// 启用方式：运行游戏前设置环境变量 ENTJOY_DEBUG=1。
// JobSystem::Initialize() 内部调用 JobDebuggerGUI::TryLaunch()。
// ============================================================

#ifndef ENTJOY_IMGUI_ENABLED
// 若未通过 CMake 定义（例如直接编译 NativeDll.vcxproj 而未加 imgui 源），
// 本文件编译为空实现，不引入任何 imgui 依赖。
#define ENTJOY_IMGUI_ENABLED 0
#endif

#if ENTJOY_IMGUI_ENABLED

#include "imgui.h"
#include "backends/imgui_impl_win32.h"
#include "backends/imgui_impl_dx11.h"
#include "JobSystemInternal.h"
#include "JobDebuggerGUI.h"
#include "Exports.h"
#include <atomic>
#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstdarg>
#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <thread>
#include <unordered_map>
#include <vector>
#include <windows.h>
#include <d3d11.h>
#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")
// ImGui Win32 后端有意把 WndProcHandler 声明放在 '#if 0' 块里，要求使用者自行前置声明
extern IMGUI_IMPL_API LRESULT ImGui_ImplWin32_WndProcHandler(HWND hWnd, UINT msg, WPARAM wParam, LPARAM lParam);
#else
#include "JobDebuggerGUI.h"
#endif

namespace JobSystem
{
#if ENTJOY_IMGUI_ENABLED

    namespace
    {
        std::atomic<bool> g_guiLaunched{ false };
        std::atomic<bool> g_guiRunning{ false };

        // 窗口数据
        struct GuiState
        {
            ID3D11Device* device = nullptr;
            ID3D11DeviceContext* context = nullptr;
            IDXGISwapChain* swapChain = nullptr;
            ID3D11RenderTargetView* rtv = nullptr;
            HWND hwnd = nullptr;
            bool initializing = true;
        };

        GuiState* g_gui = nullptr;

        const wchar_t* const kWindowTitle = L"EntJoy JobSystem Monitor";

        // D3D11 初始化失败时最清晰的诊断
        void LogGui(const char* msg)
        {
            std::fprintf(stderr, "[EntJoy ImGui] %s\n", msg);
        }

        // ---- 数据快照（从 JobSystem 全局读） ----
        struct WorkerRow
        {
            int index;
            uint64_t batchId;
            uint32_t tile;
            uint32_t tileCount;
            bool active;
        };

        void CollectWorkerRows(std::vector<WorkerRow>& rows)
        {
            rows.clear();
            const int maxWorkers = CurrentWorkerCount();
            const int cap = maxWorkers < kMaxTrackedWorkers ? maxWorkers : kMaxTrackedWorkers;
            if (cap <= 0) return;
            rows.reserve(cap);
            for (int i = 0; i < cap; ++i)
            {
                WorkerRow r;
                r.index = i;
                r.batchId = g_workerCurrentBatchId[i].load(std::memory_order_relaxed);
                r.tile = g_workerCurrentTile[i].load(std::memory_order_relaxed);
                r.tileCount = g_workerBatchTileCount[i].load(std::memory_order_relaxed);
                r.active = g_workerIsActive[i].load(std::memory_order_relaxed);
                rows.push_back(r);
            }
        }

        // clamp 进度
        float SafeProgress(uint32_t tile, uint32_t count)
        {
            if (count == 0) return 0.0f;
            float p = static_cast<float>(tile) / static_cast<float>(count);
            return p < 0.0f ? 0.0f : (p > 1.0f ? 1.0f : p);
        }

        // ---- 监听：滚动活动日志 ----
        // 每个 worker 的 start/end/publish 以文本行形式记录，容量足够大避免早期 batch 被立即覆盖。
        struct ActivityEntry { char text[160]; };
        static constexpr int kActivityMax = 4096;
        static ActivityEntry g_activity[kActivityMax];
        static int g_activityHead = 0;   // 下一个写入位置
        static int g_activityCount = 0;  // 有效条数
        static uint64_t g_prevBatch[kMaxTrackedWorkers]{};
        static bool g_prevActive[kMaxTrackedWorkers]{};
        static uint64_t g_prevPublished = 0;
        static bool g_activityPrimed = false;

        static void LogActivity(const char* fmt, ...)
        {
            char buf[160];
            va_list ap;
            va_start(ap, fmt);
            vsnprintf(buf, sizeof(buf), fmt, ap);
            va_end(ap);
            snprintf(g_activity[g_activityHead].text, sizeof(g_activity[g_activityHead].text), "%s", buf);
            g_activityHead = (g_activityHead + 1) % kActivityMax;
            if (g_activityCount < kActivityMax) ++g_activityCount;
        }

        // "回看"：冻结实时刷新，把当前快照按在屏幕上慢慢看（job 瞬时完成后进度条会消失）
        static bool g_frozen = false;

        // ---- 时间线段（Timeline/Gantt 可视化数据）----
        // 每个 worker 的 job 段记录 (worker, batchId, startMs, endMs)。GUI 每帧从瞬时原子
        // 快照检测 worker 的 start/end 迁移，补上时间戳，供 Timeline 画带起止的彩色横条。
        double NowMs()
        {
            using namespace std::chrono;
            return duration_cast<duration<double, std::milli>>(steady_clock::now().time_since_epoch()).count();
        }

        constexpr int kSegmentMax = 16384; // 历史段容量（撑起最长达 120s 的回看窗）
        struct JobSegment { int worker; uint64_t batchId; double startMs; double endMs; uint32_t tiles; };
        static JobSegment g_segments[kSegmentMax];
        static int g_segHead = 0;   // 下一个写入槽
        static int g_segCount = 0;  // 有效段数
        static int g_openIdx[kMaxTrackedWorkers]; // worker 当前进行中的段下标，-1=无

        // ---- Timeline 交互状态（缩放/平移/暂停/点选）----
        static double g_winSpanMs = 8000.0;   // 可视窗长（滚轮缩放，200ms~120000ms）
        static double g_viewRightMs = 0.0;    // 暂停时最右端时间；0=跟随 now
        static bool   g_timelinePaused = false;
        static bool   g_clickDown = false;
        static bool   g_dragging = false;
        static ImVec2 g_clickDownPos{};
        static double g_dragBaseRight = 0.0;
        static JobSegment g_selected{};
        static bool   g_hasSelected = false;

        // "相对程序启动"的参考时间：GUI 线程首帧记录（原始值），详情页用偏移量显示
        static double g_guiBootMs = 0.0;
        static uint64_t g_guiBootPublished = 0;   // 面板打开时的 published 基准（Stats 显示增量）
        double ProcessElapsedMs(double rawMs) { return g_guiBootMs > 0.0 ? rawMs - g_guiBootMs : rawMs; }

        void StartSegment(int worker, uint64_t batchId, uint32_t tiles)
        {
            if (worker < 0 || worker >= kMaxTrackedWorkers) return;
            if (g_segCount == kSegmentMax)
            {
                const int old = g_segHead; // 淘汰最旧段（已是完成段）
                if (g_openIdx[g_segments[old].worker] == old)
                    g_openIdx[g_segments[old].worker] = -1;
            }
            else
            {
                ++g_segCount;
            }
            g_segments[g_segHead] = JobSegment{ worker, batchId, NowMs(), 0.0, tiles };
            g_openIdx[worker] = g_segHead;
            g_segHead = (g_segHead + 1) % kSegmentMax;
        }

        void EndSegment(int worker, uint32_t tiles)
        {
            if (worker < 0 || worker >= kMaxTrackedWorkers) return;
            const int idx = g_openIdx[worker];
            if (idx < 0) return;
            g_segments[idx].endMs = NowMs();
            g_segments[idx].tiles = tiles;
            g_openIdx[worker] = -1;
        }

        // batchId → 稳定彩色（HSV），Unity 风格区分不同 job
        ImU32 BatchColor(uint64_t id)
        {
            const uint32_t seed = (uint32_t)(id ^ (id >> 32));
            const float hue = (float)(seed % 360u) / 360.0f;
            const float sat = 0.75f, val = 0.85f;
            const int i = (int)(hue * 6.0f);
            const float f = hue * 6.0f - i;
            const float p = val * (1.0f - sat), q = val * (1.0f - sat * f), t = val * (1.0f - sat * (1.0f - f));
            float r, gx, b;
            switch (i % 6)
            {
            case 0: r = val; gx = t;   b = p;   break;
            case 1: r = q;   gx = val; b = p;   break;
            case 2: r = p;   gx = val; b = t;   break;
            case 3: r = p;   gx = q;   b = val; break;
            case 4: r = t;   gx = p;   b = val; break;
            default: r = val; gx = p;   b = q;   break;
            }
            return IM_COL32((int)(r * 255.0f), (int)(gx * 255.0f), (int)(b * 255.0f), 220);
        }

        // batchId → Job 名（优先原生直调名表，其次 C# 注册的解析器）。失败返回 "?"。
        std::string ResolveJobName(uint64_t batchId)
        {
            char nativeBuf[128];
            const int nn = ResolveNativeJobName(batchId, nativeBuf, (int)sizeof(nativeBuf));
            if (nn > 0) return std::string(nativeBuf, static_cast<size_t>(nn));
            const BatchJobNameResolver& resolver = JobSystem_GetNameResolver();
            if (!resolver) return "?";
            char buf[128];
            const int n = resolver(batchId, buf, (int)sizeof(buf));
            if (n <= 0) return "?";
            return std::string(buf, static_cast<size_t>(n));
        }

        void RecordActivity(const std::vector<WorkerRow>& rows)
        {
            if (!g_activityPrimed)
            {
                g_activityPrimed = true;
                std::memset(g_openIdx, -1, sizeof(g_openIdx));
                for (const auto& r : rows)
                {
                    if (r.index >= kMaxTrackedWorkers) continue;
                    g_prevActive[r.index] = r.active;
                    g_prevBatch[r.index] = r.batchId;
                }
                g_prevPublished = g_publishedJobs.load(std::memory_order_relaxed);
                return;
            }

            for (const auto& r : rows)
            {
                if (r.index >= kMaxTrackedWorkers) continue;
                const bool wasActive = g_prevActive[r.index];
                const uint64_t wasBatch = g_prevBatch[r.index];

                if (!wasActive && r.active)
                {
                    // worker 开始跑新 batch
                    const std::string nm = ResolveJobName(r.batchId);
                    LogActivity("W%d << #%llu %s", r.index, (unsigned long long)r.batchId, nm.c_str());
                    StartSegment(r.index, r.batchId, r.tileCount);
                }
                else if (wasActive && !r.active)
                {
                    // worker 结束一个片段
                    const std::string nm = ResolveJobName(wasBatch);
                    LogActivity("W%d >> #%llu %s (%u tiles)", r.index, (unsigned long long)wasBatch, nm.c_str(), r.tileCount);
                    EndSegment(r.index, r.tileCount);
                }
                else if (r.active && wasBatch != r.batchId)
                {
                    // worker 从旧 batch 切到新 batch
                    EndSegment(r.index, r.tileCount);
                    const std::string nm = ResolveJobName(r.batchId);
                    LogActivity("W%d -> #%llu %s", r.index, (unsigned long long)r.batchId, nm.c_str());
                    StartSegment(r.index, r.batchId, r.tileCount);
                }
                g_prevActive[r.index] = r.active;
                g_prevBatch[r.index] = r.batchId;
            }

            const uint64_t published = g_publishedJobs.load(std::memory_order_relaxed);
            if (published > g_prevPublished)
            {
                LogActivity("publish: %llu (+%llu)",
                            (unsigned long long)published, (unsigned long long)(published - g_prevPublished));
                g_prevPublished = published;
            }
        }

        // 消费原生发布事件，追加到 Activity 文本日志（完整覆盖微秒级 batch，不依赖 worker 采样）
        void DrainNativeActivity()
        {
            if (!g_nativeActivityCaptureEnabled.load(std::memory_order_relaxed)) return;
            static uint64_t readIndex = 0;
            NativeActivityEvent buf[64];
            for (;;)
            {
                const int n = ConsumePublishedJobs(buf, 64, &readIndex);
                if (n <= 0) break;
                for (int i = 0; i < n; ++i)
                {
                    const std::string nm = ResolveJobName(buf[i].batchId);
                    LogActivity("#%llu %s  tiles=%u", (unsigned long long)buf[i].batchId, nm.c_str(), buf[i].tiles);
                }
                if (n < 64) break;
            }
        }

        // ---- Timeline：Unity 风格 Gantt 泳道图 ----
        // 每条 worker 一条泳道，job 用彩色横条沿时间轴从左往右铺（start→end）。
        void DrawTimeline()
        {
            const double now = NowMs();
            const int lanes = CurrentWorkerCount();
            const int laneCount = lanes < kMaxTrackedWorkers ? lanes : kMaxTrackedWorkers;
            if (laneCount <= 0)
            {
                ImGui::Text("no workers");
                return;
            }

            // 最早一段的起点（限制左移越界）
            double minSegStart = now;
            for (int i = 0; i < g_segCount; ++i)
            {
                const int idx = (g_segHead - g_segCount + kSegmentMax * 2 + i) % kSegmentMax;
                if (g_segments[idx].startMs < minSegStart) minSegStart = g_segments[idx].startMs;
            }

            const float labelW = 48.0f;
            const float laneH = 22.0f;
            const ImVec2 avail = ImGui::GetContentRegionAvail();
            float plotW = avail.x - labelW;
            float plotH = laneCount * laneH + 16.0f;
            if (plotH > avail.y - 88.0f) plotH = avail.y - 88.0f;
            if (plotW < 20.0f) plotW = 20.0f;
            if (plotH < 20.0f) plotH = 20.0f;

            const ImVec2 o = ImGui::GetCursorScreenPos();
            const ImVec2 mouse = ImGui::GetIO().MousePos;
            const bool hovered = (mouse.x >= o.x + labelW && mouse.x <= o.x + labelW + plotW &&
                                  mouse.y >= o.y && mouse.y <= o.y + plotH);

            // 实时时右端贴 now；暂停后固定 viewRight
            if (!g_timelinePaused) g_viewRightMs = now;

            const double span0 = g_winSpanMs;
            double winStartBase = g_viewRightMs - span0;

            // 缩放：仅 Ctrl+滚轮，且以鼠标位置为锚点居中缩放（不做参考点，光标处时间保持不动）
            const float wheel = ImGui::GetIO().MouseWheel;
            const bool ctrlHeld = ImGui::GetIO().KeyCtrl;
            if (hovered && ctrlHeld && wheel != 0.0f)
            {
                const double frac = (double)(mouse.x - (o.x + labelW)) / (double)plotW;
                const double anchorTime = winStartBase + frac * span0;
                const double newSpan = std::clamp(span0 * std::pow(2.0, -(double)wheel), 200.0, 120000.0);
                const double newStart = anchorTime - frac * newSpan;
                g_winSpanMs = newSpan;
                g_viewRightMs = newStart + newSpan;
                g_timelinePaused = true; // 手动缩放即脱离实时跟随，便于观察
            }

            // 平移（拖拽）：左键按住拖动进入暂停态；松开且几乎没动 → 点选。
            // 用"按下点与当前点距离"判拖拽（>=3px），比 MouseDelta 累加灵敏稳定。
            if (ImGui::IsMouseClicked(ImGuiMouseButton_Left) && hovered)
            {
                g_clickDown = true;
                g_clickDownPos = mouse;
                g_dragging = false;
                g_dragBaseRight = g_viewRightMs; // 记录拖拽基点（按下时的时间线右端）
            }
            if (g_clickDown && ImGui::IsMouseDown(ImGuiMouseButton_Left))
            {
                const float distX = mouse.x - g_clickDownPos.x;
                const float distY = mouse.y - g_clickDownPos.y;
                if (distX * distX + distY * distY >= 3.0f * 3.0f)
                {
                    if (!g_dragging)
                    {
                        g_dragging = true;
                        g_dragBaseRight = g_viewRightMs;
                    }
                    g_timelinePaused = true;
                    // 恒定相对"拖拽基点"平移，松开即停（不累积）
                    g_viewRightMs = g_dragBaseRight + (double)(g_clickDownPos.x - mouse.x) / (double)plotW * g_winSpanMs;
                }
            }

            double viewRight = g_viewRightMs;
            double winStart = viewRight - g_winSpanMs;
            if (winStart < minSegStart) { winStart = minSegStart; viewRight = winStart + g_winSpanMs; }
            const double span = g_winSpanMs;
            if (span <= 0.0) return;

            // 命中测试 & 点选
            auto pick = [&](const ImVec2& p) -> bool
            {
                for (int i = 0; i < g_segCount; ++i)
                {
                    const int idx = (g_segHead - g_segCount + kSegmentMax * 2 + i) % kSegmentMax;
                    const JobSegment& seg = g_segments[idx];
                    if (seg.worker < 0 || seg.worker >= laneCount) continue;
                    const double end = seg.endMs > 0 ? seg.endMs : now;
                    if (end < winStart || seg.startMs > viewRight) continue;
                    const double s = seg.startMs < winStart ? winStart : seg.startMs;
                    const double e = end > viewRight ? viewRight : end;
                    if (e <= s) continue;
                    const float x0 = o.x + labelW + (float)((s - winStart) / span) * plotW;
                    const float x1 = o.x + labelW + (float)((e - winStart) / span) * plotW;
                    const float y0 = o.y + seg.worker * laneH + 2.0f;
                    const float bh = laneH - 4.0f;
                    if (p.x >= x0 && p.x <= x1 && p.y >= y0 && p.y <= y0 + bh)
                    {
                        g_selected = seg;
                        if (g_selected.endMs <= 0.0) g_selected.endMs = now;
                        g_hasSelected = true;
                        return true;
                    }
                }
                return false;
            };
            if (ImGui::IsMouseReleased(ImGuiMouseButton_Left))
            {
                if (g_clickDown && !g_dragging)
                    pick(g_clickDownPos);
                g_clickDown = false;
                g_dragging = false;
            }

            ImDrawList* dl = ImGui::GetWindowDrawList();
            dl->AddRectFilled(o, ImVec2(o.x + labelW + plotW, o.y + plotH), IM_COL32(18, 18, 22, 255));

            // 垂直网格：按 zoom 自适应间距
            double step = 100.0;
            if (span > 20000.0) step = 2000.0;
            else if (span > 5000.0) step = 500.0;
            else if (span < 1500.0) step = 50.0;
            for (double t = winStart + span - std::fmod(winStart, step); t >= winStart; t -= step)
            {
                const float x = o.x + labelW + (float)((t - winStart) / span) * plotW;
                dl->AddLine(ImVec2(x, o.y), ImVec2(x, o.y + plotH), IM_COL32(60, 60, 70, 80));
            }

            // 彩色作业条
            for (int i = 0; i < g_segCount; ++i)
            {
                const int idx = (g_segHead - g_segCount + kSegmentMax * 2 + i) % kSegmentMax;
                const JobSegment& seg = g_segments[idx];
                if (seg.worker < 0 || seg.worker >= laneCount) continue;
                const double end = seg.endMs > 0 ? seg.endMs : now;
                if (end < winStart || seg.startMs > viewRight) continue;
                const double s = seg.startMs < winStart ? winStart : seg.startMs;
                const double e = end > viewRight ? viewRight : end;
                if (e <= s) continue;
                const float x0 = o.x + labelW + (float)((s - winStart) / span) * plotW;
                const float x1 = o.x + labelW + (float)((e - winStart) / span) * plotW;
                const float y0 = o.y + seg.worker * laneH + 2.0f;
                const float bh = laneH - 4.0f;
                dl->AddRectFilled(ImVec2(x0, y0), ImVec2(x1, y0 + bh), BatchColor(seg.batchId));
                dl->AddRect(ImVec2(x0, y0), ImVec2(x1, y0 + bh), IM_COL32(0, 0, 0, 120));

                // 条够宽时在条内画 Job 名（微秒级条极窄，缩到足够窗长才显示；Workers 页的 Job 列兜底）
                if (x1 - x0 > 48.0f)
                {
                    const std::string nm = ResolveJobName(seg.batchId);
                    if (nm != "?")
                    {
                        const ImVec2 ts = ImGui::CalcTextSize(nm.c_str());
                        if (ts.x + 8.0f < (x1 - x0))
                        {
                            dl->AddText(ImVec2(x0 + 4.0f, y0 + (bh - ts.y) * 0.5f),
                                        IM_COL32(255, 255, 255, 230), nm.c_str());
                        }
                    }
                }
            }

            // 选中高亮
            if (g_hasSelected)
            {
                const JobSegment& seg = g_selected;
                const double end = seg.endMs > 0 ? seg.endMs : now;
                const double s = seg.startMs < winStart ? winStart : seg.startMs;
                const double e = end > viewRight ? viewRight : end;
                if (e > s && seg.worker >= 0 && seg.worker < laneCount)
                {
                    const float x0 = o.x + labelW + (float)((s - winStart) / span) * plotW;
                    const float x1 = o.x + labelW + (float)((e - winStart) / span) * plotW;
                    const float y0 = o.y + seg.worker * laneH + 2.0f;
                    dl->AddRect(ImVec2(x0, y0), ImVec2(x1, y0 + laneH - 4.0f),
                                IM_COL32(255, 255, 255, 255), 0.0f, 0, 2.0f);
                }
            }

            // 泳道标签 + 横向分隔
            for (int w = 0; w < laneCount; ++w)
            {
                const float y0 = o.y + w * laneH;
                dl->AddLine(ImVec2(o.x + labelW, y0), ImVec2(o.x + labelW + plotW, y0), IM_COL32(60, 60, 70, 80));
                char lb[16];
                snprintf(lb, sizeof(lb), "W%d", w);
                const ImVec2 ts = ImGui::CalcTextSize(lb);
                dl->AddText(ImVec2(o.x + (labelW - ts.x) * 0.5f, y0 + (laneH - ts.y) * 0.5f),
                            IM_COL32(210, 210, 215, 255), lb);
            }

            // 底轴时间标签（左=窗长，右=now）
            ImGui::SetCursorScreenPos(ImVec2(o.x + labelW, o.y + plotH + 2.0f));
            ImGui::TextColored(ImVec4(0.6f, 0.6f, 0.6f, 1.0f), "-%.0fs", span / 1000.0);
            ImGui::SameLine(o.x + labelW + plotW - 46.0f);
            ImGui::TextColored(ImVec4(0.85f, 0.85f, 0.85f, 1.0f), g_timelinePaused ? "paused" : "now");

            ImGui::SetCursorScreenPos(ImVec2(o.x, o.y + plotH + 22.0f));

            // 点选详情
            if (g_hasSelected)
            {
                const JobSegment& s = g_selected;
                const double durMs = s.endMs - s.startMs;
                const std::string nm = ResolveJobName(s.batchId);
                // 路径开销：提交→首 worker 的典型 EWMA（从全局快照读个近似）；无则显示 "-"
                JobSystemStatsSnapshot ss;
                GetStatsSnapshot(&ss);
                const double s2fUs = (double)ss.submitToFirstWorkerEwmaNs / 1000.0;
                const double execUs = (double)ss.perRangeExecEwmaNs / 1000.0;

                ImGui::TextColored(ImVec4(0.3f, 0.8f, 1.0f, 1.0f), "Selected Job");
                ImGui::Separator();
                ImGui::Text("Job      : %s", nm.c_str());
                ImGui::Text("Worker   : W%d", s.worker);
                ImGui::Text("Batch    : #%llu", (unsigned long long)s.batchId);
                ImGui::Text("Duration : %.2f ms", durMs);
                ImGui::Text("Tiles    : %u (本 worker 认领的工作切片数)", s.tiles);
                ImGui::Text("Range    : %.3f ~ %.3f ms (相对程序启动)", ProcessElapsedMs(s.startMs), ProcessElapsedMs(s.endMs));
                ImGui::Separator();
                ImGui::TextColored(ImVec4(0.8f, 0.8f, 0.8f, 1.0f), "调度路径开销 (EWMA)");
                ImGui::Text("Submit→首 Worker : %.1f us", s2fUs);
                ImGui::Text("单 Range 执行    : %.1f us", execUs);
                ImGui::Spacing();
            }
        }

        void DrawGuiFrame()
        {
            DrainNativeActivity(); // 消费原生发布事件，完整记录每个 batch

            static std::vector<WorkerRow> rows;
            if (!g_frozen)
            {
                CollectWorkerRows(rows);
                RecordActivity(rows);
            }

            ImGui::SetNextWindowPos(ImVec2(0, 0));
            ImGui::SetNextWindowSize(ImGui::GetIO().DisplaySize);
            ImGui::Begin("EntJoy JobSystem Monitor",
                nullptr,
                ImGuiWindowFlags_NoDecoration | ImGuiWindowFlags_NoMove |
                ImGuiWindowFlags_NoResize | ImGuiWindowFlags_NoBringToFrontOnFocus |
                ImGuiWindowFlags_NoNav | ImGuiWindowFlags_NoSavedSettings);

            ImGui::Checkbox("Freeze (hold frame to inspect)", &g_frozen);
            if (ImGui::IsItemHovered())
                ImGui::SetTooltip("暂停实时刷新：job 瞬时完成后进度条会消失，勾选后把当前快照按在屏幕上慢慢回看。");
            ImGui::Separator();

            if (ImGui::BeginTabBar("MainTabs"))
            {
                // ---------- Stats tab ----------
                if (ImGui::BeginTabItem("Stats"))
                {
                    JobSystemStatsSnapshot s;
                    GetStatsSnapshot(&s);

                    ImGui::TextColored(ImVec4(0.3f, 0.8f, 1.0f, 1.0f), "JobSystem Statistics");
                    ImGui::Separator();
                    ImGui::Spacing();

                    if (ImGui::BeginTable("stats", 2,
                        ImGuiTableFlags_Borders | ImGuiTableFlags_RowBg | ImGuiTableFlags_SizingStretchProp))
                    {
                        ImGui::TableSetupColumn("Metric", ImGuiTableColumnFlags_WidthStretch, 1);
                        ImGui::TableSetupColumn("Value", ImGuiTableColumnFlags_WidthStretch, 1);
                        ImGui::TableHeadersRow();

                        auto srow = [](const char* k, const char* v) {
                            ImGui::TableNextRow();
                            ImGui::TableSetColumnIndex(0); ImGui::Text("%s", k);
                            ImGui::TableSetColumnIndex(1); ImGui::Text("%s", v);
                        };
                        char buf[64];
                        snprintf(buf, sizeof(buf), "%llu", (unsigned long long)s.publishedJobs); srow("Published Jobs", buf);
                        snprintf(buf, sizeof(buf), "%llu", (unsigned long long)s.totalTilesPublished); srow("Tiles Total", buf);
                        snprintf(buf, sizeof(buf), "%llu", (unsigned long long)s.localTiles); srow("Tiles Local", buf);
                        snprintf(buf, sizeof(buf), "%llu", (unsigned long long)s.stolenTiles); srow("Tiles Stolen", buf);
                        snprintf(buf, sizeof(buf), "%llu", (unsigned long long)s.assistTiles); srow("Tiles Assist", buf);
                        snprintf(buf, sizeof(buf), "%llu", (unsigned long long)s.activeWorkersPeak); srow("Active Peak", buf);
                        snprintf(buf, sizeof(buf), "%llu", (unsigned long long)s.stealAttempts); srow("Steal Attempts", buf);
                        snprintf(buf, sizeof(buf), "%llu", (unsigned long long)s.stealSuccesses); srow("Steal Success", buf);
                        snprintf(buf, sizeof(buf), "%llu", (unsigned long long)s.parkWakeCount); srow("Park/Wake", buf);
                        snprintf(buf, sizeof(buf), "%.1fus", (double)s.wakeLatencyEwmaNs / 1000.0); srow("Wake Latency", buf);
                        snprintf(buf, sizeof(buf), "%.1fus", (double)s.perRangeExecEwmaNs / 1000.0); srow("Per-Range Exec", buf);
                        snprintf(buf, sizeof(buf), "%llu%%", (unsigned long long)s.assistExecPctEwma); srow("Assist %", buf);

                        ImGui::EndTable();
                    }

                    ImGui::EndTabItem();
                }

                // ---------- Timeline tab：Unity 风格泳道时间线 ----------
                if (ImGui::BeginTabItem("Timeline"))
                {
                    // 工具栏：实时/暂停 + 快捷窗长
                    if (ImGui::Button(g_timelinePaused ? "Resume (live)" : "Pause (hold)"))
                        g_timelinePaused = !g_timelinePaused;
                    ImGui::SameLine();
                    if (ImGui::Button("Live")) { g_timelinePaused = false; g_viewRightMs = 0.0; g_winSpanMs = 8000.0; }
                    ImGui::SameLine();
                    ImGui::SetNextItemWidth(110.0f);
                    const char* presets[] = { "0.5s", "1s", "2s", "4s", "8s", "15s", "30s", "60s", "120s" };
                    static int presetIdx = 4; // 8s
                    const double presetMs[] = { 500, 1000, 2000, 4000, 8000, 15000, 30000, 60000, 120000 };
                    if (ImGui::Combo("##span", &presetIdx, presets, IM_ARRAYSIZE(presets)))
                        g_winSpanMs = presetMs[presetIdx];
                    ImGui::SameLine();
                    ImGui::Text("%s | Ctrl+wheel=zoom, drag=pan, click=inspect",
                                g_timelinePaused ? "Paused" : "Live");

                    ImGui::Separator();
                    ImGui::Spacing();
                    DrawTimeline();
                    ImGui::EndTabItem();
                }

                // ---------- Activity tab：滚动事件日志 ----------
                if (ImGui::BeginTabItem("Activity"))
                {
                    ImGui::TextColored(ImVec4(0.3f, 0.8f, 1.0f, 1.0f), "Recent Activity (rolling %d)", kActivityMax);
                    ImGui::Separator();
                    ImGui::Spacing();

                    const int start = (g_activityHead - g_activityCount + kActivityMax * 2) % kActivityMax;
                    for (int i = 0; i < g_activityCount; ++i)
                    {
                        const ActivityEntry& e = g_activity[(start + i) % kActivityMax];
                        ImGui::TextUnformatted(e.text);
                    }
                    ImGui::EndTabItem();
                }

                ImGui::EndTabBar();
            }

            ImGui::End();
        }

        // ---- Win32 消息循环 ----
        LRESULT WINAPI WndProc(HWND hWnd, UINT msg, WPARAM wParam, LPARAM lParam)
        {
            // ImGui Win32 后端要求先处理输入
            if (g_gui)
                ImGui_ImplWin32_WndProcHandler(hWnd, msg, wParam, lParam);

            switch (msg)
            {
            case WM_SIZE:
                if (g_gui && wParam != SIZE_MINIMIZED)
                {
                    g_gui->context->OMSetRenderTargets(0, nullptr, nullptr);
                    if (g_gui->rtv) { g_gui->rtv->Release(); g_gui->rtv = nullptr; }
                    g_gui->swapChain->ResizeBuffers(0, LOWORD(lParam), HIWORD(lParam), DXGI_FORMAT_UNKNOWN, 0);
                    // 重建 RTV
                    ID3D11Texture2D* backBuffer = nullptr;
                    g_gui->swapChain->GetBuffer(0, __uuidof(ID3D11Texture2D), (void**)&backBuffer);
                    if (backBuffer)
                    {
                        g_gui->device->CreateRenderTargetView(backBuffer, nullptr, &g_gui->rtv);
                        backBuffer->Release();
                    }
                    g_gui->context->OMSetRenderTargets(1, &g_gui->rtv, nullptr);
                    D3D11_VIEWPORT vp = { 0, 0, (float)LOWORD(lParam), (float)HIWORD(lParam), 0.0f, 1.0f };
                    g_gui->context->RSSetViewports(1, &vp);
                }
                return 0;
            case WM_DESTROY:
                PostQuitMessage(0);
                return 0;
            }
            return DefWindowProc(hWnd, msg, wParam, lParam);
        }

        bool InitD3D(HWND hwnd, UINT width, UINT height, GuiState& st)
        {
            DXGI_SWAP_CHAIN_DESC sd = {};
            sd.BufferCount = 2;
            sd.BufferDesc.Width = width;
            sd.BufferDesc.Height = height;
            sd.BufferDesc.Format = DXGI_FORMAT_R8G8B8A8_UNORM;
            sd.BufferDesc.RefreshRate.Numerator = 60;
            sd.BufferDesc.RefreshRate.Denominator = 1;
            sd.Flags = DXGI_SWAP_CHAIN_FLAG_ALLOW_MODE_SWITCH;
            sd.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
            sd.OutputWindow = hwnd;
            sd.SampleDesc.Count = 1;
            sd.SampleDesc.Quality = 0;
            sd.Windowed = TRUE;
            sd.SwapEffect = DXGI_SWAP_EFFECT_DISCARD;

            D3D_FEATURE_LEVEL featureLevel;
            D3D_FEATURE_LEVEL levels[] = { D3D_FEATURE_LEVEL_11_0, D3D_FEATURE_LEVEL_10_0 };
            HRESULT hr = D3D11CreateDeviceAndSwapChain(
                nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr,
                0, levels, 2, D3D11_SDK_VERSION,
                &sd, &st.swapChain, &st.device, &featureLevel, &st.context);
            if (FAILED(hr))
            {
                // 回退 WARP（无硬件加速环境）
                hr = D3D11CreateDeviceAndSwapChain(
                    nullptr, D3D_DRIVER_TYPE_WARP, nullptr,
                    0, levels, 2, D3D11_SDK_VERSION,
                    &sd, &st.swapChain, &st.device, &featureLevel, &st.context);
                if (FAILED(hr)) return false;
            }

            ID3D11Texture2D* backBuffer = nullptr;
            st.swapChain->GetBuffer(0, __uuidof(ID3D11Texture2D), (void**)&backBuffer);
            if (!backBuffer) return false;
            st.device->CreateRenderTargetView(backBuffer, nullptr, &st.rtv);
            backBuffer->Release();

            st.context->OMSetRenderTargets(1, &st.rtv, nullptr);
            D3D11_VIEWPORT vp = { 0, 0, (float)width, (float)height, 0.0f, 1.0f };
            st.context->RSSetViewports(1, &vp);
            return true;
        }

        void CleanupGui(GuiState& st)
        {
            if (st.rtv) { st.rtv->Release(); st.rtv = nullptr; }
            if (st.context) { st.context->Release(); st.context = nullptr; }
            if (st.swapChain) { st.swapChain->Release(); st.swapChain = nullptr; }
            if (st.device) { st.device->Release(); st.device = nullptr; }
        }

        void GuiThreadMain()
        {
            GuiState st;
            g_gui = &st;
            g_guiRunning.store(true, std::memory_order_release);
            if (g_guiBootMs == 0.0) g_guiBootMs = NowMs(); // 记录"相对程序启动"基准
            if (g_guiBootPublished == 0)
                g_guiBootPublished = g_publishedJobs.load(std::memory_order_relaxed); // 面板打开起的 published 基准

            HINSTANCE hInstance = GetModuleHandleW(nullptr);

            WNDCLASSEXW wc = {};
            wc.cbSize = sizeof(wc);
            wc.style = CS_CLASSDC;
            wc.lpfnWndProc = WndProc;
            wc.hInstance = hInstance;
            wc.lpszClassName = L"EntJoyJobSystemDebug";
            RegisterClassExW(&wc);

            HWND hwnd = CreateWindowW(wc.lpszClassName, kWindowTitle,
                WS_OVERLAPPEDWINDOW, 40, 40, 900, 620,
                nullptr, nullptr, hInstance, nullptr);
            if (!hwnd)
            {
                LogGui("CreateWindowW failed");
                UnregisterClassW(wc.lpszClassName, hInstance);
                JobSystem_ClearNameResolver();
                g_nativeActivityCaptureEnabled.store(false, std::memory_order_release);
                ClearPublishedJobs();
                g_guiRunning.store(false, std::memory_order_release);
                g_gui = nullptr;
                return;
            }
            st.hwnd = hwnd;

            if (!InitD3D(hwnd, 900, 620, st))
            {
                LogGui("D3D11 initialization failed; debug window disabled");
                DestroyWindow(hwnd);
                UnregisterClassW(wc.lpszClassName, hInstance);
                JobSystem_ClearNameResolver();
                g_nativeActivityCaptureEnabled.store(false, std::memory_order_release);
                ClearPublishedJobs();
                g_guiRunning.store(false, std::memory_order_release);
                g_gui = nullptr;
                return;
            }

            IMGUI_CHECKVERSION();
            ImGui::CreateContext();
            ImGuiIO& io = ImGui::GetIO();
            io.ConfigFlags |= ImGuiConfigFlags_NavEnableKeyboard;
            // 优先加载系统微软雅黑（支持中文 UI），失败才回退到默认字体
            ImFontConfig fontCfg;
            fontCfg.SizePixels = 20.0f;   // 重新栅格化大字号，避免放大默认位图发糊
            fontCfg.OversampleH = 3;
            fontCfg.OversampleV = 3;
            const char* yaheiPaths[] = {
                "C:\\Windows\\Fonts\\msyh.ttc",
                "C:\\Windows\\Fonts\\msyhbd.ttc",
                "C:\\Windows\\Fonts\\simhei.ttf",
            };
            ImFont* uiFont = nullptr;
            for (const char* p : yaheiPaths)
            {
                uiFont = io.Fonts->AddFontFromFileTTF(p, 20.0f, &fontCfg, io.Fonts->GetGlyphRangesChineseFull());
                if (uiFont) break;
            }
            if (!uiFont)
            {
                io.Fonts->AddFontDefault(&fontCfg);
                LogGui("Warning: system CJK font not found; Chinese labels will show as '?'");
            }
            io.FontGlobalScale = 1.0f;

            ImGui_ImplWin32_Init(hwnd);
            ImGui_ImplDX11_Init(st.device, st.context);

            // 深色主题
            ImGui::StyleColorsDark();
            ImGuiStyle& style = ImGui::GetStyle();
            style.WindowRounding = 0.0f;

            ShowWindow(hwnd, SW_SHOWDEFAULT);
            UpdateWindow(hwnd);

            MSG msg;
            bool running = true;
            while (running)
            {
                while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE))
                {
                    TranslateMessage(&msg);
                    DispatchMessageW(&msg);
                    if (msg.message == WM_QUIT) running = false;
                }
                if (!running) break;

                // 数据来自 JobSystem，无需额外拉取；每帧渲染
                ImGui_ImplDX11_NewFrame();
                ImGui_ImplWin32_NewFrame();
                ImGui::NewFrame();
                DrawGuiFrame();
                ImGui::Render();

                const float clear[4] = { 0.10f, 0.10f, 0.13f, 1.0f };
                st.context->OMSetRenderTargets(1, &st.rtv, nullptr);
                st.context->ClearRenderTargetView(st.rtv, clear);
                ImGui_ImplDX11_RenderDrawData(ImGui::GetDrawData());

                st.swapChain->Present(1, 0); // vsync
            }

            ImGui_ImplDX11_Shutdown();
            ImGui_ImplWin32_Shutdown();
            ImGui::DestroyContext();

            JobSystem_ClearNameResolver();
            g_nativeActivityCaptureEnabled.store(false, std::memory_order_release);
            ClearPublishedJobs();
            CleanupGui(st);
            DestroyWindow(hwnd);
            UnregisterClassW(wc.lpszClassName, hInstance);

            g_guiRunning.store(false, std::memory_order_release);
            g_gui = nullptr;
        }

    } // namespace

    void JobDebuggerGUI::Launch()
    {
        // 强制启动并开始监听（C# 直接调用，不依赖 ENTJOY_DEBUG）。幂等。
        if (g_guiLaunched.exchange(true, std::memory_order_acq_rel)) return;
        if (g_guiRunning.load(std::memory_order_acquire)) return;

        g_nativeActivityCaptureEnabled.store(true, std::memory_order_release); // 开启原生发布事件采集

        try
        {
            std::thread t(GuiThreadMain);
            t.detach();
            LogGui("launched debug window (forced from C#)");
        }
        catch (...)
        {
            LogGui("failed to spawn debug thread");
            g_nativeActivityCaptureEnabled.store(false, std::memory_order_release);
        }
    }

    void JobDebuggerGUI::TryLaunch()
    {
        // 旧路径：仅当 ENTJOY_DEBUG=1 时自动启动。未设置时直接返回，
        // 且不再占用一次性标志，以便随后仍可由 C# JobDebuggerGUI_Launch 强制启动。
        std::string env;
#if defined(_WIN32)
        char* raw = nullptr;
        if (_dupenv_s(&raw, nullptr, "ENTJOY_DEBUG") == 0 && raw) { env.assign(raw); std::free(raw); }
#else
        if (const char* raw = std::getenv("ENTJOY_DEBUG")) env.assign(raw);
#endif
        bool enabled = env == "1" || env == "true" || env == "on";
        if (!enabled)
        {
            LogGui("skipped (ENTJOY_DEBUG not set)");
            return;
        }
        Launch();
    }

    void JobDebuggerGUI::Shutdown()
    {
        // 后台线程检测到 WM_QUIT 时自行退出；此处可补充停止路径
        //（当前 detach；进程退出时窗口随之销毁）
    }

#else // !ENTJOY_IMGUI_ENABLED

    void JobDebuggerGUI::TryLaunch()
    {
        // imgui 未编译进 DLL（未设 ENTJOY_IMGUI_ENABLED），无操作
    }

    void JobDebuggerGUI::Launch()
    {
        // imgui 未编译进 DLL（未设 ENTJOY_IMGUI_ENABLED），无操作
    }

    void JobDebuggerGUI::Shutdown()
    {
    }

#endif

} // namespace JobSystem
