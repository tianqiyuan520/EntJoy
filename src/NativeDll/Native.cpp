#include "Native.h"
#include "JobSystem.h"

#include <iostream>

namespace {

struct AddArraysParallelContext {
    double* a = nullptr;
    double* b = nullptr;
    double* c = nullptr;
};

void AddArraysParallelWorker(void* ctx, int index) {
    auto* context = static_cast<AddArraysParallelContext*>(ctx);
    context->c[index] = context->a[index] + context->b[index];
}

} // namespace

HEAD void CallingConvention Test1()
{
    printf("call success\n");
}

EXTERNC void CallingConvention TestLog(char* log)
{
    printf("%s", log);
}

HEAD void CallingConvention AddArrays(double* a, double* b, double* c, int length)
{
    for (int i = 0; i < length; i++)
    {
        c[i] = a[i] + b[i];
    }
}

HEAD void CallingConvention AddArraysParallel(double* a, double* b, double* c, int length)
{
    AddArraysParallelContext context{ a, b, c };
    auto handle = JobSystem::Scheduler::ScheduleParallelFor(
        AddArraysParallelWorker,
        &context,
        length,
        0);
    handle.Complete();
}
