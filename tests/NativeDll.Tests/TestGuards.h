#pragma once

// 测试共享守卫：把「正确性判据」与「覆盖/形状判据」分开，并把 sanitizer 策略**集中一处**。
//
// 为什么需要它（CI 实测，不是推测）：
//   `-fsanitize=thread` / `-fsanitize=address` 会显著改变调度与可见资源 ——
//   容器里 `[JOBPHYS] physicalCores` 会报 0、`[JOBF6] nokey` 可达 5000
//   （大量批走表命中/旁路、根本没进学习路径）。于是「某条新代码路径在采样瞬间被走到」
//   这类**形状/覆盖**前提在 sanitizer 腿不保证成立；若按硬判据处理，红的是**测试环境**
//   而不是产品行为。历史两次假红都属此类：
//     · `WakeLivenessTests`  phase A `wakes>0`（rc=3）
//     · `JobSystemTests`   `concurrent job must learn its per-element cost`（rc=1）
//
// 约定（所有原生测试统一遵守，**不要再在各自文件里写 `#if defined(__SANITIZE_*)`**）：
//   · **正确性判据** —— 例：「每个 index/job 恰好执行一次」「丢唤醒表现为挂住，
//     由 `RunWithTimeout` 的 60 s 超时兜底 → `[DEADLOCK]` + abort」。**所有构建一律硬**。
//   · **覆盖判据** —— 例：`skips>0` / `wakes>0` / 「学到了成本」（形状自证）。
//     非 sanitizer 构建**硬**；sanitizer 构建降级为 `[COVERAGE-SKIP] <原因>` 诊断（计数照打）。
//
// 用法（各测试文件只需这一行适配，异常类型由本文件自己的 `Require` 决定）：
//     #include "TestGuards.h"
//     void RequireCoverage(bool ok, const char* msg)
//     { if (!ok && TestGuards::CoverageMiss(msg)) Require(false, msg); }
//   —— `CoverageMiss` 返回 true 表示「应当按硬失败处理」，false 表示「sanitizer 腿已降级」。

#include <iostream>

// ⚠ sanitizer 的检测必须**同时覆盖 GCC 与 Clang**，否则整个降级机制静默失效：
//   · GCC   ：`-fsanitize=address|thread` ⇒ 定义 `__SANITIZE_ADDRESS__` / `__SANITIZE_THREAD__`
//   · Clang ：**只**提供特性测试宏 `__has_feature(address_sanitizer|thread_sanitizer)`，
//             且 **clang 不定义 `__SANITIZE_THREAD__`**（历史事故：CI 的 linux-sanitizers 用 clang++，
//             早期版本只判断那两个字面宏 ⇒ `kSanitizerBuild` 恒为 false ⇒ 降级从未生效、
//             CI 照样红在 `FAIL concurrent job must learn its per-element cost`）。
#ifndef __has_feature
#define __has_feature(x) 0
#endif

namespace TestGuards
{
#if defined(__SANITIZE_THREAD__) || defined(__SANITIZE_ADDRESS__) || \
    __has_feature(thread_sanitizer) || __has_feature(address_sanitizer)
    inline constexpr bool kSanitizerBuild = true;
#else
    inline constexpr bool kSanitizerBuild = false;
#endif

    // 慢速构建下把「等前置状态」的窗口放大：要测的形状没变，只是要等更久才可能出现。
    inline constexpr int WaitMs(int normalMs, int sanitizerMs)
    {
        return kSanitizerBuild ? sanitizerMs : normalMs;
    }

    // 覆盖判据未满足时的处置。返回 true = 调用方应按硬失败处理；
    // 返回 false = sanitizer 腿，已打印诊断，调用方继续。
    inline bool CoverageMiss(const char* message)
    {
        if (!kSanitizerBuild) return true;
        std::cerr << "[COVERAGE-SKIP] " << message
                  << " -- sanitizer build: this shape did not reproduce "
                     "(coverage criterion, not a correctness failure)" << std::endl;
        return false;
    }
}
