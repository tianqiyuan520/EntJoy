#pragma once

#include <cstdint>

// 实时 Worker 状态快照（供调试面板读取）。
// 放在全局命名空间，与 Exports.h 的 extern "C" 声明匹配。
struct WorkerSnapshot {
    int32_t  workerIndex;      // worker 编号
    uint64_t currentBatchId;   // 当前执行的 batchId（0=空闲）
    uint32_t currentTile;      // 当前 tile 索引
    uint32_t tileCount;        // 总 tile 数
    bool     isActive;         // 是否正在执行 batch
};