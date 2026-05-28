#pragma once

#include "../../NativeDll/NativeMath.h"
#include "../../NativeDll/NativeContainers.h"


#ifdef __cplusplus
#define EXTERNC extern "C"
#else
#define EXTERNC
#endif

#ifdef _WIN32
#define CALLINGCONVENTION __cdecl
#else
#define CALLINGCONVENTION
#endif

#ifdef DLL_IMPORT
#define HEAD EXTERNC __declspec(dllimport)
#else
#define HEAD EXTERNC __declspec(dllexport)
#endif

// restrict keyword compatibility
#if defined(_MSC_VER) || defined(__clang__)
  #define RESTRICT __restrict
#else
  #define RESTRICT __restrict__
#endif



// Cross-compiler atomic macros (stateless, no std::atomic_ref)
#ifdef _MSC_VER
#include <intrin.h>
#define INTERLOCKED_FETCH_ADD(ptr, val) _InterlockedExchangeAdd((long*)(ptr), (val))
#define INTERLOCKED_FETCH_SUB(ptr, val) _InterlockedExchangeAdd((long*)(ptr), -(val))
#define INTERLOCKED_EXCHANGE(ptr, val)   _InterlockedExchange((long*)(ptr), (val))
#define INTERLOCKED_ADD_AND_FETCH(ptr, val)   (_InterlockedExchangeAdd((long*)(ptr), (val)) + (val))
#define INTERLOCKED_INCREMENT_AND_FETCH(ptr)  _InterlockedIncrement((long*)(ptr))
#define INTERLOCKED_DECREMENT_AND_FETCH(ptr)  _InterlockedDecrement((long*)(ptr))
#define INTERLOCKED_SUB_AND_FETCH(ptr, val)   (_InterlockedExchangeAdd((long*)(ptr), -(val)) - (val))
#define INTERLOCKED_COMPARE_EXCHANGE(ptr, oldVal, newVal) _InterlockedCompareExchange((long*)(ptr), (newVal), (oldVal))
#else
#define INTERLOCKED_FETCH_ADD(ptr, val) __sync_fetch_and_add((ptr), (val))
#define INTERLOCKED_FETCH_SUB(ptr, val) __sync_fetch_and_sub((ptr), (val))
#define INTERLOCKED_EXCHANGE(ptr, val)   __sync_lock_test_and_set((ptr), (val))
#define INTERLOCKED_ADD_AND_FETCH(ptr, val)   __sync_add_and_fetch((ptr), (val))
#define INTERLOCKED_INCREMENT_AND_FETCH(ptr)  __sync_add_and_fetch((ptr), 1)
#define INTERLOCKED_DECREMENT_AND_FETCH(ptr)  __sync_sub_and_fetch((ptr), 1)
#define INTERLOCKED_SUB_AND_FETCH(ptr, val)   __sync_sub_and_fetch((ptr), (val))
#define INTERLOCKED_COMPARE_EXCHANGE(ptr, oldVal, newVal) __sync_val_compare_and_swap((ptr), (oldVal), (newVal))
#endif


HEAD void CALLINGCONVENTION SharpNative_Job__global_namespace__GridSearch2D_PrefixSumJobPointer_Execute(int* RESTRICT Counts_ptr, int Counts_length, int* RESTRICT Length_ptr);
