#include "NativeContainers.h"

using namespace EntJoy::Collections;

extern "C" __declspec(dllexport) int RunAllocatorFixture(int label, bool dispose)
{
    UnsafeList<int> list{};
    list.Allocator = static_cast<Allocator>(label);
    for (int i = 0; i < 100; ++i) list.Add(i);
    for (int i = 0; i < 100; ++i)
        if (list.Ptr[i] != i) return 0;
    if (dispose) list.Dispose();
    return 1;
}
