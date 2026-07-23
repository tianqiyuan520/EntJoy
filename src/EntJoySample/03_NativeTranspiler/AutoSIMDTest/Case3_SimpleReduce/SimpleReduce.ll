; ModuleID = 'Case3_SimpleReduce/SimpleReduce.ispc'
source_filename = "Case3_SimpleReduce/SimpleReduce.ispc"
target datalayout = "e-m:w-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-f80:128-n8:16:32:64-S128"
target triple = "x86_64-pc-windows-msvc"

; Function Attrs: nounwind uwtable
define void @SimpleReduce_ISPC_Impl___un_3C_unf_3E_un_3C_unf_3E_uni(ptr noalias %a, ptr noalias captures(none) %result, i32 %count, <8 x i32> %__mask) local_unnamed_addr #0 {
allocas:
  %nextras = srem i32 %count, 8
  %aligned_end = sub nsw i32 %count, %nextras
  %before_aligned_end17264 = icmp sgt i32 %aligned_end, 0
  br i1 %before_aligned_end17264, label %foreach_full_body.lr.ph, label %partial_inner_all_outer, !llvm.loop !8

foreach_full_body.lr.ph:                          ; preds = %allocas
  %0 = zext nneg i32 %aligned_end to i64
  br label %foreach_full_body, !llvm.loop !8

foreach_full_body:                                ; preds = %foreach_full_body.lr.ph, %for_exit
  %indvars.iv = phi i64 [ 0, %foreach_full_body.lr.ph ], [ %indvars.iv.next, %for_exit ]
  %1 = trunc nuw nsw i64 %indvars.iv to i32
  %2 = mul i32 %1, 100
  %3 = insertelement <8 x i32> undef, i32 %2, i64 0
  %4 = shufflevector <8 x i32> %3, <8 x i32> undef, <8 x i32> zeroinitializer
  %mul_i_load24_ = add nuw <8 x i32> %4, <i32 0, i32 100, i32 200, i32 300, i32 400, i32 500, i32 600, i32 700>
  br label %for_loop

for_loop:                                         ; preds = %foreach_full_body, %for_loop
  %"oldMask&test263" = phi <8 x i32> [ splat (i32 -1), %foreach_full_body ], [ %"oldMask&test", %for_loop ]
  %j.0262 = phi <8 x i32> [ zeroinitializer, %foreach_full_body ], [ %j_load54_plus1, %for_loop ]
  %best.0261 = phi <8 x float> [ splat (float 0x46293E5940000000), %foreach_full_body ], [ %blend.i.i, %for_loop ]
  %add_base_load_j_load33 = add nuw nsw <8 x i32> %mul_i_load24_, %j.0262
  %mul__add_base_load_j_load33 = shl nsw <8 x i32> %add_base_load_j_load33, splat (i32 2)
  %offset_cast = zext <8 x i32> %mul__add_base_load_j_load33 to <8 x i64>
  %mask.i = bitcast <8 x i32> %"oldMask&test263" to <8 x float>
  %offsets_1.i = shufflevector <8 x i64> %offset_cast, <8 x i64> poison, <4 x i32> <i32 0, i32 1, i32 2, i32 3>
  %offsets_2.i = shufflevector <8 x i64> %offset_cast, <8 x i64> poison, <4 x i32> <i32 4, i32 5, i32 6, i32 7>
  %mask_1.i = shufflevector <8 x float> %mask.i, <8 x float> poison, <4 x i32> <i32 0, i32 1, i32 2, i32 3>
  %mask_2.i = shufflevector <8 x float> %mask.i, <8 x float> poison, <4 x i32> <i32 4, i32 5, i32 6, i32 7>
  %v1_1.i = tail call <4 x float> @llvm.x86.avx2.gather.q.ps.256(<4 x float> undef, ptr %a, <4 x i64> %offsets_1.i, <4 x float> %mask_1.i, i8 1)
  %v2_1.i = tail call <4 x float> @llvm.x86.avx2.gather.q.ps.256(<4 x float> undef, ptr %a, <4 x i64> %offsets_2.i, <4 x float> %mask_2.i, i8 1)
  %v.i = shufflevector <4 x float> %v1_1.i, <4 x float> %v2_1.i, <8 x i32> <i32 0, i32 1, i32 2, i32 3, i32 4, i32 5, i32 6, i32 7>
  %less_v_load_best_load = fcmp olt <8 x float> %v.i, %best.0261
  %mask_as_float.i.i = select <8 x i1> %less_v_load_best_load, <8 x float> %mask.i, <8 x float> zeroinitializer
  %blend.i.i = call <8 x float> @llvm.x86.avx.blendv.ps.256(<8 x float> %best.0261, <8 x float> %v.i, <8 x float> %mask_as_float.i.i)
  %j_load54_plus1 = add nuw nsw <8 x i32> %j.0262, splat (i32 1)
  %less_j_load_ = icmp samesign ult <8 x i32> %j.0262, splat (i32 99)
  %"oldMask&test" = select <8 x i1> %less_j_load_, <8 x i32> %"oldMask&test263", <8 x i32> zeroinitializer
  %5 = icmp slt <8 x i32> %"oldMask&test", zeroinitializer
  %6 = bitcast <8 x i1> %5 to i8
  %cmp.i.not = icmp eq i8 %6, 0
  br i1 %cmp.i.not, label %for_exit, label %for_loop, !llvm.loop !10

for_exit:                                         ; preds = %for_loop
  %7 = shl nsw i64 %indvars.iv, 2
  %ptr = getelementptr i8, ptr %result, i64 %7
  store <8 x float> %blend.i.i, ptr %ptr, align 4, !filename !11, !first_line !12, !first_column !13, !last_line !12, !last_column !14
  %indvars.iv.next = add nuw nsw i64 %indvars.iv, 8
  %before_aligned_end17 = icmp samesign ult i64 %indvars.iv.next, %0
  br i1 %before_aligned_end17, label %foreach_full_body, label %outer_not_in_extras.partial_inner_all_outer_crit_edge, !llvm.loop !8

for_loop89:                                       ; preds = %for_loop89.lr.ph, %for_loop89
  %"oldMask&test98269" = phi <8 x i32> [ %cmp80_to_boolvec, %for_loop89.lr.ph ], [ %"oldMask&test98", %for_loop89 ]
  %j95.0268 = phi <8 x i32> [ zeroinitializer, %for_loop89.lr.ph ], [ %j_load140_plus1, %for_loop89 ]
  %best85.0267 = phi <8 x float> [ splat (float 0x46293E5940000000), %for_loop89.lr.ph ], [ %blend.i.i256, %for_loop89 ]
  %add_base_load106_j_load107 = add nuw nsw <8 x i32> %j95.0268, %mul_i_load87_
  %mul__add_base_load106_j_load107 = shl nsw <8 x i32> %add_base_load106_j_load107, splat (i32 2)
  %mask.i252 = bitcast <8 x i32> %"oldMask&test98269" to <8 x float>
  %v_1.i = tail call <8 x float> @llvm.x86.avx2.gather.d.ps.256(<8 x float> undef, ptr readonly %a, <8 x i32> %mul__add_base_load106_j_load107, <8 x float> %mask.i252, i8 1)
  %less_v_load114_best_load115 = fcmp olt <8 x float> %v_1.i, %best85.0267
  %mask_as_float.i.i254 = select <8 x i1> %less_v_load114_best_load115, <8 x float> %mask.i252, <8 x float> zeroinitializer
  %blend.i.i256 = call <8 x float> @llvm.x86.avx.blendv.ps.256(<8 x float> %best85.0267, <8 x float> %v_1.i, <8 x float> %mask_as_float.i.i254)
  %j_load140_plus1 = add nuw nsw <8 x i32> %j95.0268, splat (i32 1)
  %less_j_load96_ = icmp samesign ult <8 x i32> %j95.0268, splat (i32 99)
  %"oldMask&test98" = select <8 x i1> %less_j_load96_, <8 x i32> %"oldMask&test98269", <8 x i32> zeroinitializer
  %8 = icmp slt <8 x i32> %"oldMask&test98", zeroinitializer
  %9 = bitcast <8 x i1> %8 to i8
  %cmp.i251.not = icmp eq i8 %9, 0
  br i1 %cmp.i251.not, label %for_exit91, label %for_loop89, !llvm.loop !15

for_exit91:                                       ; preds = %for_loop89, %partial_inner_only
  %best85.0.lcssa = phi <8 x float> [ splat (float 0x46293E5940000000), %partial_inner_only ], [ %blend.i.i256, %for_loop89 ]
  %mul__i_load146.elt0 = shl nsw i32 %counter.1.lcssa, 2
  %10 = zext nneg i32 %mul__i_load146.elt0 to i64
  %ptr250 = getelementptr i8, ptr %result, i64 %10
  call void @llvm.masked.store.v8f32.p0(<8 x float> %best85.0.lcssa, ptr %ptr250, i32 1, <8 x i1> %cmp80)
  br label %foreach_reset

foreach_reset:                                    ; preds = %for_exit91, %partial_inner_all_outer
  ret void

outer_not_in_extras.partial_inner_all_outer_crit_edge: ; preds = %for_exit
  %11 = trunc nuw nsw i64 %indvars.iv.next to i32
  br label %partial_inner_all_outer, !llvm.loop !8

partial_inner_all_outer:                          ; preds = %outer_not_in_extras.partial_inner_all_outer_crit_edge, %allocas
  %counter.1.lcssa = phi i32 [ %11, %outer_not_in_extras.partial_inner_all_outer_crit_edge ], [ 0, %allocas ]
  %before_full_end = icmp slt i32 %counter.1.lcssa, %count
  br i1 %before_full_end, label %partial_inner_only, label %foreach_reset

partial_inner_only:                               ; preds = %partial_inner_all_outer
  %smear_counter_init75 = insertelement <8 x i32> poison, i32 %counter.1.lcssa, i64 0
  %smear_counter76 = shufflevector <8 x i32> %smear_counter_init75, <8 x i32> poison, <8 x i32> zeroinitializer
  %iter_val77 = or disjoint <8 x i32> %smear_counter76, <i32 0, i32 1, i32 2, i32 3, i32 4, i32 5, i32 6, i32 7>
  %smear_end_init78 = insertelement <8 x i32> poison, i32 %count, i64 0
  %smear_end79 = shufflevector <8 x i32> %smear_end_init78, <8 x i32> poison, <8 x i32> zeroinitializer
  %cmp80 = icmp slt <8 x i32> %iter_val77, %smear_end79
  %mul_i_load87_ = mul nuw nsw <8 x i32> %iter_val77, splat (i32 100)
  %12 = bitcast <8 x i1> %cmp80 to i8
  %cmp.i251.not266 = icmp eq i8 %12, 0
  br i1 %cmp.i251.not266, label %for_exit91, label %for_loop89.lr.ph

for_loop89.lr.ph:                                 ; preds = %partial_inner_only
  %cmp80_to_boolvec = sext <8 x i1> %cmp80 to <8 x i32>
  br label %for_loop89
}

; Function Attrs: nounwind uwtable
define void @SimpleReduce_ISPC_Impl(ptr noalias %a, ptr noalias captures(none) %result, i32 %count) local_unnamed_addr #0 {
allocas:
  %nextras = srem i32 %count, 8
  %aligned_end = sub nsw i32 %count, %nextras
  %before_aligned_end8183 = icmp sgt i32 %aligned_end, 0
  br i1 %before_aligned_end8183, label %foreach_full_body.lr.ph, label %partial_inner_all_outer, !llvm.loop !16

foreach_full_body.lr.ph:                          ; preds = %allocas
  %0 = zext nneg i32 %aligned_end to i64
  br label %foreach_full_body, !llvm.loop !16

foreach_full_body:                                ; preds = %foreach_full_body.lr.ph, %for_exit
  %indvars.iv = phi i64 [ 0, %foreach_full_body.lr.ph ], [ %indvars.iv.next, %for_exit ]
  %1 = trunc nuw nsw i64 %indvars.iv to i32
  %2 = mul i32 %1, 100
  %3 = insertelement <8 x i32> undef, i32 %2, i64 0
  %4 = shufflevector <8 x i32> %3, <8 x i32> undef, <8 x i32> zeroinitializer
  %mul_i_load13_ = add nuw <8 x i32> %4, <i32 0, i32 100, i32 200, i32 300, i32 400, i32 500, i32 600, i32 700>
  br label %for_loop

for_loop:                                         ; preds = %foreach_full_body, %for_loop
  %"oldMask&test182" = phi <8 x i32> [ splat (i32 -1), %foreach_full_body ], [ %"oldMask&test", %for_loop ]
  %j.0181 = phi <8 x i32> [ zeroinitializer, %foreach_full_body ], [ %j_load29_plus1, %for_loop ]
  %best.0180 = phi <8 x float> [ splat (float 0x46293E5940000000), %foreach_full_body ], [ %blend.i.i, %for_loop ]
  %add_base_load_j_load18 = add nuw nsw <8 x i32> %mul_i_load13_, %j.0181
  %mul__add_base_load_j_load18 = shl nsw <8 x i32> %add_base_load_j_load18, splat (i32 2)
  %offset_cast = zext <8 x i32> %mul__add_base_load_j_load18 to <8 x i64>
  %mask.i = bitcast <8 x i32> %"oldMask&test182" to <8 x float>
  %offsets_1.i = shufflevector <8 x i64> %offset_cast, <8 x i64> poison, <4 x i32> <i32 0, i32 1, i32 2, i32 3>
  %offsets_2.i = shufflevector <8 x i64> %offset_cast, <8 x i64> poison, <4 x i32> <i32 4, i32 5, i32 6, i32 7>
  %mask_1.i = shufflevector <8 x float> %mask.i, <8 x float> poison, <4 x i32> <i32 0, i32 1, i32 2, i32 3>
  %mask_2.i = shufflevector <8 x float> %mask.i, <8 x float> poison, <4 x i32> <i32 4, i32 5, i32 6, i32 7>
  %v1_1.i = tail call <4 x float> @llvm.x86.avx2.gather.q.ps.256(<4 x float> undef, ptr %a, <4 x i64> %offsets_1.i, <4 x float> %mask_1.i, i8 1)
  %v2_1.i = tail call <4 x float> @llvm.x86.avx2.gather.q.ps.256(<4 x float> undef, ptr %a, <4 x i64> %offsets_2.i, <4 x float> %mask_2.i, i8 1)
  %v.i = shufflevector <4 x float> %v1_1.i, <4 x float> %v2_1.i, <8 x i32> <i32 0, i32 1, i32 2, i32 3, i32 4, i32 5, i32 6, i32 7>
  %less_v_load_best_load = fcmp olt <8 x float> %v.i, %best.0180
  %mask_as_float.i.i = select <8 x i1> %less_v_load_best_load, <8 x float> %mask.i, <8 x float> zeroinitializer
  %blend.i.i = call <8 x float> @llvm.x86.avx.blendv.ps.256(<8 x float> %best.0180, <8 x float> %v.i, <8 x float> %mask_as_float.i.i)
  %j_load29_plus1 = add nuw nsw <8 x i32> %j.0181, splat (i32 1)
  %less_j_load_ = icmp samesign ult <8 x i32> %j.0181, splat (i32 99)
  %"oldMask&test" = select <8 x i1> %less_j_load_, <8 x i32> %"oldMask&test182", <8 x i32> zeroinitializer
  %5 = icmp slt <8 x i32> %"oldMask&test", zeroinitializer
  %6 = bitcast <8 x i1> %5 to i8
  %cmp.i.not = icmp eq i8 %6, 0
  br i1 %cmp.i.not, label %for_exit, label %for_loop, !llvm.loop !17

for_exit:                                         ; preds = %for_loop
  %7 = shl nsw i64 %indvars.iv, 2
  %ptr = getelementptr i8, ptr %result, i64 %7
  store <8 x float> %blend.i.i, ptr %ptr, align 4, !filename !11, !first_line !12, !first_column !13, !last_line !12, !last_column !14
  %indvars.iv.next = add nuw nsw i64 %indvars.iv, 8
  %before_aligned_end8 = icmp samesign ult i64 %indvars.iv.next, %0
  br i1 %before_aligned_end8, label %foreach_full_body, label %outer_not_in_extras.partial_inner_all_outer_crit_edge, !llvm.loop !16

for_loop55:                                       ; preds = %for_loop55.lr.ph, %for_loop55
  %"oldMask&test64188" = phi <8 x i32> [ %cmp49_to_boolvec, %for_loop55.lr.ph ], [ %"oldMask&test64", %for_loop55 ]
  %j61.0187 = phi <8 x i32> [ zeroinitializer, %for_loop55.lr.ph ], [ %j_load92_plus1, %for_loop55 ]
  %best51.0186 = phi <8 x float> [ splat (float 0x46293E5940000000), %for_loop55.lr.ph ], [ %blend.i.i175, %for_loop55 ]
  %add_base_load68_j_load69 = add nuw nsw <8 x i32> %j61.0187, %mul_i_load53_
  %mul__add_base_load68_j_load69 = shl nsw <8 x i32> %add_base_load68_j_load69, splat (i32 2)
  %mask.i171 = bitcast <8 x i32> %"oldMask&test64188" to <8 x float>
  %v_1.i = tail call <8 x float> @llvm.x86.avx2.gather.d.ps.256(<8 x float> undef, ptr readonly %a, <8 x i32> %mul__add_base_load68_j_load69, <8 x float> %mask.i171, i8 1)
  %less_v_load74_best_load75 = fcmp olt <8 x float> %v_1.i, %best51.0186
  %mask_as_float.i.i173 = select <8 x i1> %less_v_load74_best_load75, <8 x float> %mask.i171, <8 x float> zeroinitializer
  %blend.i.i175 = call <8 x float> @llvm.x86.avx.blendv.ps.256(<8 x float> %best51.0186, <8 x float> %v_1.i, <8 x float> %mask_as_float.i.i173)
  %j_load92_plus1 = add nuw nsw <8 x i32> %j61.0187, splat (i32 1)
  %less_j_load62_ = icmp samesign ult <8 x i32> %j61.0187, splat (i32 99)
  %"oldMask&test64" = select <8 x i1> %less_j_load62_, <8 x i32> %"oldMask&test64188", <8 x i32> zeroinitializer
  %8 = icmp slt <8 x i32> %"oldMask&test64", zeroinitializer
  %9 = bitcast <8 x i1> %8 to i8
  %cmp.i170.not = icmp eq i8 %9, 0
  br i1 %cmp.i170.not, label %for_exit57, label %for_loop55, !llvm.loop !18

for_exit57:                                       ; preds = %for_loop55, %partial_inner_only
  %best51.0.lcssa = phi <8 x float> [ splat (float 0x46293E5940000000), %partial_inner_only ], [ %blend.i.i175, %for_loop55 ]
  %mul__i_load96.elt0 = shl nsw i32 %counter.1.lcssa, 2
  %10 = zext nneg i32 %mul__i_load96.elt0 to i64
  %ptr169 = getelementptr i8, ptr %result, i64 %10
  call void @llvm.masked.store.v8f32.p0(<8 x float> %best51.0.lcssa, ptr %ptr169, i32 1, <8 x i1> %cmp49)
  br label %foreach_reset

foreach_reset:                                    ; preds = %for_exit57, %partial_inner_all_outer
  ret void

outer_not_in_extras.partial_inner_all_outer_crit_edge: ; preds = %for_exit
  %11 = trunc nuw nsw i64 %indvars.iv.next to i32
  br label %partial_inner_all_outer, !llvm.loop !16

partial_inner_all_outer:                          ; preds = %outer_not_in_extras.partial_inner_all_outer_crit_edge, %allocas
  %counter.1.lcssa = phi i32 [ %11, %outer_not_in_extras.partial_inner_all_outer_crit_edge ], [ 0, %allocas ]
  %before_full_end = icmp slt i32 %counter.1.lcssa, %count
  br i1 %before_full_end, label %partial_inner_only, label %foreach_reset

partial_inner_only:                               ; preds = %partial_inner_all_outer
  %smear_counter_init44 = insertelement <8 x i32> poison, i32 %counter.1.lcssa, i64 0
  %smear_counter45 = shufflevector <8 x i32> %smear_counter_init44, <8 x i32> poison, <8 x i32> zeroinitializer
  %iter_val46 = or disjoint <8 x i32> %smear_counter45, <i32 0, i32 1, i32 2, i32 3, i32 4, i32 5, i32 6, i32 7>
  %smear_end_init47 = insertelement <8 x i32> poison, i32 %count, i64 0
  %smear_end48 = shufflevector <8 x i32> %smear_end_init47, <8 x i32> poison, <8 x i32> zeroinitializer
  %cmp49 = icmp slt <8 x i32> %iter_val46, %smear_end48
  %mul_i_load53_ = mul nuw nsw <8 x i32> %iter_val46, splat (i32 100)
  %12 = bitcast <8 x i1> %cmp49 to i8
  %cmp.i170.not185 = icmp eq i8 %12, 0
  br i1 %cmp.i170.not185, label %for_exit57, label %for_loop55.lr.ph

for_loop55.lr.ph:                                 ; preds = %partial_inner_only
  %cmp49_to_boolvec = sext <8 x i1> %cmp49 to <8 x i32>
  br label %for_loop55
}

; Function Attrs: nocallback nofree nosync nounwind willreturn memory(read)
declare <8 x float> @llvm.x86.avx2.gather.d.ps.256(<8 x float>, ptr, <8 x i32>, <8 x float>, i8 immarg) #1

; Function Attrs: nocallback nofree nosync nounwind willreturn memory(read)
declare <4 x float> @llvm.x86.avx2.gather.q.ps.256(<4 x float>, ptr, <4 x i64>, <4 x float>, i8 immarg) #1

; Function Attrs: nocallback nofree nosync nounwind willreturn memory(none)
declare <8 x float> @llvm.x86.avx.blendv.ps.256(<8 x float>, <8 x float>, <8 x float>) #2

; Function Attrs: nocallback nofree nosync nounwind willreturn memory(argmem: write)
declare void @llvm.masked.store.v8f32.p0(<8 x float>, ptr captures(none), i32 immarg, <8 x i1>) #3

attributes #0 = { nounwind uwtable }
attributes #1 = { nocallback nofree nosync nounwind willreturn memory(read) }
attributes #2 = { nocallback nofree nosync nounwind willreturn memory(none) }
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
!10 = distinct !{!10, !9}
!11 = !{!"Case3_SimpleReduce/SimpleReduce.ispc"}
!12 = !{i32 16}
!13 = !{i32 9}
!14 = !{i32 18}
!15 = distinct !{!15, !9}
!16 = distinct !{!16, !9}
!17 = distinct !{!17, !9}
!18 = distinct !{!18, !9}
