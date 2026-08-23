#pragma once
// CPU pause hint — used by ChaseLevScheduler, JobSystem_State, NativeWorkerPool
// Extracted to header to avoid ODR violations in unity build
static inline void CpuPause() noexcept
{
#if defined(_MSC_VER) && (defined(_M_IX86) || defined(_M_X64))
    _mm_pause();
#elif defined(__x86_64__) || defined(_M_X64) || defined(__i386__)
    __asm__ __volatile__("pause");
#else
    std::atomic_signal_fence(std::memory_order_seq_cst);
#endif
}
