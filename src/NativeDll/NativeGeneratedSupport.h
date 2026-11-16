#pragma once

// NativeTranspiler 生成代码的公共前导：导出/原子/内联宏，chunk 上下文与事件缓冲结构体。
// 与手写代码的分工：本文件只服务生成物；NativeContainers.h / NativeMath.h / ChunkJobData.h
// 仍由生成物按需 include。

#ifdef __cplusplus
#define EXTERNC extern "C"
#else
#define EXTERNC
#endif

#ifdef _WIN32
#define CALLINGCONVENTION __cdecl
#ifdef DLL_IMPORT
#define HEAD EXTERNC __declspec(dllimport)
#define GENERATED_API EXTERNC __declspec(dllimport)
#else
// NativeDll 核心导出（默认 dllexport，旧语义保留）
#define HEAD EXTERNC __declspec(dllexport)
// NativeTranspiled.dll 专用导出宏（生成代码独立 DLL）。
// GENERATED_EXPORTS 由 CMake 对 NativeTranspiled 目标定义 → dllexport；被包含声明时 → dllimport。
#ifdef GENERATED_EXPORTS
#define GENERATED_API EXTERNC __declspec(dllexport)
#else
#define GENERATED_API EXTERNC __declspec(dllimport)
#endif
#endif
#else
#define CALLINGCONVENTION
#define HEAD EXTERNC
#define GENERATED_API EXTERNC
#endif

// 生成的分量指针不加 __restrict：别名判定未逐处完成，宏保持空定义。
#define RESTRICT

// 跨编译器原子操作宏。MSVC 用 _Interlocked*，其余用 __sync_*（类型泛化，宽度自适配）。
#ifdef _MSC_VER
#include <intrin.h>
#define INTERLOCKED_FETCH_ADD32(ptr, val)       _InterlockedExchangeAdd((volatile long*)(ptr), (long)(val))
#define INTERLOCKED_FETCH_SUB32(ptr, val)       _InterlockedExchangeAdd((volatile long*)(ptr), -(long)(val))
#define INTERLOCKED_EXCHANGE32(ptr, val)        _InterlockedExchange((volatile long*)(ptr), (long)(val))
#define INTERLOCKED_ADD_AND_FETCH32(ptr, val)   (_InterlockedExchangeAdd((volatile long*)(ptr), (long)(val)) + (long)(val))
#define INTERLOCKED_INCREMENT_AND_FETCH32(ptr)  _InterlockedIncrement((volatile long*)(ptr))
#define INTERLOCKED_DECREMENT_AND_FETCH32(ptr)  _InterlockedDecrement((volatile long*)(ptr))
#define INTERLOCKED_COMPARE_EXCHANGE32(ptr, oldVal, newVal) _InterlockedCompareExchange((volatile long*)(ptr), (long)(newVal), (long)(oldVal))
#define INTERLOCKED_FETCH_ADD64(ptr, val)       _InterlockedExchangeAdd64((volatile __int64*)(ptr), (__int64)(val))
#define INTERLOCKED_FETCH_SUB64(ptr, val)       _InterlockedExchangeAdd64((volatile __int64*)(ptr), -(__int64)(val))
#define INTERLOCKED_EXCHANGE64(ptr, val)        _InterlockedExchange64((volatile __int64*)(ptr), (__int64)(val))
#define INTERLOCKED_ADD_AND_FETCH64(ptr, val)   (_InterlockedExchangeAdd64((volatile __int64*)(ptr), (__int64)(val)) + (__int64)(val))
#define INTERLOCKED_INCREMENT_AND_FETCH64(ptr)  _InterlockedIncrement64((volatile __int64*)(ptr))
#define INTERLOCKED_DECREMENT_AND_FETCH64(ptr)  _InterlockedDecrement64((volatile __int64*)(ptr))
#define INTERLOCKED_COMPARE_EXCHANGE64(ptr, oldVal, newVal) _InterlockedCompareExchange64((volatile __int64*)(ptr), (__int64)(newVal), (__int64)(oldVal))
#else
#define INTERLOCKED_FETCH_ADD32(ptr, val) __sync_fetch_and_add((ptr), (val))
#define INTERLOCKED_FETCH_SUB32(ptr, val) __sync_fetch_and_sub((ptr), (val))
#define INTERLOCKED_EXCHANGE32(ptr, val)   __sync_lock_test_and_set((ptr), (val))
#define INTERLOCKED_ADD_AND_FETCH32(ptr, val)   __sync_add_and_fetch((ptr), (val))
#define INTERLOCKED_INCREMENT_AND_FETCH32(ptr)  __sync_add_and_fetch((ptr), 1)
#define INTERLOCKED_DECREMENT_AND_FETCH32(ptr)  __sync_sub_and_fetch((ptr), 1)
#define INTERLOCKED_COMPARE_EXCHANGE32(ptr, oldVal, newVal) __sync_val_compare_and_swap((ptr), (oldVal), (newVal))
#define INTERLOCKED_FETCH_ADD64(ptr, val) __sync_fetch_and_add((ptr), (val))
#define INTERLOCKED_FETCH_SUB64(ptr, val) __sync_fetch_and_sub((ptr), (val))
#define INTERLOCKED_EXCHANGE64(ptr, val)   __sync_lock_test_and_set((ptr), (val))
#define INTERLOCKED_ADD_AND_FETCH64(ptr, val)   __sync_add_and_fetch((ptr), (val))
#define INTERLOCKED_INCREMENT_AND_FETCH64(ptr)  __sync_add_and_fetch((ptr), 1)
#define INTERLOCKED_DECREMENT_AND_FETCH64(ptr)  __sync_sub_and_fetch((ptr), 1)
#define INTERLOCKED_COMPARE_EXCHANGE64(ptr, oldVal, newVal) __sync_val_compare_and_swap((ptr), (oldVal), (newVal))
#endif

// 批入口的内联约定：dllexport 会阻止内联（clang 实测），这里用强制内联把批函数摊进
// 同 TU 的 adapter。两者都由生成物使用，定义随本文件只出现一次。
#ifndef EJ_BATCH_FORCEINLINE_PRE
#  if defined(_MSC_VER) && !defined(__clang__)
#    define EJ_BATCH_FORCEINLINE_PRE __forceinline
#  else
#    define EJ_BATCH_FORCEINLINE_PRE
#  endif
#endif
#ifndef EJ_BATCH_ALWAYS_INLINE
#  if defined(__clang__)
#    define EJ_BATCH_ALWAYS_INLINE __attribute__((always_inline))
#  else
#    define EJ_BATCH_ALWAYS_INLINE
#  endif
#endif

// Chunk 上下文包头（非托管布局）。与 C# EntJoy.ECS.JobSystem.ChunkContextHeader 逐字段对应：
// 生成代码按此偏移读取 context，字段顺序或宽度改动必须同步两侧。
struct __EntJoyChunkContextHeader
{
    int chunkCount;
    int hasEnabledFilter;
    void* queryAllEnabledTypes;
    int allEnabledCount;
    int gcHandleStartIndex;
    void* chunksPtr;
    int cleanupInProgress;
    int ownsChunkData;
    void* requiredComponentTypeIds;
    int requiredComponentTypeIdCount;
    int jobIsBoxed;
    void* chunkArrayHandle;
    // Event Buffer
    int eventBufferCount;
    void* eventBufferHeaders;
    void* eventWorldHandle;
};

// 事件缓冲区描述（SendEvent 生成的代码按此解引用）。
struct __EntJoyEventBuffer
{
    void* data;
    int* count;
    int capacity;
    int elementSize;
};

#ifdef __cplusplus
// 布局按 64 位指针假设计算：32 位工具链下这两个断言会直接失败（不支持该目标）。
static_assert(sizeof(__EntJoyChunkContextHeader) == 88,
    "ChunkContextHeader must match the managed x64 layout (see NativeChunkJobs.cs).");
static_assert(sizeof(__EntJoyEventBuffer) == 24,
    "EventBufferHeader must match the managed x64 layout.");

#include <cstddef>

// 布局指纹：字段名 + 类型名（都忽略大小写）+ 偏移 + 宽度，FNV-1a 后按加法合成（与字段枚举顺序无关）。
// 托管侧 NativeChunkJobs.ComputeChunkContextHeaderFingerprint 用同值常量算出同一结果；
// 只比总大小抓不到"同宽换序"，所以加载时还要比指纹。
#define __ENTJOY_CHUNK_CONTEXT_FIELDS(X) \
    X(chunkCount, int) X(hasEnabledFilter, int) X(queryAllEnabledTypes, void*) X(allEnabledCount, int) \
    X(gcHandleStartIndex, int) X(chunksPtr, void*) X(cleanupInProgress, int) X(ownsChunkData, int) \
    X(requiredComponentTypeIds, void*) X(requiredComponentTypeIdCount, int) X(jobIsBoxed, int) \
    X(chunkArrayHandle, void*) X(eventBufferCount, int) X(eventBufferHeaders, void*) X(eventWorldHandle, void*)

inline constexpr unsigned long long __EntJoyLayoutFnvBasis = 1469598103934665603ull;
inline constexpr unsigned long long __EntJoyLayoutFnvPrime = 1099511628211ull;
inline constexpr unsigned long long __EntJoyLayoutSalt = 0x9e3779b97f4a7c15ull;

inline constexpr unsigned long long __EntJoyChunkContextFieldHash(
    const char* name, const char* type, unsigned long long offset, unsigned long long size)
{
    unsigned long long hash = __EntJoyLayoutFnvBasis;
    for (const char* c = name; *c != '\0'; ++c)
    {
        const char lower = (*c >= 'A' && *c <= 'Z') ? static_cast<char>(*c + 32) : *c;
        hash ^= static_cast<unsigned char>(lower);
        hash *= __EntJoyLayoutFnvPrime;
    }
    // 名字与类型之间夹一次空推进，避免两者拼接后产生歧义。
    hash *= __EntJoyLayoutFnvPrime;
    for (const char* c = type; *c != '\0'; ++c)
    {
        if (*c == ' ') continue;
        const char lower = (*c >= 'A' && *c <= 'Z') ? static_cast<char>(*c + 32) : *c;
        hash ^= static_cast<unsigned char>(lower);
        hash *= __EntJoyLayoutFnvPrime;
    }
    hash = (hash ^ offset) * __EntJoyLayoutFnvPrime;
    hash = (hash ^ size) * __EntJoyLayoutFnvPrime;
    return hash;
}

inline constexpr unsigned long long __EntJoyChunkContextHeaderFingerprint()
{
    unsigned long long sum = 0;
#define __ENTJOY_CHUNK_FIELD_HASH(name, type) \
    sum += __EntJoyChunkContextFieldHash(#name, #type, \
        offsetof(__EntJoyChunkContextHeader, name), \
        sizeof((static_cast<__EntJoyChunkContextHeader*>(nullptr))->name));
    __ENTJOY_CHUNK_CONTEXT_FIELDS(__ENTJOY_CHUNK_FIELD_HASH)
#undef __ENTJOY_CHUNK_FIELD_HASH
#undef __ENTJOY_CHUNK_CONTEXT_FIELDS
    return sum + (__EntJoyLayoutSalt ^ static_cast<unsigned long long>(sizeof(__EntJoyChunkContextHeader)));
}
#endif
