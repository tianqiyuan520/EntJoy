#include "SharpNative_Job__global_namespace__GridSearch2D_ClosestPointJobPointer_Execute.h"
#include <algorithm>
#include <cmath>
#include <cstdio>
#include "../../NativeDll/NativeSIMD.h"
#include "../../NativeDll/SimdValue.h"

HEAD void CALLINGCONVENTION SharpNative_Job__global_namespace__GridSearch2D_ClosestPointJobPointer_Execute_Batch_false(int __startIndex, int __count, EntJoy::Mathematics::float2* RESTRICT GridOrigin_ptr, float* RESTRICT GridResolutionInv_ptr, EntJoy::Mathematics::int2* RESTRICT GridDimensions_ptr, EntJoy::Mathematics::float2* RESTRICT QueryPositions_ptr, int QueryPositions_length, EntJoy::Mathematics::float2* RESTRICT SortedPositions_ptr, int SortedPositions_length, EntJoy::Mathematics::int2* RESTRICT HashIndex_ptr, int HashIndex_length, EntJoy::Collections::UnsafeList<EntJoy::Mathematics::int2>* RESTRICT CellStartEnd_listData, int* RESTRICT SortedLength_ptr, bool* RESTRICT IgnoreSelf_ptr, float* RESTRICT SquaredEpsilonSelf_ptr, int* RESTRICT Results_ptr, int Results_length)
{
    EntJoy::Collections::UnsafeList<EntJoy::Mathematics::int2>& CellStartEnd = *CellStartEnd_listData;
    const EntJoy::Mathematics::float2& GridOrigin = *GridOrigin_ptr;
    const float& GridResolutionInv = *GridResolutionInv_ptr;
    const EntJoy::Mathematics::int2& GridDimensions = *GridDimensions_ptr;
    const int& SortedLength = *SortedLength_ptr;
    const bool& IgnoreSelf = *IgnoreSelf_ptr;
    const float& SquaredEpsilonSelf = *SquaredEpsilonSelf_ptr;
    // --- ISPC-style SIMD: 8-wide mask-managed (no per-lane extraction) ---
    int simd_end_ = __startIndex + ((__count) / NSIMD_WIDTH) * NSIMD_WIDTH;
    if (simd_end_ > __startIndex)
    {
        // Hoisted loop-invariant broadcasts
        simd_value<float> v_GridOrigin_x = simd_value<float>::broadcast(GridOrigin.x());
        simd_value<float> v_GridOrigin_y = simd_value<float>::broadcast(GridOrigin.y());

        simd_value<int> v_base = simd_value<int>::sequence(0);
        for (int si = __startIndex; si < simd_end_; si += NSIMD_WIDTH)
        {
            // Gather 8 query positions (SIMD)
            simd_value<int> v_i = v_base + si;
            simd_value<EntJoy::Mathematics::float2> v_q =
                simd_value<EntJoy::Mathematics::float2>::gather(QueryPositions_ptr, v_i);

            // ===== ISPC-style body (all SIMD, no per-lane extraction) =====
            {
                // Broadcast grid constants
                simd_value<int> v_grid_dims_x = simd_value<int>::broadcast(GridDimensions.x());
                simd_value<int> v_grid_dims_y = simd_value<int>::broadcast(GridDimensions.y());
                simd_value<int> v_zero = simd_value<int>::broadcast(0);
                simd_value<int> v_maxCellHash = v_grid_dims_x * v_grid_dims_y - 1;

                // Compute cell positions (SIMD: floor, convert, clamp)
                simd_value<float> v_cell_fx = (v_q.x - v_GridOrigin_x) * GridResolutionInv;
                v_cell_fx = v_cell_fx.floor();
                simd_value<float> v_cell_fy = (v_q.y - v_GridOrigin_y) * GridResolutionInv;
                v_cell_fy = v_cell_fy.floor();
                simd_value<int> v_cell_x = simd_value<int>::convert(v_cell_fx);
                simd_value<int> v_cell_y = simd_value<int>::convert(v_cell_fy);
                v_cell_x = simd_max(v_cell_x, v_zero);
                v_cell_y = simd_max(v_cell_y, v_zero);
                v_cell_x = simd_min(v_cell_x, v_grid_dims_x - 1);
                v_cell_y = simd_min(v_cell_y, v_grid_dims_y - 1);

                // Initialize best values (per-lane in SIMD regs)
                simd_value<float> v_bestDistSq = simd_value<float>::broadcast(std::numeric_limits<float>::max());
                simd_value<int> v_bestIdx = simd_value<int>::broadcast(-1);

                // Results initialization: all -1
                n_store_epi32(&Results_ptr[si], n_set1_epi32(-1));

                // ---- dx loop (SIMD mask-managed) ----
                for (int dx = -1; dx <= 1; dx++)
                {
                    simd_value<int> v_nx = v_cell_x + dx;
                    simd_mask v_nx_active{ n_cmp_ult_epi32(v_nx.v, v_grid_dims_x.v) };
                    if (!v_nx_active.any_true()) continue;
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        simd_value<int> v_ny = v_cell_y + dy;
                        simd_mask v_cell_active = v_nx_active & simd_mask{ n_cmp_ult_epi32(v_ny.v, v_grid_dims_y.v) };
                        if (!v_cell_active.any_true()) continue;

                        // Cell hash (clamped for gather safety)
                        simd_value<int> v_cellHash = v_ny * v_grid_dims_x + v_nx;
                        v_cellHash = simd_max(v_cellHash, v_zero);
                        v_cellHash = simd_min(v_cellHash, v_maxCellHash);

                        // Gather CellStartEnd range (int2 per cell)
                        simd_value<EntJoy::Mathematics::int2> v_range =
                            simd_value<EntJoy::Mathematics::int2>::gather(CellStartEnd.Ptr, v_cellHash);
                        simd_mask v_start_valid{ n_cmp_ge_epi32(v_range.x.v, v_zero.v) };
                        simd_mask v_active = v_cell_active & v_start_valid;
                        if (!v_active.any_true()) continue;

                        // ===== Inner reduction loop (ISPC-style count for) =====
                        // Each lane has its own i (start..end), advances independently
                        // Pre-compute max iterations = hmax(end-start) so all lanes march together
                        // Finished lanes do wasted work, result masked out by blend (ISPC semantics)
                        simd_value<int> v_i_red = v_range.x;
                        v_i_red = simd_max(v_i_red, v_zero);
                        simd_value<int> v_end = v_range.y;
                        simd_value<int> v_maxIter = v_end - v_i_red;
                        int maxIter = hmax(v_maxIter);
                        simd_value<int> v_sortedLast = simd_value<int>::broadcast(SortedLength - 1);
                        #pragma loop(ivdep)
                        for (int iter = 0; iter < maxIter; iter++)
                        {
                            // Per-iteration mask: lanes with i < end stay active
                            simd_mask v_mask{ n_cmp_lt_epi32(v_i_red.v, v_end.v) };
                            // Clamp i to safe bounds for gather (finished lanes read 0, blend discards)
                            simd_value<int> v_safe_i = simd_min(v_i_red, v_sortedLast);
                            simd_value<float> v_px = simd_value<float>::gathf(SortedPositions_ptr, v_safe_i.v);
                            simd_value<float> v_py = simd_value<float>::gathfy(SortedPositions_ptr, v_safe_i.v);

                            // 8-wide distance squared: (qx-px)^2 + (qy-py)^2
                            simd_value<float> v_dx = v_q.x - v_px;
                            simd_value<float> v_dy = v_q.y - v_py;
                            simd_value<float> v_distSq = v_dx * v_dx + v_dy * v_dy;

                            // Masked blend: update if distSq < bestDistSq AND lane active
                            simd_mask v_improve{ n_cmp_lt_ps(v_distSq.v, v_bestDistSq.v) };
                            v_improve = v_improve & v_mask;
                            v_bestDistSq = blend(v_bestDistSq, v_distSq, v_improve);
                            v_bestIdx = blend(v_bestIdx, v_i_red, v_improve);

                            // Advance i (finished lanes keep going, blend discards)
                            v_i_red = v_i_red + 1;
                        }
                    }
                }

                // ---- Global fallback (lanes where bestIdx is still -1) ----
                simd_mask v_need_fallback{ n_cmp_eq_epi32(v_bestIdx.v, n_set1_epi32(-1)) };
                if (v_need_fallback.any_true())
                {
                    // Scalar load + broadcast (all active lanes share same index i_fb)
                    simd_value<float> v_fb_bestDistSq = v_bestDistSq;
                    simd_value<int> v_fb_bestIdx = v_bestIdx;
                    simd_mask v_fb_active = v_need_fallback;
                    int sortedLen = SortedLength;
                    for (int i_fb = 0; i_fb < sortedLen; i_fb++)
                    {
                        if (!v_fb_active.any_true()) break;
                        // Scalar load once, broadcast to all lanes
                        EntJoy::Mathematics::float2 fb_pos = SortedPositions_ptr[i_fb];
                        simd_value<float> v_fb_px = simd_value<float>::broadcast(fb_pos.x());
                        simd_value<float> v_fb_py = simd_value<float>::broadcast(fb_pos.y());
                        simd_value<float> v_fb_dx = v_q.x - v_fb_px;
                        simd_value<float> v_fb_dy = v_q.y - v_fb_py;
                        simd_value<float> v_fb_distSq = v_fb_dx * v_fb_dx + v_fb_dy * v_fb_dy;
                        simd_mask v_fb_improve{ n_cmp_lt_ps(v_fb_distSq.v, v_fb_bestDistSq.v) };
                        v_fb_improve = v_fb_improve & v_fb_active;
                        v_fb_bestDistSq = blend(v_fb_bestDistSq, v_fb_distSq, v_fb_improve);
                        v_fb_bestIdx = blend(v_fb_bestIdx, simd_value<int>::broadcast(i_fb), v_fb_improve);
                    }
                    // Merge fallback results into main SIMD registers
                    v_bestDistSq = blend(v_bestDistSq, v_fb_bestDistSq, v_need_fallback);
                    v_bestIdx = blend(v_bestIdx, v_fb_bestIdx, v_need_fallback);
                }

                // ---- Write results: HashIndex_ptr[bestIdx].y for found lanes ----
                // Per-lane scalar write (safe: unmasked gather with -1 indices would AV)
                for (int lane = 0; lane < NSIMD_WIDTH; lane++)
                {
                    int bestIdx_lane = n_extract_lane_epi32(v_bestIdx.v, lane);
                    if (bestIdx_lane != -1)
                        Results_ptr[si + lane] = HashIndex_ptr[bestIdx_lane].y();
                }
            }
        }
    }
    for (int index = simd_end_; index < __startIndex + __count; ++index)
    {
    Results_ptr[index] = -1;
    EntJoy::Mathematics::float2 q = QueryPositions_ptr[index];
    EntJoy::Mathematics::int2 cell = ((EntJoy::Mathematics::int2)EntJoy::Mathematics::floor((q - GridOrigin) * GridResolutionInv));
    cell = EntJoy::Mathematics::clamp(cell, EntJoy::Mathematics::int2(0), GridDimensions - 1);
    float bestDistSq = std::numeric_limits<float>::max();
    int bestIdx = -1;
    for (int dx = -1; dx <= 1; dx++)
    {
        int nx = cell.x() + dx;
        if (((unsigned int)nx) >= ((unsigned int)GridDimensions.x()))
                    continue;
        for (int dy = -1; dy <= 1; dy++)
        {
            int ny = cell.y() + dy;
            if (((unsigned int)ny) >= ((unsigned int)GridDimensions.y()))
                            continue;
            int cellHash = ny * GridDimensions.x() + nx;
            EntJoy::Mathematics::int2 range = CellStartEnd[cellHash];
            int start = range.x();
            int end = range.y();
            if (start < 0)
                            continue;
            for (int i = start; i < end; i++)
            {
                EntJoy::Mathematics::float2 pos = SortedPositions_ptr[i];
                float distSq = (q.x()-pos.x())*(q.x()-pos.x()) + (q.y()-pos.y())*(q.y()-pos.y());
                if (false && distSq < SquaredEpsilonSelf)
                                    continue;
                if (distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    bestIdx = i;
                }
            }
        }
    }
    if (bestIdx != -1)
    {
        Results_ptr[index] = HashIndex_ptr[bestIdx].y();
    }
    else
    {
        for (int i = 0; i < SortedLength; i++)
        {
            EntJoy::Mathematics::float2 pos = SortedPositions_ptr[i];
            float distSq = (q.x()-pos.x())*(q.x()-pos.x()) + (q.y()-pos.y())*(q.y()-pos.y());
            if (false && distSq < SquaredEpsilonSelf)
                            continue;
            if (distSq < bestDistSq)
            {
                bestDistSq = distSq;
                bestIdx = i;
            }
        }
        if (bestIdx != -1)
                    Results_ptr[index] = HashIndex_ptr[bestIdx].y();
    }
    }
}

HEAD void CALLINGCONVENTION SharpNative_Job__global_namespace__GridSearch2D_ClosestPointJobPointer_Execute_Batch_true(int __startIndex, int __count, EntJoy::Mathematics::float2* RESTRICT GridOrigin_ptr, float* RESTRICT GridResolutionInv_ptr, EntJoy::Mathematics::int2* RESTRICT GridDimensions_ptr, EntJoy::Mathematics::float2* RESTRICT QueryPositions_ptr, int QueryPositions_length, EntJoy::Mathematics::float2* RESTRICT SortedPositions_ptr, int SortedPositions_length, EntJoy::Mathematics::int2* RESTRICT HashIndex_ptr, int HashIndex_length, EntJoy::Collections::UnsafeList<EntJoy::Mathematics::int2>* RESTRICT CellStartEnd_listData, int* RESTRICT SortedLength_ptr, bool* RESTRICT IgnoreSelf_ptr, float* RESTRICT SquaredEpsilonSelf_ptr, int* RESTRICT Results_ptr, int Results_length)
{
    EntJoy::Collections::UnsafeList<EntJoy::Mathematics::int2>& CellStartEnd = *CellStartEnd_listData;
    const EntJoy::Mathematics::float2& GridOrigin = *GridOrigin_ptr;
    const float& GridResolutionInv = *GridResolutionInv_ptr;
    const EntJoy::Mathematics::int2& GridDimensions = *GridDimensions_ptr;
    const int& SortedLength = *SortedLength_ptr;
    const bool& IgnoreSelf = *IgnoreSelf_ptr;
    const float& SquaredEpsilonSelf = *SquaredEpsilonSelf_ptr;
    // --- ISPC-style SIMD: 8-wide mask-managed (no per-lane extraction) ---
    int simd_end_ = __startIndex + ((__count) / NSIMD_WIDTH) * NSIMD_WIDTH;
    if (simd_end_ > __startIndex)
    {
        // Hoisted loop-invariant broadcasts
        simd_value<float> v_GridOrigin_x = simd_value<float>::broadcast(GridOrigin.x());
        simd_value<float> v_GridOrigin_y = simd_value<float>::broadcast(GridOrigin.y());
        simd_value<float> v_sqEpsilon = simd_value<float>::broadcast(SquaredEpsilonSelf);

        simd_value<int> v_base = simd_value<int>::sequence(0);
        for (int si = __startIndex; si < simd_end_; si += NSIMD_WIDTH)
        {
            // Gather 8 query positions (SIMD)
            simd_value<int> v_i = v_base + si;
            simd_value<EntJoy::Mathematics::float2> v_q =
                simd_value<EntJoy::Mathematics::float2>::gather(QueryPositions_ptr, v_i);

            // ===== ISPC-style body (all SIMD, no per-lane extraction) =====
            {
                // Broadcast grid constants
                simd_value<int> v_grid_dims_x = simd_value<int>::broadcast(GridDimensions.x());
                simd_value<int> v_grid_dims_y = simd_value<int>::broadcast(GridDimensions.y());
                simd_value<int> v_zero = simd_value<int>::broadcast(0);
                simd_value<int> v_maxCellHash = v_grid_dims_x * v_grid_dims_y - 1;

                // Compute cell positions (SIMD: floor, convert, clamp)
                simd_value<float> v_cell_fx = (v_q.x - v_GridOrigin_x) * GridResolutionInv;
                v_cell_fx = v_cell_fx.floor();
                simd_value<float> v_cell_fy = (v_q.y - v_GridOrigin_y) * GridResolutionInv;
                v_cell_fy = v_cell_fy.floor();
                simd_value<int> v_cell_x = simd_value<int>::convert(v_cell_fx);
                simd_value<int> v_cell_y = simd_value<int>::convert(v_cell_fy);
                v_cell_x = simd_max(v_cell_x, v_zero);
                v_cell_y = simd_max(v_cell_y, v_zero);
                v_cell_x = simd_min(v_cell_x, v_grid_dims_x - 1);
                v_cell_y = simd_min(v_cell_y, v_grid_dims_y - 1);

                // Initialize best values (per-lane in SIMD regs)
                simd_value<float> v_bestDistSq = simd_value<float>::broadcast(std::numeric_limits<float>::max());
                simd_value<int> v_bestIdx = simd_value<int>::broadcast(-1);

                // Results initialization: all -1
                n_store_epi32(&Results_ptr[si], n_set1_epi32(-1));

                // ---- dx loop (SIMD mask-managed) ----
                for (int dx = -1; dx <= 1; dx++)
                {
                    simd_value<int> v_nx = v_cell_x + dx;
                    simd_mask v_nx_active{ n_cmp_ult_epi32(v_nx.v, v_grid_dims_x.v) };
                    if (!v_nx_active.any_true()) continue;
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        simd_value<int> v_ny = v_cell_y + dy;
                        simd_mask v_cell_active = v_nx_active & simd_mask{ n_cmp_ult_epi32(v_ny.v, v_grid_dims_y.v) };
                        if (!v_cell_active.any_true()) continue;

                        // Cell hash (clamped for gather safety)
                        simd_value<int> v_cellHash = v_ny * v_grid_dims_x + v_nx;
                        v_cellHash = simd_max(v_cellHash, v_zero);
                        v_cellHash = simd_min(v_cellHash, v_maxCellHash);

                        // Gather CellStartEnd range (int2 per cell)
                        simd_value<EntJoy::Mathematics::int2> v_range =
                            simd_value<EntJoy::Mathematics::int2>::gather(CellStartEnd.Ptr, v_cellHash);
                        simd_mask v_start_valid{ n_cmp_ge_epi32(v_range.x.v, v_zero.v) };
                        simd_mask v_active = v_cell_active & v_start_valid;
                        if (!v_active.any_true()) continue;

                        // ===== Inner reduction loop (ISPC-style count for) =====
                        // Each lane has its own i (start..end), advances independently
                        // Pre-compute max iterations = hmax(end-start) so all lanes march together
                        // Finished lanes do wasted work, result masked out by blend (ISPC semantics)
                        simd_value<int> v_i_red = v_range.x;
                        v_i_red = simd_max(v_i_red, v_zero);
                        simd_value<int> v_end = v_range.y;
                        simd_value<int> v_maxIter = v_end - v_i_red;
                        int maxIter = hmax(v_maxIter);
                        simd_value<int> v_sortedLast = simd_value<int>::broadcast(SortedLength - 1);
                        #pragma loop(ivdep)
                        for (int iter = 0; iter < maxIter; iter++)
                        {
                            // Per-iteration mask: lanes with i < end stay active
                            simd_mask v_mask{ n_cmp_lt_epi32(v_i_red.v, v_end.v) };
                            // Clamp i to safe bounds for gather (finished lanes read 0, blend discards)
                            simd_value<int> v_safe_i = simd_min(v_i_red, v_sortedLast);
                            simd_value<float> v_px = simd_value<float>::gathf(SortedPositions_ptr, v_safe_i.v);
                            simd_value<float> v_py = simd_value<float>::gathfy(SortedPositions_ptr, v_safe_i.v);

                            // 8-wide distance squared: (qx-px)^2 + (qy-py)^2
                            simd_value<float> v_dx = v_q.x - v_px;
                            simd_value<float> v_dy = v_q.y - v_py;
                            simd_value<float> v_distSq = v_dx * v_dx + v_dy * v_dy;

                            // Masked blend: update if distSq < bestDistSq AND lane active
                            simd_mask v_improve{ n_cmp_lt_ps(v_distSq.v, v_bestDistSq.v) };
                            v_improve = v_improve & v_mask;
                            // Self-point exclusion (IgnoreSelf=true)
                            simd_mask v_not_self{ n_not_mask(n_cmp_lt_ps(v_distSq.v, v_sqEpsilon.v)) };
                            v_improve = v_improve & v_not_self;
                            v_bestDistSq = blend(v_bestDistSq, v_distSq, v_improve);
                            v_bestIdx = blend(v_bestIdx, v_i_red, v_improve);

                            // Advance i (finished lanes keep going, blend discards)
                            v_i_red = v_i_red + 1;
                        }
                    }
                }

                // ---- Global fallback (lanes where bestIdx is still -1) ----
                simd_mask v_need_fallback{ n_cmp_eq_epi32(v_bestIdx.v, n_set1_epi32(-1)) };
                if (v_need_fallback.any_true())
                {
                    // Scalar load + broadcast (all active lanes share same index i_fb)
                    simd_value<float> v_fb_bestDistSq = v_bestDistSq;
                    simd_value<int> v_fb_bestIdx = v_bestIdx;
                    simd_mask v_fb_active = v_need_fallback;
                    int sortedLen = SortedLength;
                    for (int i_fb = 0; i_fb < sortedLen; i_fb++)
                    {
                        if (!v_fb_active.any_true()) break;
                        // Scalar load once, broadcast to all lanes
                        EntJoy::Mathematics::float2 fb_pos = SortedPositions_ptr[i_fb];
                        simd_value<float> v_fb_px = simd_value<float>::broadcast(fb_pos.x());
                        simd_value<float> v_fb_py = simd_value<float>::broadcast(fb_pos.y());
                        simd_value<float> v_fb_dx = v_q.x - v_fb_px;
                        simd_value<float> v_fb_dy = v_q.y - v_fb_py;
                        simd_value<float> v_fb_distSq = v_fb_dx * v_fb_dx + v_fb_dy * v_fb_dy;
                        simd_mask v_fb_improve{ n_cmp_lt_ps(v_fb_distSq.v, v_fb_bestDistSq.v) };
                        v_fb_improve = v_fb_improve & v_fb_active;
                        simd_mask v_fb_not_self{ n_not_mask(n_cmp_lt_ps(v_fb_distSq.v, v_sqEpsilon.v)) };
                        v_fb_improve = v_fb_improve & v_fb_not_self;
                        v_fb_bestDistSq = blend(v_fb_bestDistSq, v_fb_distSq, v_fb_improve);
                        v_fb_bestIdx = blend(v_fb_bestIdx, simd_value<int>::broadcast(i_fb), v_fb_improve);
                    }
                    // Merge fallback results into main SIMD registers
                    v_bestDistSq = blend(v_bestDistSq, v_fb_bestDistSq, v_need_fallback);
                    v_bestIdx = blend(v_bestIdx, v_fb_bestIdx, v_need_fallback);
                }

                // ---- Write results: HashIndex_ptr[bestIdx].y for found lanes ----
                // Per-lane scalar write (safe: unmasked gather with -1 indices would AV)
                for (int lane = 0; lane < NSIMD_WIDTH; lane++)
                {
                    int bestIdx_lane = n_extract_lane_epi32(v_bestIdx.v, lane);
                    if (bestIdx_lane != -1)
                        Results_ptr[si + lane] = HashIndex_ptr[bestIdx_lane].y();
                }
            }
        }
    }
    for (int index = simd_end_; index < __startIndex + __count; ++index)
    {
    Results_ptr[index] = -1;
    EntJoy::Mathematics::float2 q = QueryPositions_ptr[index];
    EntJoy::Mathematics::int2 cell = ((EntJoy::Mathematics::int2)EntJoy::Mathematics::floor((q - GridOrigin) * GridResolutionInv));
    cell = EntJoy::Mathematics::clamp(cell, EntJoy::Mathematics::int2(0), GridDimensions - 1);
    float bestDistSq = std::numeric_limits<float>::max();
    int bestIdx = -1;
    for (int dx = -1; dx <= 1; dx++)
    {
        int nx = cell.x() + dx;
        if (((unsigned int)nx) >= ((unsigned int)GridDimensions.x()))
                    continue;
        for (int dy = -1; dy <= 1; dy++)
        {
            int ny = cell.y() + dy;
            if (((unsigned int)ny) >= ((unsigned int)GridDimensions.y()))
                            continue;
            int cellHash = ny * GridDimensions.x() + nx;
            EntJoy::Mathematics::int2 range = CellStartEnd[cellHash];
            int start = range.x();
            int end = range.y();
            if (start < 0)
                            continue;
            for (int i = start; i < end; i++)
            {
                EntJoy::Mathematics::float2 pos = SortedPositions_ptr[i];
                float distSq = (q.x()-pos.x())*(q.x()-pos.x()) + (q.y()-pos.y())*(q.y()-pos.y());
                if (true && distSq < SquaredEpsilonSelf)
                                    continue;
                if (distSq < bestDistSq)
                {
                    bestDistSq = distSq;
                    bestIdx = i;
                }
            }
        }
    }
    if (bestIdx != -1)
    {
        Results_ptr[index] = HashIndex_ptr[bestIdx].y();
    }
    else
    {
        for (int i = 0; i < SortedLength; i++)
        {
            EntJoy::Mathematics::float2 pos = SortedPositions_ptr[i];
            float distSq = (q.x()-pos.x())*(q.x()-pos.x()) + (q.y()-pos.y())*(q.y()-pos.y());
            if (true && distSq < SquaredEpsilonSelf)
                            continue;
            if (distSq < bestDistSq)
            {
                bestDistSq = distSq;
                bestIdx = i;
            }
        }
        if (bestIdx != -1)
                    Results_ptr[index] = HashIndex_ptr[bestIdx].y();
    }
    }
}

