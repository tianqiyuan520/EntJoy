// 模拟「完全旧版」NativeDll：没有 JobSystem_GetAbiVersion 导出。
// LoadNativeDll 应检测「ABI export 缺失」并释放 DLL、回退 Managed。
extern "C" {
    __declspec(dllexport) int EntJoy_StubMarker() { return 0; }
}
