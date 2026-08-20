; ModuleID = 'Case4_ComplexControlFlow/ComplexFlow.ispc'
source_filename = "Case4_ComplexControlFlow/ComplexFlow.ispc"
target datalayout = "e-m:w-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-f80:128-n8:16:32:64-S128"
target triple = "x86_64-pc-windows-msvc"

; Function Attrs: nounwind uwtable
define void @ComplexFlow_ISPC_Impl___un_3C_unf_3E_un_3C_unf_3E_un_3C_unf_3E_unfuni(ptr noalias %a, ptr noalias %b, ptr noalias captures(none) %result, float %threshold, i32 %count, <8 x i32> %__mask) local_unnamed_addr #0 {
allocas:
  %nextras = srem i32 %count, 8
  %aligned_end = sub nsw i32 %count, %nextras
  %before_aligned_end19447 = icmp sgt i32 %aligned_end, 0
  br i1 %before_aligned_end19447, label %foreach_full_body.lr.ph, label %partial_inner_all_outer, !llvm.loop !8

foreach_full_body.lr.ph:                          ; preds = %allocas
  %threshold_load_broadcast = insertelement <8 x float> poison, float %threshold, i64 0
  %threshold_load_broadcast32 = shufflevector <8 x float> %threshold_load_broadcast, <8 x float> poison, <8 x i32> zeroinitializer
  %threshold_load56_negate = fneg float %threshold
  %threshold_load56_negate_broadcast = insertelement <8 x float> poison, float %threshold_load56_negate, i64 0
  %threshold_load56_negate_broadcast57 = shufflevector <8 x float> %threshold_load56_negate_broadcast, <8 x float> poison, <8 x i32> zeroinitializer
  br label %foreach_full_body, !llvm.loop !8

foreach_full_body:                                ; preds = %foreach_full_body.lr.ph, %if_done
  %indvars.iv = phi i64 [ 0, %foreach_full_body.lr.ph ], [ %indvars.iv.next, %if_done ]
  %0 = shl nsw i64 %indvars.iv, 2
  %ptr = getelementptr i8, ptr %a, i64 %0, !filename !10, !first_line !11, !first_column !12, !last_line !11, !last_column !13
  %ptr_masked_load351 = load <8 x float>, ptr %ptr, align 4, !filename !10, !first_line !11, !first_column !12, !last_line !11, !last_column !13
  %greater_v_load_threshold_load_broadcast32 = fcmp ogt <8 x float> %ptr_masked_load351, %threshold_load_broadcast32
  %1 = bitcast <8 x i1> %greater_v_load_threshold_load_broadcast32 to i8
  %cmp.i.not = icmp eq i8 %1, 0
  br i1 %cmp.i.not, label %safe_if_after_true, label %safe_if_run_true

foreach_reset:                                    ; preds = %safe_if_after_true142, %safe_if_run_false198, %safe_if_after_true176, %partial_inner_all_outer
  ret void

outer_not_in_extras.partial_inner_all_outer_crit_edge: ; preds = %if_done
  br label %partial_inner_all_outer, !llvm.loop !8

partial_inner_all_outer:                          ; preds = %outer_not_in_extras.partial_inner_all_outer_crit_edge, %allocas
  %counter.1.lcssa = phi i32 [ %2, %outer_not_in_extras.partial_inner_all_outer_crit_edge ], [ 0, %allocas ]
  %before_full_end = icmp slt i32 %counter.1.lcssa, %count
  br i1 %before_full_end, label %partial_inner_only, label %foreach_reset

if_done:                                          ; preds = %safe_if_after_true60, %safe_if_run_false82, %safe_if_after_true
  %indvars.iv.next = add nuw nsw i64 %indvars.iv, 8
  %2 = trunc nuw i64 %indvars.iv.next to i32
  %before_aligned_end19 = icmp sgt i32 %aligned_end, %2
  br i1 %before_aligned_end19, label %foreach_full_body, label %outer_not_in_extras.partial_inner_all_outer_crit_edge, !llvm.loop !8

safe_if_after_true:                               ; preds = %safe_if_run_true, %foreach_full_body
  %3 = xor <8 x i1> %greater_v_load_threshold_load_broadcast32, splat (i1 true)
  %"~test" = sext <8 x i1> %3 to <8 x i32>
  %4 = bitcast <8 x i1> %3 to i8
  %cmp.i432.not = icmp eq i8 %4, 0
  br i1 %cmp.i432.not, label %if_done, label %safe_if_run_false

safe_if_run_true:                                 ; preds = %foreach_full_body
  %ptr361 = getelementptr i8, ptr %b, i64 %0, !filename !10, !first_line !14, !first_column !15, !last_line !14, !last_column !16
  %floatval.i.i = tail call <8 x float> @llvm.masked.load.v8f32.p0(ptr %ptr361, i32 1, <8 x i1> %greater_v_load_threshold_load_broadcast32, <8 x float> zeroinitializer)
  %mul_v_load40_b_load_offset_load = fmul <8 x float> %ptr_masked_load351, %floatval.i.i
  %ptr364 = getelementptr i8, ptr %result, i64 %0
  call void @llvm.masked.store.v8f32.p0(<8 x float> %mul_v_load40_b_load_offset_load, ptr %ptr364, i32 1, <8 x i1> %greater_v_load_threshold_load_broadcast32)
  br label %safe_if_after_true

safe_if_run_false:                                ; preds = %safe_if_after_true
  %less_v_load55_threshold_load56_negate_broadcast57 = fcmp olt <8 x float> %ptr_masked_load351, %threshold_load56_negate_broadcast57
  %5 = and <8 x i1> %less_v_load55_threshold_load56_negate_broadcast57, %3
  %6 = bitcast <8 x i1> %5 to i8
  %cmp.i433.not = icmp eq i8 %6, 0
  br i1 %cmp.i433.not, label %safe_if_after_true60, label %safe_if_run_true61

safe_if_after_true60:                             ; preds = %safe_if_run_true61, %safe_if_run_false
  %not.less_v_load55_threshold_load56_negate_broadcast57 = xor <8 x i1> %less_v_load55_threshold_load56_negate_broadcast57, splat (i1 true)
  %7 = and <8 x i1> %not.less_v_load55_threshold_load56_negate_broadcast57, %3
  %8 = bitcast <8 x i1> %7 to i8
  %cmp.i434.not = icmp eq i8 %8, 0
  br i1 %cmp.i434.not, label %if_done, label %safe_if_run_false82

safe_if_run_true61:                               ; preds = %safe_if_run_false
  %"oldMask&test62" = select <8 x i1> %less_v_load55_threshold_load56_negate_broadcast57, <8 x i32> %"~test", <8 x i32> zeroinitializer
  %ptr370 = getelementptr i8, ptr %b, i64 %0, !filename !10, !first_line !17, !first_column !15, !last_line !17, !last_column !16
  %floatval.i.i435 = tail call <8 x float> @llvm.x86.avx.maskload.ps.256(ptr readonly %ptr370, <8 x i32> %"oldMask&test62")
  %add_v_load70_b_load72_offset_load = fadd <8 x float> %ptr_masked_load351, %floatval.i.i435
  %ptr377 = getelementptr i8, ptr %result, i64 %0
  call void @llvm.x86.avx.maskstore.ps.256(ptr %ptr377, <8 x i32> %"oldMask&test62", <8 x float> %add_v_load70_b_load72_offset_load)
  br label %safe_if_after_true60

safe_if_run_false82:                              ; preds = %safe_if_after_true60
  %"oldMask&~test84" = select <8 x i1> %less_v_load55_threshold_load56_negate_broadcast57, <8 x i32> zeroinitializer, <8 x i32> %"~test"
  %ptr384 = getelementptr i8, ptr %result, i64 %0
  call void @llvm.x86.avx.maskstore.ps.256(ptr %ptr384, <8 x i32> %"oldMask&~test84", <8 x float> zeroinitializer)
  br label %if_done

partial_inner_only:                               ; preds = %partial_inner_all_outer
  %smear_counter_init119 = insertelement <8 x i32> poison, i32 %counter.1.lcssa, i64 0
  %smear_counter120 = shufflevector <8 x i32> %smear_counter_init119, <8 x i32> poison, <8 x i32> zeroinitializer
  %iter_val121 = or disjoint <8 x i32> %smear_counter120, <i32 0, i32 1, i32 2, i32 3, i32 4, i32 5, i32 6, i32 7>
  %smear_end_init122 = insertelement <8 x i32> poison, i32 %count, i64 0
  %smear_end123 = shufflevector <8 x i32> %smear_end_init122, <8 x i32> poison, <8 x i32> zeroinitializer
  %cmp124 = icmp slt <8 x i32> %iter_val121, %smear_end123
  %cmp124_to_boolvec = sext <8 x i1> %cmp124 to <8 x i32>
  %mul__i_load130.elt0 = shl nsw i32 %counter.1.lcssa, 2
  %9 = zext nneg i32 %mul__i_load130.elt0 to i64
  %ptr355 = getelementptr i8, ptr %a, i64 %9
  %floatval.i.i437 = tail call <8 x float> @llvm.masked.load.v8f32.p0(ptr %ptr355, i32 1, <8 x i1> %cmp124, <8 x float> zeroinitializer)
  %threshold_load138_broadcast = insertelement <8 x float> poison, float %threshold, i64 0
  %threshold_load138_broadcast139 = shufflevector <8 x float> %threshold_load138_broadcast, <8 x float> poison, <8 x i32> zeroinitializer
  %greater_v_load137_threshold_load138_broadcast139 = fcmp ogt <8 x float> %floatval.i.i437, %threshold_load138_broadcast139
  %10 = select <8 x i1> %greater_v_load137_threshold_load138_broadcast139, <8 x i1> %cmp124, <8 x i1> zeroinitializer
  %11 = bitcast <8 x i1> %10 to i8
  %cmp.i439.not = icmp eq i8 %11, 0
  br i1 %cmp.i439.not, label %safe_if_after_true142, label %safe_if_run_true143

safe_if_after_true142:                            ; preds = %safe_if_run_true143, %partial_inner_only
  %"oldMask&~test166" = select <8 x i1> %greater_v_load137_threshold_load138_broadcast139, <8 x i32> zeroinitializer, <8 x i32> %cmp124_to_boolvec
  %not.greater_v_load137_threshold_load138_broadcast139 = xor <8 x i1> %greater_v_load137_threshold_load138_broadcast139, splat (i1 true)
  %12 = select <8 x i1> %not.greater_v_load137_threshold_load138_broadcast139, <8 x i1> %cmp124, <8 x i1> zeroinitializer
  %13 = bitcast <8 x i1> %12 to i8
  %cmp.i440.not = icmp eq i8 %13, 0
  br i1 %cmp.i440.not, label %foreach_reset, label %safe_if_run_false164

safe_if_run_true143:                              ; preds = %partial_inner_only
  %"oldMask&test144" = select <8 x i1> %greater_v_load137_threshold_load138_broadcast139, <8 x i32> %cmp124_to_boolvec, <8 x i32> zeroinitializer
  %ptr392 = getelementptr i8, ptr %b, i64 %9
  %floatval.i.i441 = tail call <8 x float> @llvm.x86.avx.maskload.ps.256(ptr readonly %ptr392, <8 x i32> %"oldMask&test144")
  %mul_v_load152_b_load154_offset_load = fmul <8 x float> %floatval.i.i437, %floatval.i.i441
  %ptr401 = getelementptr i8, ptr %result, i64 %9
  call void @llvm.x86.avx.maskstore.ps.256(ptr %ptr401, <8 x i32> %"oldMask&test144", <8 x float> %mul_v_load152_b_load154_offset_load)
  br label %safe_if_after_true142

safe_if_run_false164:                             ; preds = %safe_if_after_true142
  %threshold_load172_negate = fneg float %threshold
  %threshold_load172_negate_broadcast = insertelement <8 x float> poison, float %threshold_load172_negate, i64 0
  %threshold_load172_negate_broadcast173 = shufflevector <8 x float> %threshold_load172_negate_broadcast, <8 x float> poison, <8 x i32> zeroinitializer
  %less_v_load171_threshold_load172_negate_broadcast173 = fcmp uge <8 x float> %floatval.i.i437, %threshold_load172_negate_broadcast173
  %"oldMask&test178" = select <8 x i1> %less_v_load171_threshold_load172_negate_broadcast173, <8 x i32> zeroinitializer, <8 x i32> %"oldMask&~test166"
  %14 = icmp slt <8 x i32> %"oldMask&test178", zeroinitializer
  %15 = bitcast <8 x i1> %14 to i8
  %cmp.i443.not = icmp eq i8 %15, 0
  br i1 %cmp.i443.not, label %safe_if_after_true176, label %safe_if_run_true177

safe_if_after_true176:                            ; preds = %safe_if_run_true177, %safe_if_run_false164
  %"oldMask&~test200" = select <8 x i1> %less_v_load171_threshold_load172_negate_broadcast173, <8 x i32> %"oldMask&~test166", <8 x i32> zeroinitializer
  %16 = icmp slt <8 x i32> %"oldMask&~test200", zeroinitializer
  %17 = bitcast <8 x i1> %16 to i8
  %cmp.i444.not = icmp eq i8 %17, 0
  br i1 %cmp.i444.not, label %foreach_reset, label %safe_if_run_false198

safe_if_run_true177:                              ; preds = %safe_if_run_false164
  %ptr409 = getelementptr i8, ptr %b, i64 %9
  %floatval.i.i445 = tail call <8 x float> @llvm.x86.avx.maskload.ps.256(ptr readonly %ptr409, <8 x i32> %"oldMask&test178")
  %add_v_load186_b_load188_offset_load = fadd <8 x float> %floatval.i.i437, %floatval.i.i445
  %ptr420 = getelementptr i8, ptr %result, i64 %9
  call void @llvm.x86.avx.maskstore.ps.256(ptr %ptr420, <8 x i32> %"oldMask&test178", <8 x float> %add_v_load186_b_load188_offset_load)
  br label %safe_if_after_true176

safe_if_run_false198:                             ; preds = %safe_if_after_true176
  %ptr431 = getelementptr i8, ptr %result, i64 %9
  call void @llvm.x86.avx.maskstore.ps.256(ptr %ptr431, <8 x i32> %"oldMask&~test200", <8 x float> zeroinitializer)
  br label %foreach_reset
}

; Function Attrs: nounwind uwtable
define void @ComplexFlow_ISPC_Impl(ptr noalias %a, ptr noalias %b, ptr noalias captures(none) %result, float %threshold, i32 %count) local_unnamed_addr #0 {
allocas:
  %nextras = srem i32 %count, 8
  %aligned_end = sub nsw i32 %count, %nextras
  %before_aligned_end10334 = icmp sgt i32 %aligned_end, 0
  br i1 %before_aligned_end10334, label %foreach_full_body.lr.ph, label %partial_inner_all_outer, !llvm.loop !18

foreach_full_body.lr.ph:                          ; preds = %allocas
  %threshold_load_broadcast = insertelement <8 x float> poison, float %threshold, i64 0
  %threshold_load_broadcast19 = shufflevector <8 x float> %threshold_load_broadcast, <8 x float> poison, <8 x i32> zeroinitializer
  %threshold_load33_negate = fneg float %threshold
  %threshold_load33_negate_broadcast = insertelement <8 x float> poison, float %threshold_load33_negate, i64 0
  %threshold_load33_negate_broadcast34 = shufflevector <8 x float> %threshold_load33_negate_broadcast, <8 x float> poison, <8 x i32> zeroinitializer
  br label %foreach_full_body, !llvm.loop !18

foreach_full_body:                                ; preds = %foreach_full_body.lr.ph, %if_done
  %indvars.iv = phi i64 [ 0, %foreach_full_body.lr.ph ], [ %indvars.iv.next, %if_done ]
  %0 = shl nsw i64 %indvars.iv, 2
  %ptr = getelementptr i8, ptr %a, i64 %0, !filename !10, !first_line !11, !first_column !12, !last_line !11, !last_column !13
  %ptr_masked_load238 = load <8 x float>, ptr %ptr, align 4, !filename !10, !first_line !11, !first_column !12, !last_line !11, !last_column !13
  %greater_v_load_threshold_load_broadcast19 = fcmp ogt <8 x float> %ptr_masked_load238, %threshold_load_broadcast19
  %1 = bitcast <8 x i1> %greater_v_load_threshold_load_broadcast19 to i8
  %cmp.i.not = icmp eq i8 %1, 0
  br i1 %cmp.i.not, label %safe_if_after_true, label %safe_if_run_true

foreach_reset:                                    ; preds = %safe_if_after_true92, %safe_if_run_false131, %safe_if_after_true116, %partial_inner_all_outer
  ret void

outer_not_in_extras.partial_inner_all_outer_crit_edge: ; preds = %if_done
  br label %partial_inner_all_outer, !llvm.loop !18

partial_inner_all_outer:                          ; preds = %outer_not_in_extras.partial_inner_all_outer_crit_edge, %allocas
  %counter.1.lcssa = phi i32 [ %2, %outer_not_in_extras.partial_inner_all_outer_crit_edge ], [ 0, %allocas ]
  %before_full_end = icmp slt i32 %counter.1.lcssa, %count
  br i1 %before_full_end, label %partial_inner_only, label %foreach_reset

if_done:                                          ; preds = %safe_if_after_true37, %safe_if_run_false52, %safe_if_after_true
  %indvars.iv.next = add nuw nsw i64 %indvars.iv, 8
  %2 = trunc nuw i64 %indvars.iv.next to i32
  %before_aligned_end10 = icmp sgt i32 %aligned_end, %2
  br i1 %before_aligned_end10, label %foreach_full_body, label %outer_not_in_extras.partial_inner_all_outer_crit_edge, !llvm.loop !18

safe_if_after_true:                               ; preds = %safe_if_run_true, %foreach_full_body
  %3 = xor <8 x i1> %greater_v_load_threshold_load_broadcast19, splat (i1 true)
  %"~test" = sext <8 x i1> %3 to <8 x i32>
  %4 = bitcast <8 x i1> %3 to i8
  %cmp.i319.not = icmp eq i8 %4, 0
  br i1 %cmp.i319.not, label %if_done, label %safe_if_run_false

safe_if_run_true:                                 ; preds = %foreach_full_body
  %ptr248 = getelementptr i8, ptr %b, i64 %0, !filename !10, !first_line !14, !first_column !15, !last_line !14, !last_column !16
  %floatval.i.i = tail call <8 x float> @llvm.masked.load.v8f32.p0(ptr %ptr248, i32 1, <8 x i1> %greater_v_load_threshold_load_broadcast19, <8 x float> zeroinitializer)
  %mul_v_load24_b_load_offset_load = fmul <8 x float> %ptr_masked_load238, %floatval.i.i
  %ptr251 = getelementptr i8, ptr %result, i64 %0
  call void @llvm.masked.store.v8f32.p0(<8 x float> %mul_v_load24_b_load_offset_load, ptr %ptr251, i32 1, <8 x i1> %greater_v_load_threshold_load_broadcast19)
  br label %safe_if_after_true

safe_if_run_false:                                ; preds = %safe_if_after_true
  %less_v_load32_threshold_load33_negate_broadcast34 = fcmp olt <8 x float> %ptr_masked_load238, %threshold_load33_negate_broadcast34
  %5 = and <8 x i1> %less_v_load32_threshold_load33_negate_broadcast34, %3
  %6 = bitcast <8 x i1> %5 to i8
  %cmp.i320.not = icmp eq i8 %6, 0
  br i1 %cmp.i320.not, label %safe_if_after_true37, label %safe_if_run_true38

safe_if_after_true37:                             ; preds = %safe_if_run_true38, %safe_if_run_false
  %not.less_v_load32_threshold_load33_negate_broadcast34 = xor <8 x i1> %less_v_load32_threshold_load33_negate_broadcast34, splat (i1 true)
  %7 = and <8 x i1> %not.less_v_load32_threshold_load33_negate_broadcast34, %3
  %8 = bitcast <8 x i1> %7 to i8
  %cmp.i321.not = icmp eq i8 %8, 0
  br i1 %cmp.i321.not, label %if_done, label %safe_if_run_false52

safe_if_run_true38:                               ; preds = %safe_if_run_false
  %"oldMask&test39" = select <8 x i1> %less_v_load32_threshold_load33_negate_broadcast34, <8 x i32> %"~test", <8 x i32> zeroinitializer
  %ptr257 = getelementptr i8, ptr %b, i64 %0, !filename !10, !first_line !17, !first_column !15, !last_line !17, !last_column !16
  %floatval.i.i322 = tail call <8 x float> @llvm.x86.avx.maskload.ps.256(ptr readonly %ptr257, <8 x i32> %"oldMask&test39")
  %add_v_load44_b_load46_offset_load = fadd <8 x float> %ptr_masked_load238, %floatval.i.i322
  %ptr264 = getelementptr i8, ptr %result, i64 %0
  call void @llvm.x86.avx.maskstore.ps.256(ptr %ptr264, <8 x i32> %"oldMask&test39", <8 x float> %add_v_load44_b_load46_offset_load)
  br label %safe_if_after_true37

safe_if_run_false52:                              ; preds = %safe_if_after_true37
  %"oldMask&~test54" = select <8 x i1> %less_v_load32_threshold_load33_negate_broadcast34, <8 x i32> zeroinitializer, <8 x i32> %"~test"
  %ptr271 = getelementptr i8, ptr %result, i64 %0
  call void @llvm.x86.avx.maskstore.ps.256(ptr %ptr271, <8 x i32> %"oldMask&~test54", <8 x float> zeroinitializer)
  br label %if_done

partial_inner_only:                               ; preds = %partial_inner_all_outer
  %smear_counter_init74 = insertelement <8 x i32> poison, i32 %counter.1.lcssa, i64 0
  %smear_counter75 = shufflevector <8 x i32> %smear_counter_init74, <8 x i32> poison, <8 x i32> zeroinitializer
  %iter_val76 = or disjoint <8 x i32> %smear_counter75, <i32 0, i32 1, i32 2, i32 3, i32 4, i32 5, i32 6, i32 7>
  %smear_end_init77 = insertelement <8 x i32> poison, i32 %count, i64 0
  %smear_end78 = shufflevector <8 x i32> %smear_end_init77, <8 x i32> poison, <8 x i32> zeroinitializer
  %cmp79 = icmp slt <8 x i32> %iter_val76, %smear_end78
  %cmp79_to_boolvec = sext <8 x i1> %cmp79 to <8 x i32>
  %mul__i_load82.elt0 = shl nsw i32 %counter.1.lcssa, 2
  %9 = zext nneg i32 %mul__i_load82.elt0 to i64
  %ptr242 = getelementptr i8, ptr %a, i64 %9
  %floatval.i.i324 = tail call <8 x float> @llvm.masked.load.v8f32.p0(ptr %ptr242, i32 1, <8 x i1> %cmp79, <8 x float> zeroinitializer)
  %threshold_load88_broadcast = insertelement <8 x float> poison, float %threshold, i64 0
  %threshold_load88_broadcast89 = shufflevector <8 x float> %threshold_load88_broadcast, <8 x float> poison, <8 x i32> zeroinitializer
  %greater_v_load87_threshold_load88_broadcast89 = fcmp ogt <8 x float> %floatval.i.i324, %threshold_load88_broadcast89
  %10 = select <8 x i1> %greater_v_load87_threshold_load88_broadcast89, <8 x i1> %cmp79, <8 x i1> zeroinitializer
  %11 = bitcast <8 x i1> %10 to i8
  %cmp.i326.not = icmp eq i8 %11, 0
  br i1 %cmp.i326.not, label %safe_if_after_true92, label %safe_if_run_true93

safe_if_after_true92:                             ; preds = %safe_if_run_true93, %partial_inner_only
  %"oldMask&~test109" = select <8 x i1> %greater_v_load87_threshold_load88_broadcast89, <8 x i32> zeroinitializer, <8 x i32> %cmp79_to_boolvec
  %not.greater_v_load87_threshold_load88_broadcast89 = xor <8 x i1> %greater_v_load87_threshold_load88_broadcast89, splat (i1 true)
  %12 = select <8 x i1> %not.greater_v_load87_threshold_load88_broadcast89, <8 x i1> %cmp79, <8 x i1> zeroinitializer
  %13 = bitcast <8 x i1> %12 to i8
  %cmp.i327.not = icmp eq i8 %13, 0
  br i1 %cmp.i327.not, label %foreach_reset, label %safe_if_run_false107

safe_if_run_true93:                               ; preds = %partial_inner_only
  %"oldMask&test94" = select <8 x i1> %greater_v_load87_threshold_load88_broadcast89, <8 x i32> %cmp79_to_boolvec, <8 x i32> zeroinitializer
  %ptr279 = getelementptr i8, ptr %b, i64 %9
  %floatval.i.i328 = tail call <8 x float> @llvm.x86.avx.maskload.ps.256(ptr readonly %ptr279, <8 x i32> %"oldMask&test94")
  %mul_v_load99_b_load101_offset_load = fmul <8 x float> %floatval.i.i324, %floatval.i.i328
  %ptr288 = getelementptr i8, ptr %result, i64 %9
  call void @llvm.x86.avx.maskstore.ps.256(ptr %ptr288, <8 x i32> %"oldMask&test94", <8 x float> %mul_v_load99_b_load101_offset_load)
  br label %safe_if_after_true92

safe_if_run_false107:                             ; preds = %safe_if_after_true92
  %threshold_load112_negate = fneg float %threshold
  %threshold_load112_negate_broadcast = insertelement <8 x float> poison, float %threshold_load112_negate, i64 0
  %threshold_load112_negate_broadcast113 = shufflevector <8 x float> %threshold_load112_negate_broadcast, <8 x float> poison, <8 x i32> zeroinitializer
  %less_v_load111_threshold_load112_negate_broadcast113 = fcmp uge <8 x float> %floatval.i.i324, %threshold_load112_negate_broadcast113
  %"oldMask&test118" = select <8 x i1> %less_v_load111_threshold_load112_negate_broadcast113, <8 x i32> zeroinitializer, <8 x i32> %"oldMask&~test109"
  %14 = icmp slt <8 x i32> %"oldMask&test118", zeroinitializer
  %15 = bitcast <8 x i1> %14 to i8
  %cmp.i330.not = icmp eq i8 %15, 0
  br i1 %cmp.i330.not, label %safe_if_after_true116, label %safe_if_run_true117

safe_if_after_true116:                            ; preds = %safe_if_run_true117, %safe_if_run_false107
  %"oldMask&~test133" = select <8 x i1> %less_v_load111_threshold_load112_negate_broadcast113, <8 x i32> %"oldMask&~test109", <8 x i32> zeroinitializer
  %16 = icmp slt <8 x i32> %"oldMask&~test133", zeroinitializer
  %17 = bitcast <8 x i1> %16 to i8
  %cmp.i331.not = icmp eq i8 %17, 0
  br i1 %cmp.i331.not, label %foreach_reset, label %safe_if_run_false131

safe_if_run_true117:                              ; preds = %safe_if_run_false107
  %ptr296 = getelementptr i8, ptr %b, i64 %9
  %floatval.i.i332 = tail call <8 x float> @llvm.x86.avx.maskload.ps.256(ptr readonly %ptr296, <8 x i32> %"oldMask&test118")
  %add_v_load123_b_load125_offset_load = fadd <8 x float> %floatval.i.i324, %floatval.i.i332
  %ptr307 = getelementptr i8, ptr %result, i64 %9
  call void @llvm.x86.avx.maskstore.ps.256(ptr %ptr307, <8 x i32> %"oldMask&test118", <8 x float> %add_v_load123_b_load125_offset_load)
  br label %safe_if_after_true116

safe_if_run_false131:                             ; preds = %safe_if_after_true116
  %ptr318 = getelementptr i8, ptr %result, i64 %9
  call void @llvm.x86.avx.maskstore.ps.256(ptr %ptr318, <8 x i32> %"oldMask&~test133", <8 x float> zeroinitializer)
  br label %foreach_reset
}

; Function Attrs: nocallback nofree nosync nounwind willreturn memory(argmem: read)
declare <8 x float> @llvm.x86.avx.maskload.ps.256(ptr, <8 x i32>) #1

; Function Attrs: nounwind memory(argmem: readwrite)
declare void @llvm.x86.avx.maskstore.ps.256(ptr, <8 x i32>, <8 x float>) #2

; Function Attrs: nocallback nofree nosync nounwind willreturn memory(argmem: read)
declare <8 x float> @llvm.masked.load.v8f32.p0(ptr captures(none), i32 immarg, <8 x i1>, <8 x float>) #1

; Function Attrs: nocallback nofree nosync nounwind willreturn memory(argmem: write)
declare void @llvm.masked.store.v8f32.p0(<8 x float>, ptr captures(none), i32 immarg, <8 x i1>) #3

attributes #0 = { nounwind uwtable }
attributes #1 = { nocallback nofree nosync nounwind willreturn memory(argmem: read) }
attributes #2 = { nounwind memory(argmem: readwrite) }
attributes #3 = { nocallback nofree nosync nounwind willreturn memory(argmem: write) }

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
!10 = !{!"Case4_ComplexControlFlow/ComplexFlow.ispc"}
!11 = !{i32 11}
!12 = !{i32 19}
!13 = !{i32 23}
!14 = !{i32 13}
!15 = !{i32 29}
!16 = !{i32 33}
!17 = !{i32 15}
!18 = distinct !{!18, !9}
