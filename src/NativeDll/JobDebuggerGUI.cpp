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

        // ---- 监听：滚动活动日志 ----
        // 每个发布事件以文本行形式记录（来自原生发布事件流），容量足够大避免早期 batch 被立即覆盖。
        struct ActivityEntry { char text[160]; };
        static constexpr int kActivityMax = 4096;
        static ActivityEntry g_activity[kActivityMax];
        static int g_activityHead = 0;   // 下一个写入位置
        static int g_activityCount = 0;  // 有效条数

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

        // ---- 时间线段（Timeline/Gantt 可视化数据）----
        // ---- Timeline 数据：直接渲染共享时间线历史 ----
        // Job 的 start/end 由原生侧在事件发生时记录（DebugBeginExec/DebugEndExec 在
        // 执行瞬间把完整窗口追加进 g_debugSegments），GUI 只读渲染，不采样、不迁移检测。
        double NowMs()
        {
            using namespace std::chrono;
            return duration_cast<duration<double, std::milli>>(steady_clock::now().time_since_epoch()).count();
        }

        // ---- Timeline 交互状态（缩放/平移/暂停/点选）----
        static double g_winSpanMs = 8000.0;   // 可视窗长（滚轮缩放，200ms~120000ms）
        static double g_viewRightMs = 0.0;    // 暂停时最右端时间；0=跟随 now
        static bool   g_timelinePaused = false;
        static bool   g_clickDown = false;
        static bool   g_dragging = false;
        static ImVec2 g_clickDownPos{};
        static double g_dragBaseRight = 0.0;
        static DebugSegment g_selected{};
        static bool   g_hasSelected = false;
        static bool   g_pauseFrozen = false;

        // "相对程序启动"的参考时间：GUI 线程首帧记录（原始值），详情页用偏移量显示
        static double g_guiBootMs = 0.0;
        static uint64_t g_guiBootPublished = 0;   // 面板打开时的 published 基准（Stats 显示增量）
        double ProcessElapsedMs(double rawMs) { return g_guiBootMs > 0.0 ? rawMs - g_guiBootMs : rawMs; }

        // 按耗时着色：越短越绿（0ms → 亮绿），越长越暖（→ 黄 → 橙红）。
        // 用对数轴归一，短耗时对比更明显。
        ImU32 DurationColor(double startMs, double endMs)
        {
            const double durMs = endMs - startMs;
            double h = durMs <= 0.0 ? 0.0 : (std::log1p(durMs) / std::log1p(10.0));
            if (h > 1.0) h = 1.0;
            if (h < 0.0) h = 0.0;
            // 色相从绿(120°)渐变到红(0°)：色彩沿 120→0，饱和度/亮度固定。
            const int hue = (int)((1.0 - h) * 120.0); // 120(绿)..0(红)
            const float sat = 0.80f, val = 0.90f;
            const float f = (float)hue / 60.0f;
            const int i = (int)f;
            const float fr = f - i;
            const float p = val * (1.0f - sat), q = val * (1.0f - sat * fr), t = val * (1.0f - sat * (1.0f - fr));
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

        // 消费原生发布事件，追加到 Activity 文本日志（完整覆盖微秒级 batch，事件驱动）
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
        // 每条 worker 一条泳道（末条 M = 主/调用线程），job 用彩色横条沿时间轴铺。
        // 时间窗口模型：一窗 [winLeft, winRight] 映射到固定可视宽。拖拽平移 winRight、
        // Ctrl+滚轮缩放 span（锚点保持）、可选横向滚动条也驱动 winRight——解耦后始终灵敏。
        // 直调（isDirect）标记 [D]；ISPC MT 任务的条画在 W 泳道（连续编号，不单独用 T 泳道）。
        // 条色由耗时决定：越短越绿、越长越暖（红）。
        void DrawTimeline()
        {
            const double now = NowMs();
            const int lanes = CurrentWorkerCount();
            // 泳道：W(*lanes) + M(1) + ISPC 泳道（连续编号排到 M 之后，标签显示为 W<idx>）
            const int jobLaneCount = (lanes < kMaxTrackedWorkers - 1 ? lanes : kMaxTrackedWorkers - 1) + 1;
            const int ispcLaneCount = DebugIspcLaneCount();
            const int laneCount = jobLaneCount + ispcLaneCount;
            if (laneCount <= 0)
            {
                ImGui::Text("no workers");
                return;
            }
            // seg.lane → 行号：W/M 区原样（0..jobLaneCount-1，M 在 lanes 处）；ISPC 保留区排到 jobLaneCount 之后
            auto rowOf = [&](int lane) -> int {
                if (lane < 0) return -1;
                if (lane < kIspcLaneBase) return lane;
                return jobLaneCount + (lane - kIspcLaneBase);
            };

            // 共享历史快照
            const unsigned int visible = g_debugSegVisible.load(std::memory_order_acquire);
            const unsigned int segCount = visible < (unsigned int)kDebugSegmentMax ? visible : (unsigned int)kDebugSegmentMax;
            const unsigned int startSlot = visible - segCount;

            // 历史最晚时间（用于实时跟随右界 / 暂停锚点）
            double tLatest = now;
            for (unsigned int i = 0; i < segCount; ++i)
            {
                const double e = g_debugSegments[(startSlot + i) % kDebugSegmentMax].endMs;
                if (e > tLatest) tLatest = e;
            }

            // ---- 时间窗口 [winStart, winEnd]：实时跟随 now 或暂停固定 ----
            double viewRight = g_timelinePaused ? g_viewRightMs : now;
            if (g_timelinePaused)
            {
                // 首次暂停冻结右界（避免 now 推进导致窗口漂移）
                if (!g_pauseFrozen)
                {
                    g_viewRightMs = tLatest > now ? tLatest : now;
                    g_pauseFrozen = true;
                }
                else
                {
                    viewRight = g_viewRightMs;
                }
            }
            else
            {
                g_pauseFrozen = false;
                g_viewRightMs = now;
            }
            double winStart = viewRight - g_winSpanMs;
            const double span = g_winSpanMs;
            if (span <= 0.0) return;

            const float labelW = 48.0f;
            const float laneH = 22.0f;
            const ImVec2 avail = ImGui::GetContentRegionAvail();
            const float plotW = avail.x - labelW - 12.0f; // 时间绘图区宽（给竖向滚动条留一点）
            const float contentH = laneCount * laneH;
            float viewH = avail.y - 118.0f;               // 底部给横向滑块 + 详情
            if (viewH > contentH) viewH = contentH;
            if (viewH < 40.0f) viewH = 40.0f;

            // 时间 → x（固定映射到可视宽，与缩放解耦）
            auto mapX = [&](double t)->float {
                return (float)((t - winStart) / span) * plotW;
            };

            const float sbw = ImGui::GetStyle().ScrollbarSize + 6.0f;
            const float canvasH = viewH; // 泳道画布可视高（竖向滚动用）

            // 泳道画布：child 承担竖向滚动（泳道过多时可滚）；横向时间窗由拖拽/缩放/底部滑块控制。
            // SetNextWindowContentSize 告知 child 内容高度=所有泳道，竖向滚动条才能滚到后面的 T# 泳道。
            ImGui::SetNextWindowContentSize(ImVec2(plotW, contentH));
            ImGui::BeginChild("tlLanes", ImVec2(avail.x, canvasH), false, ImGuiWindowFlags_AlwaysVerticalScrollbar);
            {
                // 泳道内容原点（屏幕坐标）。显式加竖向滚动偏移，使绘制与命中(y)始终对齐：
                // 滚动后 GetCursorScreenPos 的 y 不可靠，改为 winPos - scrollY。
                const ImVec2 winPos = ImGui::GetWindowPos();
                const float scrollY = ImGui::GetScrollY();
                const ImVec2 o = ImVec2(winPos.x, winPos.y - scrollY);
                const ImVec2 mouse = ImGui::GetIO().MousePos;
                // 命中区：必须用可视窗口的屏幕范围（winPos..winPos+canvasH），与滚动无关。
                // 滚动后 o.y=winPos.y-scrollY 会整体上移，用它做命中区会把下方泳道排除在外。
                const bool hovered = (mouse.y >= winPos.y && mouse.y <= winPos.y + canvasH - (contentH > canvasH ? sbw : 0.0f) &&
                                      mouse.x >= winPos.x + labelW && mouse.x <= winPos.x + labelW + plotW - (plotW < avail.x - labelW ? sbw : 0.0f));

                // ---- 缩放：Ctrl+滚轮，锚点=鼠标所在时间，缩放后保持锚点时间在屏幕相对位置 ----
                const float wheel = ImGui::GetIO().MouseWheel;
                const bool ctrlHeld = ImGui::GetIO().KeyCtrl;
                if (hovered && ctrlHeld && wheel != 0.0f)
                {
                    const double anchorFrac = (double)((mouse.x - (o.x + labelW)) / plotW);
                    const double anchorT = winStart + anchorFrac * span;
                    const double newSpan = std::clamp(span * std::pow(2.0, -(double)wheel), 200.0, 120000.0);
                    g_winSpanMs = newSpan;
                    g_viewRightMs = anchorT + (1.0 - anchorFrac) * newSpan;
                    g_timelinePaused = true; // 手动缩放即脱离实时跟随
                    // 注意：此处绝不能 return —— BeginChild 尚未 EndChild，提前 return 会破坏 ImGui 栈
                }

                // ---- 拖拽平移：左键拖动改 viewRight；松开且几乎没动 → 点选 ----
                if (ImGui::IsMouseClicked(ImGuiMouseButton_Left) && hovered)
                {
                    g_clickDown = true;
                    g_clickDownPos = mouse;
                    g_dragging = false;
                    g_dragBaseRight = g_viewRightMs;
                }
                if (g_clickDown && ImGui::IsMouseDown(ImGuiMouseButton_Left))
                {
                    const float distX = mouse.x - g_clickDownPos.x;
                    const float distY = mouse.y - g_clickDownPos.y;
                    if (distX * distX + distY * distY >= 3.0f * 3.0f)
                    {
                        if (!g_dragging) { g_dragging = true; g_dragBaseRight = g_viewRightMs; }
                        g_timelinePaused = true;
                        g_viewRightMs = g_dragBaseRight + (double)(g_clickDownPos.x - mouse.x) / plotW * span;
                    }
                }

                // 命中测试 & 点选
                auto pick = [&](const ImVec2& p) -> bool
                {
                    for (unsigned int i = 0; i < segCount; ++i)
                    {
                        const DebugSegment& seg = g_debugSegments[(startSlot + i) % kDebugSegmentMax];
                        const int row = rowOf(seg.lane);
                        if (row < 0) continue;
                        if (seg.endMs < winStart || seg.startMs > viewRight) continue;
                        const double s = seg.startMs < winStart ? winStart : seg.startMs;
                        const double e = seg.endMs > viewRight ? viewRight : seg.endMs;
                        if (e <= s) continue;
                        const float x0 = o.x + labelW + mapX(s), x1 = o.x + labelW + mapX(e);
                        const float y0 = o.y + row * laneH + 2.0f;
                        const float bh = laneH - 4.0f;
                        if (p.x >= x0 && p.x <= x1 && p.y >= y0 && p.y <= y0 + bh)
                        {
                            g_selected = seg;
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
                dl->AddRectFilled(o, ImVec2(o.x + labelW + plotW, o.y + contentH), IM_COL32(18, 18, 22, 255));

                // 垂直网格
                double step = 100.0;
                if (span > 20000.0) step = 2000.0;
                else if (span > 5000.0) step = 500.0;
                else if (span < 1500.0) step = 50.0;
                for (double t = winStart + span - std::fmod(winStart, step); t >= winStart; t -= step)
                {
                    const float x = o.x + labelW + mapX(t);
                    dl->AddLine(ImVec2(x, o.y), ImVec2(x, o.y + contentH), IM_COL32(60, 60, 70, 80));
                }

                // 彩色作业条（色由耗时决定：越短越绿、越长越暖）
                for (unsigned int i = 0; i < segCount; ++i)
                {
                    const DebugSegment& seg = g_debugSegments[(startSlot + i) % kDebugSegmentMax];
                    const int row = rowOf(seg.lane);
                    if (row < 0) continue;
                    if (seg.endMs < winStart || seg.startMs > viewRight) continue;
                    const double s = seg.startMs < winStart ? winStart : seg.startMs;
                    const double e = seg.endMs > viewRight ? viewRight : seg.endMs;
                    if (e <= s) continue;
                    const float x0 = o.x + labelW + mapX(s), x1 = o.x + labelW + mapX(e);
                    const float y0 = o.y + row * laneH + 2.0f;
                    const float bh = laneH - 4.0f;
                    ImU32 col = seg.isDirect
                        ? IM_COL32(120, 200, 255, 150)                 // 直调：蓝青半透明
                        : DurationColor(seg.startMs, seg.endMs);       // Job：按耗时着色
                    dl->AddRectFilled(ImVec2(x0, y0), ImVec2(x1, y0 + bh), col);
                    dl->AddRect(ImVec2(x0, y0), ImVec2(x1, y0 + bh), IM_COL32(0, 0, 0, 120));

                    if (x1 - x0 > 48.0f)
                    {
                        const std::string nm = ResolveJobName(seg.batchId);
                        if (!nm.empty() && nm != "?")
                        {
                            std::string disp = seg.isDirect ? ("[D]" + nm) : nm;
                            const ImVec2 ts = ImGui::CalcTextSize(disp.c_str());
                            if (ts.x + 8.0f < (x1 - x0))
                                dl->AddText(ImVec2(x0 + 4.0f, y0 + (bh - ts.y) * 0.5f),
                                            IM_COL32(255, 255, 255, 235), disp.c_str());
                        }
                    }
                }

                // 选中高亮
                if (g_hasSelected)
                {
                    const DebugSegment& seg = g_selected;
                    const int selRow = rowOf(seg.lane);
                    if (selRow >= 0 && seg.endMs >= winStart && seg.startMs <= viewRight)
                    {
                        const double s = seg.startMs < winStart ? winStart : seg.startMs;
                        const double e = seg.endMs > viewRight ? viewRight : seg.endMs;
                        if (e > s)
                        {
                            const float y0 = o.y + selRow * laneH + 2.0f;
                            dl->AddRect(ImVec2(o.x + labelW + mapX(s), y0),
                                        ImVec2(o.x + labelW + mapX(e), y0 + laneH - 4.0f),
                                        IM_COL32(255, 255, 255, 255), 0.0f, 0, 2.0f);
                        }
                    }
                }

                // 泳道标签 + 横向分隔（row → 标签：前 W 区 W#，M，其后为 ISPC 任务 T#）
                for (int r = 0; r < laneCount; ++r)
                {
                    const float y0 = o.y + r * laneH;
                    dl->AddLine(ImVec2(o.x + labelW, y0), ImVec2(o.x + labelW + plotW, y0), IM_COL32(60, 60, 70, 80));
                    char lb[16];
                    if (r == lanes) // M 泳道（index = CurrentWorkerCount）
                        snprintf(lb, sizeof(lb), "M");
                    else if (r < lanes)
                        snprintf(lb, sizeof(lb), "W%d", r);
                    else
                        snprintf(lb, sizeof(lb), "T%d", r - lanes - 1); // ISPC ConcRT 任务泳道
                    const ImVec2 ts = ImGui::CalcTextSize(lb);
                    dl->AddText(ImVec2(o.x + (labelW - ts.x) * 0.5f, y0 + (laneH - ts.y) * 0.5f),
                                IM_COL32(210, 210, 215, 255), lb);
                }
            }
            ImGui::EndChild();

            // ---- 底部横向滑块：驱动时间平移（保留横向滚时间轴，但与拖拽/缩放统一走 winStart/span）----
            if (segCount > 0)
            {
                double tMin = winStart, tMax = tLatest > now ? tLatest : now;
                for (unsigned int i = 0; i < segCount; ++i)
                {
                    const double s = g_debugSegments[(startSlot + i) % kDebugSegmentMax].startMs;
                    if (s < tMin) tMin = s;
                }
                if (tMax - tMin < span) tMax = tMin + span;
                float frac = (float)((winStart - tMin) / (tMax - tMin));
                ImGui::SetNextItemWidth(avail.x - 20.0f);
                if (ImGui::SliderFloat("##tspan", &frac, 0.0f, 1.0f, "view"))
                {
                    g_viewRightMs = tMin + frac * (tMax - tMin) + span;
                    if (!g_timelinePaused) g_timelinePaused = true; // 手动滑即脱离实时跟随
                }
                ImGui::SameLine();
                ImGui::Text("  -%.1fs", span / 1000.0);
            }

            // 点选详情（child 外）
            if (g_hasSelected)
            {
                const DebugSegment& s = g_selected;
                const double durMs = s.endMs - s.startMs;
                const std::string nm = ResolveJobName(s.batchId);
                const int row = rowOf(s.lane);
                std::string whereName;
                if (s.lane >= kIspcLaneBase)
                    whereName = "T" + std::to_string(row - lanes - 1) + " (ISPC ConcRT 任务线程)";
                else if (s.lane == lanes)
                    whereName = "M (调用线程)";
                else
                    whereName = "W" + std::to_string(s.lane) + " (worker线程)";

                ImGui::TextColored(ImVec4(0.3f, 0.8f, 1.0f, 1.0f), "Selected %s", s.isDirect ? "Direct Call" : "Job");
                ImGui::Separator();
                ImGui::Text("Name     : %s%s", s.isDirect ? "[D]" : "", nm.c_str());
                ImGui::Text("Where    : %s", whereName.c_str());
                ImGui::Text("Batch    : #%llu", (unsigned long long)s.batchId);
                ImGui::Text("Duration : %.2f ms", durMs);
                if (s.isDirect)
                    ImGui::Text("Tasks    : %u (直调并行度)", s.tiles);
                else
                    ImGui::Text("Tiles    : %u (此 worker 实际领取执行)", s.tiles);
                ImGui::Text("Range    : %.3f ~ %.3f ms (相对程序启动)", ProcessElapsedMs(s.startMs), ProcessElapsedMs(s.endMs));
                if (!s.isDirect && s.workers > 1)
                    ImGui::Text("整批占用 : %u 个 worker 并行执行", s.workers);
                ImGui::Spacing();
            }
        }

        void DrawGuiFrame()
        {
            DrainNativeActivity(); // 消费原生发布事件，完整记录每个 batch

            ImGui::SetNextWindowPos(ImVec2(0, 0));
            ImGui::SetNextWindowSize(ImGui::GetIO().DisplaySize);
            ImGui::Begin("EntJoy JobSystem Monitor",
                nullptr,
                ImGuiWindowFlags_NoDecoration | ImGuiWindowFlags_NoMove |
                ImGuiWindowFlags_NoResize | ImGuiWindowFlags_NoBringToFrontOnFocus |
                ImGuiWindowFlags_NoNav | ImGuiWindowFlags_NoSavedSettings);

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
                        // 与 Activity/Timeline 同口径：显示面板打开以来的增量（发布即计数，
                        // 不含面板开启前已在跑的 batch），另附累计值供参考。
                        const uint64_t pubDelta = s.publishedJobs >= g_guiBootPublished
                            ? s.publishedJobs - g_guiBootPublished : s.publishedJobs;
                        snprintf(buf, sizeof(buf), "+%llu (total %llu)",
                                 (unsigned long long)pubDelta, (unsigned long long)s.publishedJobs);
                        srow("Published Jobs", buf);
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
                    ImGui::Text("%s | Ctrl+wheel=zoom, drag=pan, click=inspect | M=main/caller",
                                g_timelinePaused ? "Paused" : "Live");

                    ImGui::Separator();
                    ImGui::Spacing();
                    DrawTimeline();
                    ImGui::EndTabItem();
                }

                // ---------- Activity tab：执行窗口（worker: job）+ 发布事件 ----------
                if (ImGui::BeginTabItem("Activity"))
                {
                    // 执行窗口：事件驱动记录，每条 = worker 泳道上的一个 Job/直调执行
                    ImGui::TextColored(ImVec4(0.3f, 0.8f, 1.0f, 1.0f),
                                       "执行窗口 (Wxx: JobName   耗时   tiles   worker数)   [D]=直调");
                    ImGui::Separator();
                    const unsigned int visible = g_debugSegVisible.load(std::memory_order_acquire);
                    const unsigned int totalSegs = visible < (unsigned int)kDebugSegmentMax ? visible : (unsigned int)kDebugSegmentMax;
                    const unsigned int shownSegs = totalSegs < 2048u ? totalSegs : 2048u; // 只画最近 2048 条
                    const unsigned int segStart = visible - shownSegs;
                    const int actLanes = CurrentWorkerCount();
                    for (unsigned int i = 0; i < shownSegs; ++i)
                    {
                        const DebugSegment& seg = g_debugSegments[(segStart + i) % kDebugSegmentMax];
                        char lb[16];
                        if (seg.lane >= kIspcLaneBase)
                            snprintf(lb, sizeof(lb), "T%02d", seg.lane - kIspcLaneBase); // ISPC 任务线程
                        else if (seg.lane >= 0 && seg.lane < actLanes)
                            snprintf(lb, sizeof(lb), "W%02d", seg.lane);
                        else
                            snprintf(lb, sizeof(lb), "M");
                        const std::string nm = seg.batchId != 0 ? ResolveJobName(seg.batchId) : std::string("?");
                        const char* prefix = seg.isDirect ? "[D]" : "";
                        if (seg.workers > 0)
                            ImGui::Text("%s: %s%s   %.2f ms  |  tiles=%u  workers=%u",
                                        lb, prefix, nm.c_str(),
                                        seg.endMs - seg.startMs, seg.tiles, seg.workers);
                        else
                            ImGui::Text("%s: %s%s   %.2f ms",
                                        lb, prefix, nm.c_str(), seg.endMs - seg.startMs);
                    }
                    ImGui::Spacing();

                    ImGui::Separator();
                    ImGui::TextColored(ImVec4(0.3f, 0.8f, 1.0f, 1.0f), "发布事件 (rolling %d)", kActivityMax);
                    ImGui::Separator();
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

            // 统一高 DPI 面板：让调试窗口在任何宿主进程(控制台/Godot)下字体物理大小一致。
            // 用 io.FontGlobalScale(绘制时缩放)按窗口 DPI 放大字体，而不是加大 SizePixels——
            // 中文图集(微软雅黑全字符)在放大字号下会变成巨型图集，导致 NewFrame 构建失败崩溃。
            // 注：本 vendored imgui 无 DpiEnableScaleFonts flag，故手动按窗口 DPI 设 FontGlobalScale。
            UINT winDpi = 96;
            if (HMODULE user32 = GetModuleHandleW(L"user32.dll"))
            {
                typedef UINT(WINAPI* GetDpiForWindowFn)(HWND);
                auto getDpi = (GetDpiForWindowFn)GetProcAddress(user32, "GetDpiForWindow");
                if (getDpi) { UINT d = getDpi(hwnd); if (d >= 96) winDpi = d; }
            }
            const float dpiScale = (float)winDpi / 96.0f;

            ImFontConfig fontCfg;
            fontCfg.SizePixels = 20.0f;   // 基础字号：图集保持小型，避免大字号栅格化崩溃
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
            io.FontGlobalScale = dpiScale;

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
