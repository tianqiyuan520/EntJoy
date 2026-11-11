#include <cstdint>

extern "C" __declspec(dllexport) uint32_t JobSystem_GetAbiVersion()
{
    return 4;
}
