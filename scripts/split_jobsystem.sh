#!/usr/bin/env bash
# JobSystem.cpp 模块化拆分脚本：base 留在 JobSystem.cpp，State/Tiles/Scheduler 拆出。
set -euo pipefail

SRC="e:/GODOT/Project/EntJoy/src/NativeDll/JobSystem.cpp"
DST="e:/GODOT/Project/EntJoy/src/NativeDll"
ORIG="/tmp/jobsys_orig.cpp"

# 基于 git HEAD 的原文件做提取（工作树中的 JobSystem.cpp 已被本脚本改写，
# 若需基于未提交改动请先 git add，再改用 `git show :0:path`）。
git show HEAD:src/NativeDll/JobSystem.cpp > "$ORIG"

# 折叠连续空行为单行（提取后各段交界处的空白处理）。
collapse() { awk 'BEGIN{b=0} /^[[:space:]]*$/{if(!b) print ""; b=1; next} {b=0; print}'; }

echo "== base: JobSystem.cpp =="
sed -e '1c\#include "JobSystemInternal.h"' \
    -e '2,6d' \
    -e '41,45d' -e '54d' -e '64,72d' -e '77,99d' \
    -e '165,169d' -e '179,189d' -e '192,212d' \
    -e '524d' \
    -e '600,886d' -e '887,1665d' -e '1667,1864d' -e '1866,2320d' \
    "$ORIG" \
  | sed \
      -e 's/^    static uint64_t AssignStateDiagnosticId(/    uint64_t AssignStateDiagnosticId(/' \
      -e 's/^    static void RecordBatchTiming(/    void RecordBatchTiming(/' \
      -e 's/^    static uint64_t MonotonicNowNs(/    uint64_t MonotonicNowNs(/' \
      -e 's/^    static int CurrentProcessorIndexForDiagnostics(/    int CurrentProcessorIndexForDiagnostics(/' \
      -e 's/^    static uint64_t CurrentThreadCpuTimeNsForDiagnostics(/    uint64_t CurrentThreadCpuTimeNsForDiagnostics(/' \
      -e 's/^    static uint64_t CurrentThreadCyclesForDiagnostics(/    uint64_t CurrentThreadCyclesForDiagnostics(/' \
      -e 's/^    static int PhysicalCoreIndexForDiagnostics(/    int PhysicalCoreIndexForDiagnostics(/' \
      -e 's/^    static void FlushStateCacheToSharedPool()/    void FlushStateCacheToSharedPool()/' \
  | collapse > "$DST/JobSystem.cpp.new"
mv "$DST/JobSystem.cpp.new" "$DST/JobSystem.cpp"

echo "== JobSystem_State.cpp =="
cat > "$DST/JobSystem_State.cpp" <<'EOF'
#include "JobSystemInternal.h"

#include <algorithm>
#include <thread>
#include <utility>

#if defined(_MSC_VER) && (defined(_M_IX86) || defined(_M_X64))
#include <immintrin.h>
#endif

namespace JobSystem
{
EOF
sed -n '600,885p' "$ORIG" \
  | sed -e '/^    constexpr uint64_t kLongBatchBarrierNs/d' \
        -e 's/^    static void RetainDependency(/    void RetainDependency(/' \
        -e 's/^    static void RegisterLongBatchBarrier(/    void RegisterLongBatchBarrier(/' \
        -e 's/^    static void ConsumeLongBatchBarriers() noexcept/    void ConsumeLongBatchBarriers() noexcept/' \
        -e 's/^    static void SubmitBackendAsync(/    void SubmitBackendAsync(/' \
  >> "$DST/JobSystem_State.cpp"
printf '\n' >> "$DST/JobSystem_State.cpp"
sed -n '1667,1864p' "$ORIG" >> "$DST/JobSystem_State.cpp"
cat >> "$DST/JobSystem_State.cpp" <<'EOF'

} // namespace JobSystem
EOF

echo "== JobSystem_Tiles.cpp =="
cat > "$DST/JobSystem_Tiles.cpp" <<'EOF'
#include "JobSystemInternal.h"

#include <algorithm>
#include <thread>

#if defined(_MSC_VER) && (defined(_M_IX86) || defined(_M_X64))
#include <immintrin.h>
#endif

namespace JobSystem
{
EOF
sed -n '887,1665p' "$ORIG" \
  | sed -e 's/^    static int ResolveWorkerTarget(/    int ResolveWorkerTarget(/' \
        -e 's/^    static int ResolveEcsBatchRangeSize(/    int ResolveEcsBatchRangeSize(/' \
        -e 's/^    static int GuidedTileCount(/    int GuidedTileCount(/' \
        -e 's/^    static int BuildGuidedTiles(/    int BuildGuidedTiles(/' \
        -e 's/^    static void FlushBatchStorageCacheToSharedPool()/    void FlushBatchStorageCacheToSharedPool()/' \
        -e 's/^    static BatchStorage\* AcquireBatchStorage(/    BatchStorage* AcquireBatchStorage(/' \
        -e 's/^    static void ClearBatchStoragePool() noexcept/    void ClearBatchStoragePool() noexcept/' \
        -e 's/^    static void SubmitBatch(/    void SubmitBatch(/' \
        -e 's/^    static bool ChunkExecuteTile(/    bool ChunkExecuteTile(/' \
        -e 's/^    static void CleanupChunkContext(/    void CleanupChunkContext(/' \
        -e 's/^    static bool GeneralExecuteTile(/    bool GeneralExecuteTile(/' \
        -e 's/^    static void CleanupGeneralContext(/    void CleanupGeneralContext(/' \
  >> "$DST/JobSystem_Tiles.cpp"
cat >> "$DST/JobSystem_Tiles.cpp" <<'EOF'

} // namespace JobSystem
EOF

echo "== JobSystem_Scheduler.cpp =="
cat > "$DST/JobSystem_Scheduler.cpp" <<'EOF'
#include "JobSystemInternal.h"
#include "ThreadAffinity.h"

#include <algorithm>
#include <cctype>
#include <cstdlib>
#include <string>
#include <thread>
#include <utility>

#if defined(_MSC_VER) && (defined(_M_IX86) || defined(_M_X64))
#include <windows.h>
#include <timeapi.h>
#pragma comment(lib, "winmm.lib")
#endif

namespace JobSystem
{
EOF
sed -n '1866,2320p' "$ORIG" >> "$DST/JobSystem_Scheduler.cpp"
cat >> "$DST/JobSystem_Scheduler.cpp" <<'EOF'

} // namespace JobSystem
EOF

echo "done. line counts:"
wc -l "$DST/JobSystem.cpp" "$DST/JobSystem_State.cpp" "$DST/JobSystem_Tiles.cpp" "$DST/JobSystem_Scheduler.cpp" "$DST/JobSystemInternal.h"
