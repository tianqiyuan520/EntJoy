#pragma once

// EntJoy JobSystem 调试面板（Dear ImGui）入口声明。
// 实现在 JobDebuggerGUI.cpp。当 DLL 以 ENTJOY_IMGUI_ENABLED 编译且运行时
// ENTJOY_DEBUG=1 时，JobSystem::Initialize() 会启动后台 ImGui 调试窗口。

namespace JobSystem
{
    class JobDebuggerGUI
    {
    public:
        // 检查 ENTJOY_DEBUG=1 并启动调试窗口（幂等）
        static void TryLaunch();
        // 强制启动调试窗口并开始监听（C# 直接调用，不依赖 ENTJOY_DEBUG 环境变量；幂等）
        static void Launch();
        // 请求停止（当前 detach，窗口关闭即回收）
        static void Shutdown();
    };
}
