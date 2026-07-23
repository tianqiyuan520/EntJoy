; ModuleID = 'Case5_GatherReduce/GatherReduce.ispc'
source_filename = "Case5_GatherReduce/GatherReduce.ispc"
target datalayout = "e-m:w-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-f80:128-n8:16:32:64-S128"
target triple = "x86_64-pc-windows-msvc"

; Function Attrs: nounwind uwtable
define void @GatherReduce_ISPC_Impl___un_3C_unf_3E_un_3C_unf_3E_un_3C_unf_3E_un_3C_unf_3E_un_3C_uni_3E_un_3C_unf_3E_uni(ptr noalias %queryX, ptr noalias %queryY, ptr noalias %dataX, ptr noalias %dataY, ptr noalias %index, ptr noalias captures(none) %result, i32 %count, <8 x i32> %__mask) local_unnamed_addr #0 {
allocas:
  %nextras = srem i32 %count, 8
  %aligned_end = sub nsw i32 %count, %nextras
  %before_aligned_end21410 = icmp sgt i32 %aligned_end, 0
  br i1 %before_aligned_end21410, label %foreach_full_body.lr.ph, label %partial_inner_all_outer, !llvm.loop !8

foreach_full_body.lr.ph:                          ; preds = %allocas
  %0 = zext nneg i32 %aligned_end to i64
  br label %foreach_full_body, !llvm.loop !8

foreach_full_body:                                ; preds = %foreach_full_body.lr.ph, %for_exit
  %indvars.iv = phi i64 [ 0, %foreach_full_body.lr.ph ], [ %indvars.iv.next, %for_exit ]
  %1 = trunc nuw nsw i64 %indvars.iv to i32
  %2 = shl nsw i64 %indvars.iv, 2
  %ptr = getelementptr i8, ptr %queryX, i64 %2, !filename !10, !first_line !11, !first_column !12, !last_line !11, !last_column !13
  %ptr_masked_load335 = load <8 x float>, ptr %ptr, align 4, !filename !10, !first_line !11, !first_column !12, !last_line !11, !last_column !13
  %ptr341 = getelementptr i8, ptr %queryY, i64 %2, !filename !10, !first_line !14, !first_column !12, !last_line !14, !last_column !13
  %ptr341_masked_load342 = load <8 x float>, ptr %ptr341, align 4, !filename !10, !first_line !14, !first_column !12, !last_line !14, !last_column !13
  %3 = mul i32 %1, 50
  %4 = insertelement <8 x i32> undef, i32 %3, i64 0
  %5 = shufflevector <8 x i32> %4, <8 x i32> undef, <8 x i32> zeroinitializer
  %mul_i_load40_ = add nuw <8 x i32> %5, <i32 0, i32 50, i32 100, i32 150, i32 200, i32 250, i32 300, i32 350>
  br label %for_loop

for_loop:                                         ; preds = %foreach_full_body, %for_loop
  %"oldMask&test409" = phi <8 x i32> [ splat (i32 -1), %foreach_full_body ], [ %"oldMask&test", %for_loop ]
  %j.0408 = phi <8 x i32> [ zeroinitializer, %foreach_full_body ], [ %j_load83_plus1, %for_loop ]
  %best.0407 = phi <8 x float> [ splat (float 0x46293E5940000000), %foreach_full_body ], [ %blend.i.i, %for_loop ]
  %add_base_load_j_load49 = add nuw nsw <8 x i32> %mul_i_load40_, %j.0408
  %mul__add_base_load_j_load49 = shl nsw <8 x i32> %add_base_load_j_load49, splat (i32 2)
  %offset_cast344 = zext <8 x i32> %mul__add_base_load_j_load49 to <8 x i64>
  %vecmask_1.i = shufflevector <8 x i32> %"oldMask&test409", <8 x i32> poison, <4 x i32> <i32 0, i32 1, i32 2, i32 3>
  %vecmask_2.i = shufflevector <8 x i32> %"oldMask&test409", <8 x i32> poison, <4 x i32> <i32 4, i32 5, i32 6, i32 7>
  %offsets_1.i = shufflevector <8 x i64> %offset_cast344, <8 x i64> poison, <4 x i32> <i32 0, i32 1, i32 2, i32 3>
  %offsets_2.i = shufflevector <8 x i64> %offset_cast344, <8 x i64> poison, <4 x i32> <i32 4, i32 5, i32 6, i32 7>
  %v1_1.i = tail call <4 x i32> @llvm.x86.avx2.gather.q.d.256(<4 x i32> undef, ptr %index, <4 x i64> %offsets_1.i, <4 x i32> %vecmask_1.i, i8 1)
  %v2_1.i = tail call <4 x i32> @llvm.x86.avx2.gather.q.d.256(<4 x i32> undef, ptr %index, <4 x i64> %offsets_2.i, <4 x i32> %vecmask_2.i, i8 1)
  %v.i = shufflevector <4 x i32> %v1_1.i, <4 x i32> %v2_1.i, <8 x i32> <i32 0, i32 1, i32 2, i32 3, i32 4, i32 5, i32 6, i32 7>
  %mul__idx_load = shl nsw <8 x i32> %v.i, splat (i32 2)
  %mask.i = bitcast <8 x i32> %"oldMask&test409" to <8 x float>
  %v_1.i = tail call <8 x float> @llvm.x86.avx2.gather.d.ps.256(<8 x float> undef, ptr readonly %dataX, <8 x i32> %mul__idx_load, <8 x float> %mask.i, i8 1)
  %sub_qx_load_dataX_load_offset_load = fsub <8 x float> %ptr_masked_load335, %v_1.i
  %v_1.i390 = tail call <8 x float> @llvm.x86.avx2.gather.d.ps.256(<8 x float> undef, ptr readonly %dataY, <8 x i32> %mul__idx_load, <8 x float> %mask.i, i8 1)
  %sub_qy_load_dataY_load_offset_load = fsub <8 x float> %ptr341_masked_load342, %v_1.i390
  %mul_dx_load_dx_load66 = fmul <8 x float> %sub_qx_load_dataX_load_offset_load, %sub_qx_load_dataX_load_offset_load
  %mul_dy_load_dy_load67 = fmul <8 x float> %sub_qy_load_dataY_load_offset_load, %sub_qy_load_dataY_load_offset_load
  %add_mul_dx_load_dx_load66_mul_dy_load_dy_load67 = fadd <8 x float> %mul_dx_load_dx_load66, %mul_dy_load_dy_load67
  %less_distSq_load_best_load = fcmp olt <8 x float> %add_mul_dx_load_dx_load66_mul_dy_load_dy_load67, %best.0407
  %mask_as_float.i.i = select <8 x i1> %less_distSq_load_best_load, <8 x float> %mask.i, <8 x float> zeroinitializer
  %blend.i.i = call <8 x float> @llvm.x86.avx.blendv.ps.256(<8 x float> %best.0407, <8 x float> %add_mul_dx_load_dx_load66_mul_dy_load_dy_load67, <8 x float> %mask_as_float.i.i)
  %j_load83_plus1 = add nuw nsw <8 x i32> %j.0408, splat (i32 1)
  %less_j_load_ = icmp samesign ult <8 x i32> %j.0408, splat (i32 49)
  %"oldMask&test" = select <8 x i1> %less_j_load_, <8 x i32> %"oldMask&test409", <8 x i32> zeroinitializer
  %6 = icmp slt <8 x i32> %"oldMask&test", zeroinitializer
  %7 = bitcast <8 x i1> %6 to i8
  %cmp.i.not = icmp eq i8 %7, 0
  br i1 %cmp.i.not, label %for_exit, label %for_loop, !llvm.loop !15

for_exit:                                         ; preds = %for_loop
  %ptr354 = getelementptr i8, ptr %result, i64 %2
  store <8 x float> %blend.i.i, ptr %ptr354, align 4, !filename !10, !first_line !16, !first_column !17, !last_line !16, !last_column !18
  %indvars.iv.next = add nuw nsw i64 %indvars.iv, 8
  %before_aligned_end21 = icmp samesign ult i64 %indvars.iv.next, %0
  br i1 %before_aligned_end21, label %foreach_full_body, label %outer_not_in_extras.partial_inner_all_outer_crit_edge, !llvm.loop !8

for_loop134:                                      ; preds = %for_loop134.lr.ph, %for_loop134
  %"oldMask&test143415" = phi <8 x i32> [ %cmp109_to_boolvec, %for_loop134.lr.ph ], [ %"oldMask&test143", %for_loop134 ]
  %j140.0414 = phi <8 x i32> [ zeroinitializer, %for_loop134.lr.ph ], [ %j_load208_plus1, %for_loop134 ]
  %best130.0413 = phi <8 x float> [ splat (float 0x46293E5940000000), %for_loop134.lr.ph ], [ %blend.i.i402, %for_loop134 ]
  %add_base_load151_j_load152 = add nuw nsw <8 x i32> %j140.0414, %mul_i_load132_
  %mul__add_base_load151_j_load152 = shl nsw <8 x i32> %add_base_load151_j_load152, splat (i32 2)
  %v_1.i392 = tail call <8 x i32> @llvm.x86.avx2.gather.d.d.256(<8 x i32> undef, ptr readonly %index, <8 x i32> %mul__add_base_load151_j_load152, <8 x i32> %"oldMask&test143415", i8 1)
  %mul__idx_load161 = shl nsw <8 x i32> %v_1.i392, splat (i32 2)
  %mask.i394 = bitcast <8 x i32> %"oldMask&test143415" to <8 x float>
  %v_1.i395 = tail call <8 x float> @llvm.x86.avx2.gather.d.ps.256(<8 x float> undef, ptr readonly %dataX, <8 x i32> %mul__idx_load161, <8 x float> %mask.i394, i8 1)
  %sub_qx_load160_dataX_load162_offset_load = fsub <8 x float> %floatval.i.i, %v_1.i395
  %v_1.i398 = tail call <8 x float> @llvm.x86.avx2.gather.d.ps.256(<8 x float> undef, ptr readonly %dataY, <8 x i32> %mul__idx_load161, <8 x float> %mask.i394, i8 1)
  %sub_qy_load169_dataY_load171_offset_load = fsub <8 x float> %floatval.i.i386, %v_1.i398
  %mul_dx_load178_dx_load179 = fmul <8 x float> %sub_qx_load160_dataX_load162_offset_load, %sub_qx_load160_dataX_load162_offset_load
  %mul_dy_load180_dy_load181 = fmul <8 x float> %sub_qy_load169_dataY_load171_offset_load, %sub_qy_load169_dataY_load171_offset_load
  %add_mul_dx_load178_dx_load179_mul_dy_load180_dy_load181 = fadd <8 x float> %mul_dx_load178_dx_load179, %mul_dy_load180_dy_load181
  %less_distSq_load182_best_load183 = fcmp olt <8 x float> %add_mul_dx_load178_dx_load179_mul_dy_load180_dy_load181, %best130.0413
  %mask_as_float.i.i400 = select <8 x i1> %less_distSq_load182_best_load183, <8 x float> %mask.i394, <8 x float> zeroinitializer
  %blend.i.i402 = call <8 x float> @llvm.x86.avx.blendv.ps.256(<8 x float> %best130.0413, <8 x float> %add_mul_dx_load178_dx_load179_mul_dy_load180_dy_load181, <8 x float> %mask_as_float.i.i400)
  %j_load208_plus1 = add nuw nsw <8 x i32> %j140.0414, splat (i32 1)
  %less_j_load141_ = icmp samesign ult <8 x i32> %j140.0414, splat (i32 49)
  %"oldMask&test143" = select <8 x i1> %less_j_load141_, <8 x i32> %"oldMask&test143415", <8 x i32> zeroinitializer
  %8 = icmp slt <8 x i32> %"oldMask&test143", zeroinitializer
  %9 = bitcast <8 x i1> %8 to i8
  %cmp.i385.not = icmp eq i8 %9, 0
  br i1 %cmp.i385.not, label %for_exit136, label %for_loop134, !llvm.loop !19

for_exit136:                                      ; preds = %for_loop134, %partial_inner_only
  %best130.0.lcssa = phi <8 x float> [ splat (float 0x46293E5940000000), %partial_inner_only ], [ %blend.i.i402, %for_loop134 ]
  %ptr384 = getelementptr i8, ptr %result, i64 %11
  call void @llvm.masked.store.v8f32.p0(<8 x float> %best130.0.lcssa, ptr %ptr384, i32 1, <8 x i1> %cmp109)
  br label %foreach_reset

foreach_reset:                                    ; preds = %for_exit136, %partial_inner_all_outer
  ret void

outer_not_in_extras.partial_inner_all_outer_crit_edge: ; preds = %for_exit
  %10 = trunc nuw nsw i64 %indvars.iv.next to i32
  br label %partial_inner_all_outer, !llvm.loop !8

partial_inner_all_outer:                          ; preds = %outer_not_in_extras.partial_inner_all_outer_crit_edge, %allocas
  %counter.1.lcssa = phi i32 [ %10, %outer_not_in_extras.partial_inner_all_outer_crit_edge ], [ 0, %allocas ]
  %before_full_end = icmp slt i32 %counter.1.lcssa, %count
  br i1 %before_full_end, label %partial_inner_only, label %foreach_reset

partial_inner_only:                               ; preds = %partial_inner_all_outer
  %smear_counter_init104 = insertelement <8 x i32> poison, i32 %counter.1.lcssa, i64 0
  %smear_counter105 = shufflevector <8 x i32> %smear_counter_init104, <8 x i32> poison, <8 x i32> zeroinitializer
  %iter_val106 = or disjoint <8 x i32> %smear_counter105, <i32 0, i32 1, i32 2, i32 3, i32 4, i32 5, i32 6, i32 7>
  %smear_end_init107 = insertelement <8 x i32> poison, i32 %count, i64 0
  %smear_end108 = shufflevector <8 x i32> %smear_end_init107, <8 x i32> poison, <8 x i32> zeroinitializer
  %cmp109 = icmp slt <8 x i32> %iter_val106, %smear_end108
  %mul__i_load115.elt0 = shl nsw i32 %counter.1.lcssa, 2
  %11 = zext nneg i32 %mul__i_load115.elt0 to i64
  %ptr358 = getelementptr i8, ptr %queryX, i64 %11
  %floatval.i.i = tail call <8 x float> @llvm.masked.load.v8f32.p0(ptr %ptr358, i32 1, <8 x i1> %cmp109, <8 x float> zeroinitializer)
  %ptr366 = getelementptr i8, ptr %queryY, i64 %11
  %floatval.i.i386 = tail call <8 x float> @llvm.masked.load.v8f32.p0(ptr %ptr366, i32 1, <8 x i1> %cmp109, <8 x float> zeroinitializer)
  %mul_i_load132_ = mul nuw nsw <8 x i32> %iter_val106, splat (i32 50)
  %12 = bitcast <8 x i1> %cmp109 to i8
  %cmp.i385.not412 = icmp eq i8 %12, 0
  br i1 %cmp.i385.not412, label %for_exit136, label %for_loop134.lr.ph

for_loop134.lr.ph:                                ; preds = %partial_inner_only
  %cmp109_to_boolvec = sext <8 x i1> %cmp109 to <8 x i32>
  br label %for_loop134
}

; Function Attrs: nounwind uwtable
define void @GatherReduce_ISPC_Impl(ptr noalias %queryX, ptr noalias %queryY, ptr noalias %dataX, ptr noalias %dataY, ptr noalias %index, ptr noalias captures(none) %result, i32 %count) local_unnamed_addr #0 {
allocas:
  %nextras = srem i32 %count, 8
  %aligned_end = sub nsw i32 %count, %nextras
  %before_aligned_end12313 = icmp sgt i32 %aligned_end, 0
  br i1 %before_aligned_end12313, label %foreach_full_body.lr.ph, label %partial_inner_all_outer, !llvm.loop !20

foreach_full_body.lr.ph:                          ; preds = %allocas
  %0 = zext nneg i32 %aligned_end to i64
  br label %foreach_full_body, !llvm.loop !20

foreach_full_body:                                ; preds = %foreach_full_body.lr.ph, %for_exit
  %indvars.iv = phi i64 [ 0, %foreach_full_body.lr.ph ], [ %indvars.iv.next, %for_exit ]
  %1 = trunc nuw nsw i64 %indvars.iv to i32
  %2 = shl nsw i64 %indvars.iv, 2
  %ptr = getelementptr i8, ptr %queryX, i64 %2, !filename !10, !first_line !11, !first_column !12, !last_line !11, !last_column !13
  %ptr_masked_load238 = load <8 x float>, ptr %ptr, align 4, !filename !10, !first_line !11, !first_column !12, !last_line !11, !last_column !13
  %ptr244 = getelementptr i8, ptr %queryY, i64 %2, !filename !10, !first_line !14, !first_column !12, !last_line !14, !last_column !13
  %ptr244_masked_load245 = load <8 x float>, ptr %ptr244, align 4, !filename !10, !first_line !14, !first_column !12, !last_line !14, !last_column !13
  %3 = mul i32 %1, 50
  %4 = insertelement <8 x i32> undef, i32 %3, i64 0
  %5 = shufflevector <8 x i32> %4, <8 x i32> undef, <8 x i32> zeroinitializer
  %mul_i_load25_ = add nuw <8 x i32> %5, <i32 0, i32 50, i32 100, i32 150, i32 200, i32 250, i32 300, i32 350>
  br label %for_loop

for_loop:                                         ; preds = %foreach_full_body, %for_loop
  %"oldMask&test312" = phi <8 x i32> [ splat (i32 -1), %foreach_full_body ], [ %"oldMask&test", %for_loop ]
  %j.0311 = phi <8 x i32> [ zeroinitializer, %foreach_full_body ], [ %j_load50_plus1, %for_loop ]
  %best.0310 = phi <8 x float> [ splat (float 0x46293E5940000000), %foreach_full_body ], [ %blend.i.i, %for_loop ]
  %add_base_load_j_load30 = add nuw nsw <8 x i32> %mul_i_load25_, %j.0311
  %mul__add_base_load_j_load30 = shl nsw <8 x i32> %add_base_load_j_load30, splat (i32 2)
  %offset_cast247 = zext <8 x i32> %mul__add_base_load_j_load30 to <8 x i64>
  %vecmask_1.i = shufflevector <8 x i32> %"oldMask&test312", <8 x i32> poison, <4 x i32> <i32 0, i32 1, i32 2, i32 3>
  %vecmask_2.i = shufflevector <8 x i32> %"oldMask&test312", <8 x i32> poison, <4 x i32> <i32 4, i32 5, i32 6, i32 7>
  %offsets_1.i = shufflevector <8 x i64> %offset_cast247, <8 x i64> poison, <4 x i32> <i32 0, i32 1, i32 2, i32 3>
  %offsets_2.i = shufflevector <8 x i64> %offset_cast247, <8 x i64> poison, <4 x i32> <i32 4, i32 5, i32 6, i32 7>
  %v1_1.i = tail call <4 x i32> @llvm.x86.avx2.gather.q.d.256(<4 x i32> undef, ptr %index, <4 x i64> %offsets_1.i, <4 x i32> %vecmask_1.i, i8 1)
  %v2_1.i = tail call <4 x i32> @llvm.x86.avx2.gather.q.d.256(<4 x i32> undef, ptr %index, <4 x i64> %offsets_2.i, <4 x i32> %vecmask_2.i, i8 1)
  %v.i = shufflevector <4 x i32> %v1_1.i, <4 x i32> %v2_1.i, <8 x i32> <i32 0, i32 1, i32 2, i32 3, i32 4, i32 5, i32 6, i32 7>
  %mul__idx_load = shl nsw <8 x i32> %v.i, splat (i32 2)
  %mask.i = bitcast <8 x i32> %"oldMask&test312" to <8 x float>
  %v_1.i = tail call <8 x float> @llvm.x86.avx2.gather.d.ps.256(<8 x float> undef, ptr readonly %dataX, <8 x i32> %mul__idx_load, <8 x float> %mask.i, i8 1)
  %sub_qx_load_dataX_load_offset_load = fsub <8 x float> %ptr_masked_load238, %v_1.i
  %v_1.i293 = tail call <8 x float> @llvm.x86.avx2.gather.d.ps.256(<8 x float> undef, ptr readonly %dataY, <8 x i32> %mul__idx_load, <8 x float> %mask.i, i8 1)
  %sub_qy_load_dataY_load_offset_load = fsub <8 x float> %ptr244_masked_load245, %v_1.i293
  %mul_dx_load_dx_load41 = fmul <8 x float> %sub_qx_load_dataX_load_offset_load, %sub_qx_load_dataX_load_offset_load
  %mul_dy_load_dy_load42 = fmul <8 x float> %sub_qy_load_dataY_load_offset_load, %sub_qy_load_dataY_load_offset_load
  %add_mul_dx_load_dx_load41_mul_dy_load_dy_load42 = fadd <8 x float> %mul_dx_load_dx_load41, %mul_dy_load_dy_load42
  %less_distSq_load_best_load = fcmp olt <8 x float> %add_mul_dx_load_dx_load41_mul_dy_load_dy_load42, %best.0310
  %mask_as_float.i.i = select <8 x i1> %less_distSq_load_best_load, <8 x float> %mask.i, <8 x float> zeroinitializer
  %blend.i.i = call <8 x float> @llvm.x86.avx.blendv.ps.256(<8 x float> %best.0310, <8 x float> %add_mul_dx_load_dx_load41_mul_dy_load_dy_load42, <8 x float> %mask_as_float.i.i)
  %j_load50_plus1 = add nuw nsw <8 x i32> %j.0311, splat (i32 1)
  %less_j_load_ = icmp samesign ult <8 x i32> %j.0311, splat (i32 49)
  %"oldMask&test" = select <8 x i1> %less_j_load_, <8 x i32> %"oldMask&test312", <8 x i32> zeroinitializer
  %6 = icmp slt <8 x i32> %"oldMask&test", zeroinitializer
  %7 = bitcast <8 x i1> %6 to i8
  %cmp.i.not = icmp eq i8 %7, 0
  br i1 %cmp.i.not, label %for_exit, label %for_loop, !llvm.loop !21

for_exit:                                         ; preds = %for_loop
  %ptr257 = getelementptr i8, ptr %result, i64 %2
  store <8 x float> %blend.i.i, ptr %ptr257, align 4, !filename !10, !first_line !16, !first_column !17, !last_line !16, !last_column !18
  %indvars.iv.next = add nuw nsw i64 %indvars.iv, 8
  %before_aligned_end12 = icmp samesign ult i64 %indvars.iv.next, %0
  br i1 %before_aligned_end12, label %foreach_full_body, label %outer_not_in_extras.partial_inner_all_outer_crit_edge, !llvm.loop !20

for_loop88:                                       ; preds = %for_loop88.lr.ph, %for_loop88
  %"oldMask&test97318" = phi <8 x i32> [ %cmp70_to_boolvec, %for_loop88.lr.ph ], [ %"oldMask&test97", %for_loop88 ]
  %j94.0317 = phi <8 x i32> [ zeroinitializer, %for_loop88.lr.ph ], [ %j_load144_plus1, %for_loop88 ]
  %best84.0316 = phi <8 x float> [ splat (float 0x46293E5940000000), %for_loop88.lr.ph ], [ %blend.i.i305, %for_loop88 ]
  %add_base_load101_j_load102 = add nuw nsw <8 x i32> %j94.0317, %mul_i_load86_
  %mul__add_base_load101_j_load102 = shl nsw <8 x i32> %add_base_load101_j_load102, splat (i32 2)
  %v_1.i295 = tail call <8 x i32> @llvm.x86.avx2.gather.d.d.256(<8 x i32> undef, ptr readonly %index, <8 x i32> %mul__add_base_load101_j_load102, <8 x i32> %"oldMask&test97318", i8 1)
  %mul__idx_load109 = shl nsw <8 x i32> %v_1.i295, splat (i32 2)
  %mask.i297 = bitcast <8 x i32> %"oldMask&test97318" to <8 x float>
  %v_1.i298 = tail call <8 x float> @llvm.x86.avx2.gather.d.ps.256(<8 x float> undef, ptr readonly %dataX, <8 x i32> %mul__idx_load109, <8 x float> %mask.i297, i8 1)
  %sub_qx_load108_dataX_load110_offset_load = fsub <8 x float> %floatval.i.i, %v_1.i298
  %v_1.i301 = tail call <8 x float> @llvm.x86.avx2.gather.d.ps.256(<8 x float> undef, ptr readonly %dataY, <8 x i32> %mul__idx_load109, <8 x float> %mask.i297, i8 1)
  %sub_qy_load115_dataY_load117_offset_load = fsub <8 x float> %floatval.i.i289, %v_1.i301
  %mul_dx_load122_dx_load123 = fmul <8 x float> %sub_qx_load108_dataX_load110_offset_load, %sub_qx_load108_dataX_load110_offset_load
  %mul_dy_load124_dy_load125 = fmul <8 x float> %sub_qy_load115_dataY_load117_offset_load, %sub_qy_load115_dataY_load117_offset_load
  %add_mul_dx_load122_dx_load123_mul_dy_load124_dy_load125 = fadd <8 x float> %mul_dx_load122_dx_load123, %mul_dy_load124_dy_load125
  %less_distSq_load126_best_load127 = fcmp olt <8 x float> %add_mul_dx_load122_dx_load123_mul_dy_load124_dy_load125, %best84.0316
  %mask_as_float.i.i303 = select <8 x i1> %less_distSq_load126_best_load127, <8 x float> %mask.i297, <8 x float> zeroinitializer
  %blend.i.i305 = call <8 x float> @llvm.x86.avx.blendv.ps.256(<8 x float> %best84.0316, <8 x float> %add_mul_dx_load122_dx_load123_mul_dy_load124_dy_load125, <8 x float> %mask_as_float.i.i303)
  %j_load144_plus1 = add nuw nsw <8 x i32> %j94.0317, splat (i32 1)
  %less_j_load95_ = icmp samesign ult <8 x i32> %j94.0317, splat (i32 49)
  %"oldMask&test97" = select <8 x i1> %less_j_load95_, <8 x i32> %"oldMask&test97318", <8 x i32> zeroinitializer
  %8 = icmp slt <8 x i32> %"oldMask&test97", zeroinitializer
  %9 = bitcast <8 x i1> %8 to i8
  %cmp.i288.not = icmp eq i8 %9, 0
  br i1 %cmp.i288.not, label %for_exit90, label %for_loop88, !llvm.loop !22

for_exit90:                                       ; preds = %for_loop88, %partial_inner_only
  %best84.0.lcssa = phi <8 x float> [ splat (float 0x46293E5940000000), %partial_inner_only ], [ %blend.i.i305, %for_loop88 ]
  %ptr287 = getelementptr i8, ptr %result, i64 %11
  call void @llvm.masked.store.v8f32.p0(<8 x float> %best84.0.lcssa, ptr %ptr287, i32 1, <8 x i1> %cmp70)
  br label %foreach_reset

foreach_reset:                                    ; preds = %for_exit90, %partial_inner_all_outer
  ret void

outer_not_in_extras.partial_inner_all_outer_crit_edge: ; preds = %for_exit
  %10 = trunc nuw nsw i64 %indvars.iv.next to i32
  br label %partial_inner_all_outer, !llvm.loop !20

partial_inner_all_outer:                          ; preds = %outer_not_in_extras.partial_inner_all_outer_crit_edge, %allocas
  %counter.1.lcssa = phi i32 [ %10, %outer_not_in_extras.partial_inner_all_outer_crit_edge ], [ 0, %allocas ]
  %before_full_end = icmp slt i32 %counter.1.lcssa, %count
  br i1 %before_full_end, label %partial_inner_only, label %foreach_reset

partial_inner_only:                               ; preds = %partial_inner_all_outer
  %smear_counter_init65 = insertelement <8 x i32> poison, i32 %counter.1.lcssa, i64 0
  %smear_counter66 = shufflevector <8 x i32> %smear_counter_init65, <8 x i32> poison, <8 x i32> zeroinitializer
  %iter_val67 = or disjoint <8 x i32> %smear_counter66, <i32 0, i32 1, i32 2, i32 3, i32 4, i32 5, i32 6, i32 7>
  %smear_end_init68 = insertelement <8 x i32> poison, i32 %count, i64 0
  %smear_end69 = shufflevector <8 x i32> %smear_end_init68, <8 x i32> poison, <8 x i32> zeroinitializer
  %cmp70 = icmp slt <8 x i32> %iter_val67, %smear_end69
  %mul__i_load73.elt0 = shl nsw i32 %counter.1.lcssa, 2
  %11 = zext nneg i32 %mul__i_load73.elt0 to i64
  %ptr261 = getelementptr i8, ptr %queryX, i64 %11
  %floatval.i.i = tail call <8 x float> @llvm.masked.load.v8f32.p0(ptr %ptr261, i32 1, <8 x i1> %cmp70, <8 x float> zeroinitializer)
  %ptr269 = getelementptr i8, ptr %queryY, i64 %11
  %floatval.i.i289 = tail call <8 x float> @llvm.masked.load.v8f32.p0(ptr %ptr269, i32 1, <8 x i1> %cmp70, <8 x float> zeroinitializer)
  %mul_i_load86_ = mul nuw nsw <8 x i32> %iter_val67, splat (i32 50)
  %12 = bitcast <8 x i1> %cmp70 to i8
  %cmp.i288.not315 = icmp eq i8 %12, 0
  br i1 %cmp.i288.not315, label %for_exit90, label %for_loop88.lr.ph

for_loop88.lr.ph:                                 ; preds = %partial_inner_only
  %cmp70_to_boolvec = sext <8 x i1> %cmp70 to <8 x i32>
  br label %for_loop88
}

; Function Attrs: nocallback nofree nosync nounwind willreturn memory(read)
declare <8 x i32> @llvm.x86.avx2.gather.d.d.256(<8 x i32>, ptr, <8 x i32>, <8 x i32>, i8 immarg) #1

; Function Attrs: nocallback nofree nosync nounwind willreturn memory(read)
declare <4 x i32> @llvm.x86.avx2.gather.q.d.256(<4 x i32>, ptr, <4 x i64>, <4 x i32>, i8 immarg) #1

; Function Attrs: nocallback nofree nosync nounwind willreturn memory(read)
declare <8 x float> @llvm.x86.avx2.gather.d.ps.256(<8 x float>, ptr, <8 x i32>, <8 x float>, i8 immarg) #1

; Function Attrs: nocallback nofree nosync nounwind willreturn memory(none)
declare <8 x float> @llvm.x86.avx.blendv.ps.256(<8 x float>, <8 x float>, <8 x float>) #2

; Function Attrs: nocallback nofree nosync nounwind willreturn memory(argmem: read)
declare <8 x float> @llvm.masked.load.v8f32.p0(ptr captures(none), i32 immarg, <8 x i1>, <8 x float>) #3

; Function Attrs: nocallback nofree nosync nounwind willreturn memory(argmem: write)
declare void @llvm.masked.store.v8f32.p0(<8 x float>, ptr captures(none), i32 immarg, <8 x i1>) #4

attributes #0 = { nounwind uwtable }
attributes #1 = { nocallback nofree nosync nounwind willreturn memory(read) }
attributes #2 = { nocallback nofree nosync nounwind willreturn memory(none) }
attributes #3 = { nocallback nofree nosync nounwind willreturn memory(argmem: read) }
attributes #4 = { nocallback nofree nosync nounwind willreturn memory(argmem: write) }

!llvm.ident = !{!0, !1}
!llvm.linker.options = !{!2}
!llvm.module.flags = !{!3, !4, !5, !6, !7}

!0 = !{!"Intel(r) Implicit SPMD Program Compiler (Intel(r) ISPC), 1.30.0 (build commit 3fc6d50cf24dc8b4 @ 20260204, LLVM 21.1.8)"}
!1 = !{!"LLVM version 21.1.8 (https://github.com/llvm/llvm-project.git 2078da43e25a4623cab2d0d60decddf709aaea28)"}
!2 = !{!"/FAILIFMISMATCH:\22_CRT_STDIO_ISO_WIDE_SPECIFIERS=0\22"}
!3 = !{i32 2, !"Debug Info Version", i32 3}
!4 = !{i32 1, !"wchar_size", i32 2}
!5 = !{i32 8, !"PIC Level", i32 2}
!6 = !{i32 7, !"uwtable", i32 2}
!7 = !{i32 1, !"MaxTLSAlign", i32 65536}
!8 = distinct !{!8, !9}
!9 = !{!"llvm.loop.mustprogress"}
!10 = !{!"Case5_GatherReduce/GatherReduce.ispc"}
!11 = !{i32 13}
!12 = !{i32 20}
!13 = !{i32 29}
!14 = !{i32 14}
!15 = distinct !{!15, !9}
!16 = !{i32 25}
!17 = !{i32 9}
!18 = !{i32 18}
!19 = distinct !{!19, !9}
!20 = distinct !{!20, !9}
!21 = distinct !{!21, !9}
!22 = distinct !{!22, !9}
