// 模拟「版本不匹配」NativeDll：JobSystem_GetAbiVersion 返回非预期版本 999。
// LoadNativeDll 应检测「ABI 不匹配」并释放 DLL、回退 Managed。
extern "C" {
    __declspec(dllexport) unsigned int JobSystem_GetAbiVersion() { return 999u; }
    __declspec(dllexport) int EntJoy_StubMarker() { return 0; }
}
