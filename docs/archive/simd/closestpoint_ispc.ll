; ModuleID = 'src/EntJoySample/NativeTranspiler_Generated/SharpNative_Job__global_namespace__GridSearch2D_ClosestPointJobPointer_Execute_Batch.ispc'
source_filename = "src/EntJoySample/NativeTranspiler_Generated/SharpNative_Job__global_namespace__GridSearch2D_ClosestPointJobPointer_Execute_Batch.ispc"
target datalayout = "e-m:w-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-f80:128-n8:16:32:64-S128"
target triple = "x86_64-pc-windows-msvc"

; Function Attrs: nounwind uwtable
define void @SharpNative_Job__global_namespace__GridSearch2D_ClosestPointJobPointer_Execute_Batch_false_impl___uniuniun_3C_s_5B_unfloat2_5D__3E_un_3C_unf_3E_un_3C_s_5B_unint2_5D__3E_un_3C_s_5B_unfloat2_5D__3E_uniun_3C_s_5B_unfloat2_5D__3E_uniun_3C_s_5B_unint2_5D__3E_uniun_3C_s_5B_unUnsafeList_Context_int2_5D__3E_un_3C_uni_3E_un_3C_unb_3E_un_3C_unf_3E_un_3C_uni_3E_uni(i32 %__startIndex, i32 %__count, ptr noalias readonly captures(none) %GridOrigin_ptr, ptr noalias readonly captures(none) %GridResolutionInv_ptr, ptr noalias readonly captures(none) %GridDimensions_ptr, ptr noalias %QueryPositions_ptr, i32 %QueryPositions_length, ptr noalias %SortedPositions_ptr, i32 %SortedPositions_length, ptr noalias %HashIndex_ptr, i32 %HashIndex_length, ptr noalias readonly captures(none) %CellStartEnd, ptr noalias readonly captures(none) %SortedLength_ptr, ptr noalias readnone captures(none) %IgnoreSelf_ptr, ptr noalias readnone captures(none) %SquaredEpsilonSelf_ptr, ptr noalias captures(none) %Results_ptr, i32 %Results_length, <8 x i32> %__mask) local_unnamed_addr #0 {
allocas:
  %0 = icmp sgt <8 x i32> %__mask, splat (i32 -1)
  %1 = bitcast <8 x i1> %0 to i8
  %cmp.i = icmp eq i8 %1, 0
  %GridOrigin_ptr_load_load.unpack = load float, ptr %GridOrigin_ptr, align 4
  %GridOrigin_ptr_load_load.elt3385 = getelementptr inbounds nuw i8, ptr %GridOrigin_ptr, i64 4
  %GridOrigin_ptr_load_load.unpack3386 = load float, ptr %GridOrigin_ptr_load_load.elt3385, align 4
  %GridResolutionInv_ptr_load_load = load float, ptr %GridResolutionInv_ptr, align 4
  %GridDimensions_ptr_load_load.unpack = load i32, ptr %GridDimensions_ptr, align 4
  %GridDimensions_ptr_load_load.elt3388 = getelementptr inbounds nuw i8, ptr %GridDimensions_ptr, i64 4
  %GridDimensions_ptr_load_load.unpack3389 = load i32, ptr %GridDimensions_ptr_load_load.elt3388, align 4
  %SortedLength_ptr_load_load = load i32, ptr %SortedLength_ptr, align 4
  %add___startIndex_load37___count_load = add nsw i32 %__count, %__startIndex
  %ret.i.i = tail call i32 @llvm.smin.i32(i32 %QueryPositions_length, i32 %add___startIndex_load37___count_load)
  %nitems = sub nsw i32 %ret.i.i, %__startIndex
  %nextras = srem i32 %nitems, 8
  %aligned_end = sub nsw i32 %ret.i.i, %nextras
  %before_aligned_end484617 = icmp slt i32 %__startIndex, %aligned_end
  br i1 %cmp.i, label %outer_not_in_extras.preheader, label %outer_not_in_extras1226.preheader

outer_not_in_extras1226.preheader:                ; preds = %allocas
  br i1 %before_aligned_end484617, label %foreach_full_body1200.lr.ph, label %partial_inner_all_outer1244, !llvm.loop !8

foreach_full_body1200.lr.ph:                      ; preds = %outer_not_in_extras1226.preheader
  %get_element1283_broadcast = insertelement <8 x float> poison, float %GridOrigin_ptr_load_load.unpack, i64 0
  %get_element1283_broadcast1284 = shufflevector <8 x float> %get_element1283_broadcast, <8 x float> poison, <8 x i32> zeroinitializer
  %get_element1286_broadcast = insertelement <8 x float> poison, float %GridOrigin_ptr_load_load.unpack3386, i64 0
  %get_element1286_broadcast1287 = shufflevector <8 x float> %get_element1286_broadcast, <8 x float> poison, <8 x i32> zeroinitializer
  %GridResolutionInv_load1292_broadcast = insertelement <8 x float> poison, float %GridResolutionInv_ptr_load_load, i64 0
  %GridResolutionInv_load1292_broadcast1293 = shufflevector <8 x float> %GridResolutionInv_load1292_broadcast, <8 x float> poison, <8 x i32> zeroinitializer
  %get_element1308_broadcast = insertelement <8 x i32> poison, i32 %GridDimensions_ptr_load_load.unpack, i64 0
  %get_element1308_broadcast1309 = shufflevector <8 x i32> %get_element1308_broadcast, <8 x i32> poison, <8 x i32> zeroinitializer
  %get_element1311_broadcast = insertelement <8 x i32> poison, i32 %GridDimensions_ptr_load_load.unpack3389, i64 0
  %get_element1311_broadcast1312 = shufflevector <8 x i32> %get_element1311_broadcast, <8 x i32> poison, <8 x i32> zeroinitializer
  %sub_a14_x_b_load.i4031 = add nsw <8 x i32> %get_element1308_broadcast1309, splat (i32 -1)
  %sub_a17_y_b_load9.i4032 = add nsw <8 x i32> %get_element1311_broadcast1312, splat (i32 -1)
  %SortedLength_load1676_broadcast = insertelement <8 x i32> poison, i32 %SortedLength_ptr_load_load, i64 0
  %SortedLength_load1676_broadcast1677 = shufflevector <8 x i32> %SortedLength_load1676_broadcast, <8 x i32> poison, <8 x i32> zeroinitializer
  %less_i_load1675_SortedLength_load1676_broadcast16774537 = icmp sgt <8 x i32> %SortedLength_load1676_broadcast1677, zeroinitializer
  %invariant.gep = getelementptr i8, ptr %SortedPositions_ptr, i64 4
  %2 = sext i32 %__startIndex to i64
  %3 = sext i32 %aligned_end to i64
  br label %foreach_full_body1200, !llvm.loop !8

outer_not_in_extras.preheader:                    ; preds = %allocas
  br i1 %before_aligned_end484617, label %foreach_full_body.lr.ph, label %partial_inner_all_outer, !llvm.loop !10

foreach_full_body.lr.ph:                          ; preds = %outer_not_in_extras.preheader
  %get_element_broadcast = insertelement <8 x float> poison, float %GridOrigin_ptr_load_load.unpack, i64 0
  %get_element_broadcast75 = shufflevector <8 x float> %get_element_broadcast, <8 x float> poison, <8 x i32> zeroinitializer
  %get_element76_broadcast = insertelement <8 x float> poison, float %GridOrigin_ptr_load_load.unpack3386, i64 0
  %get_element76_broadcast77 = shufflevector <8 x float> %get_element76_broadcast, <8 x float> poison, <8 x i32> zeroinitializer
  %GridResolutionInv_load_broadcast = insertelement <8 x float> poison, float %GridResolutionInv_ptr_load_load, i64 0
  %GridResolutionInv_load_broadcast82 = shufflevector <8 x float> %GridResolutionInv_load_broadcast, <8 x float> poison, <8 x i32> zeroinitializer
  %get_element95_broadcast = insertelement <8 x i32> poison, i32 %GridDimensions_ptr_load_load.unpack, i64 0
  %get_element95_broadcast96 = shufflevector <8 x i32> %get_element95_broadcast, <8 x i32> poison, <8 x i32> zeroinitializer
  %get_element98_broadcast = insertelement <8 x i32> poison, i32 %GridDimensions_ptr_load_load.unpack3389, i64 0
  %get_element98_broadcast99 = shufflevector <8 x i32> %get_element98_broadcast, <8 x i32> poison, <8 x i32> zeroinitializer
  %sub_a14_x_b_load.i = add nsw <8 x i32> %get_element95_broadcast96, splat (i32 -1)
  %sub_a17_y_b_load9.i = add nsw <8 x i32> %get_element98_broadcast99, splat (i32 -1)
  %SortedLength_load_broadcast = insertelement <8 x i32> poison, i32 %SortedLength_ptr_load_load, i64 0
  %SortedLength_load_broadcast404 = shufflevector <8 x i32> %SortedLength_load_broadcast, <8 x i32> poison, <8 x i32> zeroinitializer
  %less_i_load403_SortedLength_load_broadcast4044605 = icmp sgt <8 x i32> %SortedLength_load_broadcast404, zeroinitializer
  %invariant.gep4615 = getelementptr i8, ptr %SortedPositions_ptr, i64 4
  %4 = sext i32 %__startIndex to i64
  %5 = sext i32 %aligned_end to i64
  br label %foreach_full_body, !llvm.loop !10

foreach_full_body:                                ; preds = %foreach_full_body.lr.ph, %if_done
  %indvars.iv4678 = phi i64 [ %4, %foreach_full_body.lr.ph ], [ %indvars.iv.next4679, %if_done ]
  %6 = trunc nsw i64 %indvars.iv4678 to i32
  %smear_counter_init52 = insertelement <8 x i32> poison, i32 %6, i64 0
  %smear_counter53 = shufflevector <8 x i32> %smear_counter_init52, <8 x i32> poison, <8 x i32> zeroinitializer
  %iter_val54 = add nsw <8 x i32> %smear_counter53, <i32 0, i32 1, i32 2, i32 3, i32 4, i32 5, i32 6, i32 7>
  %7 = shl nsw i64 %indvars.iv4678, 2
  %ptr = getelementptr i8, ptr %Results_ptr, i64 %7
  store <8 x i32> splat (i32 -1), ptr %ptr, align 4, !filename !11, !first_line !12, !first_column !13, !last_line !12, !last_column !14
  %mul__index_load61 = shl nsw <8 x i32> %iter_val54, splat (i32 3)
  %v_1.i = tail call <8 x float> @llvm.x86.avx2.gather.d.ps.256(<8 x float> undef, ptr readonly %QueryPositions_ptr, <8 x i32> %mul__index_load61, <8 x float> splat (float 0xFFFFFFFFE0000000), i8 1)
  %8 = or disjoint <8 x i32> %mul__index_load61, splat (i32 4)
  %v_1.i4167 = tail call <8 x float> @llvm.x86.avx2.gather.d.ps.256(<8 x float> undef, ptr readonly %QueryPositions_ptr, <8 x i32> %8, <8 x float> splat (float 0xFFFFFFFFE0000000), i8 1)
  %sub_a14_x_b26_x.i = fsub <8 x float> %v_1.i, %get_element_broadcast75
  %sub_a19_y_b211_y.i = fsub <8 x float> %v_1.i4167, %get_element76_broadcast77
  %mul_v14_x_s_load.i = fmul <8 x float> %GridResolutionInv_load_broadcast82, %sub_a14_x_b26_x.i
  %mul_v17_y_s_load9.i = fmul <8 x float> %GridResolutionInv_load_broadcast82, %sub_a19_y_b211_y.i
  %call.i.i.i = tail call <8 x float> @llvm.x86.avx.round.ps.256(<8 x float> %mul_v14_x_s_load.i, i32 9)
  %call.i.i3.i = tail call <8 x float> @llvm.x86.avx.round.ps.256(<8 x float> %mul_v17_y_s_load9.i, i32 9)
  %v12_x_to_int32.i = fptosi <8 x float> %call.i.i.i to <8 x i32>
  %v14_y_to_int32.i = fptosi <8 x float> %call.i.i3.i to <8 x i32>
  %9 = call <8 x i32> @llvm.smax.v8i32(<8 x i32> %v12_x_to_int32.i, <8 x i32> zeroinitializer)
  %10 = call <8 x i32> @llvm.smax.v8i32(<8 x i32> %v14_y_to_int32.i, <8 x i32> zeroinitializer)
  %blend.i4175.v = call <8 x i32> @llvm.smin.v8i32(<8 x i32> %9, <8 x i32> %sub_a14_x_b_load.i)
  %blend.i4179.v = call <8 x i32> @llvm.smin.v8i32(<8 x i32> %10, <8 x i32> %sub_a17_y_b_load9.i)
  br label %for_loop

for_loop:                                         ; preds = %foreach_full_body, %for_step
  %11 = phi i8 [ -1, %foreach_full_body ], [ %15, %for_step ]
  %"oldMask&test4604" = phi <8 x i32> [ splat (i32 -1), %foreach_full_body ], [ %"oldMask&test", %for_step ]
  %dx.04603 = phi <8 x i32> [ splat (i32 -1), %foreach_full_body ], [ %dx_load364_plus1, %for_step ]
  %bestIdx.04602 = phi <8 x i32> [ splat (i32 -1), %foreach_full_body ], [ %bestIdx.1, %for_step ]
  %bestDistSq.04601 = phi <8 x float> [ splat (float 0x47EFFFFFE0000000), %foreach_full_body ], [ %bestDistSq.1, %for_step ]
  %add_cell115_x_dx_load117 = add nsw <8 x i32> %dx.04603, %blend.i4175.v
  %greaterequal_nx_load_GridDimensions118_x_broadcast120.not = icmp ult <8 x i32> %add_cell115_x_dx_load117, %get_element95_broadcast96
  %"oldMask&test122" = select <8 x i1> %greaterequal_nx_load_GridDimensions118_x_broadcast120.not, <8 x i32> zeroinitializer, <8 x i32> %"oldMask&test4604"
  %12 = icmp slt <8 x i32> %"oldMask&test122", zeroinitializer
  %13 = bitcast <8 x i1> %12 to i8
  %"equal_finished&func_internal_mask&function_mask114" = icmp eq i8 %11, %13
  br i1 %"equal_finished&func_internal_mask&function_mask114", label %for_step, label %not_all_continued_or_breaked

for_step:                                         ; preds = %not_all_continued_or_breaked, %for_step139, %for_loop
  %bestDistSq.1 = phi <8 x float> [ %bestDistSq.04601, %for_loop ], [ %bestDistSq.04601, %not_all_continued_or_breaked ], [ %bestDistSq.3, %for_step139 ]
  %bestIdx.1 = phi <8 x i32> [ %bestIdx.04602, %for_loop ], [ %bestIdx.04602, %not_all_continued_or_breaked ], [ %bestIdx.3, %for_step139 ]
  %internal_mask_memory.2 = phi <8 x i32> [ zeroinitializer, %for_loop ], [ %new_mask134, %not_all_continued_or_breaked ], [ %new_mask134, %for_step139 ]
  %"mask|continue_mask361" = or <8 x i32> %internal_mask_memory.2, %"oldMask&test122"
  %dx_load364_plus1 = add nsw <8 x i32> %dx.04603, splat (i32 1)
  %lessequal_dx_load_.inv = icmp sgt <8 x i32> %dx.04603, zeroinitializer
  %"oldMask&test" = select <8 x i1> %lessequal_dx_load_.inv, <8 x i32> zeroinitializer, <8 x i32> %"mask|continue_mask361"
  %14 = icmp slt <8 x i32> %"oldMask&test", zeroinitializer
  %15 = bitcast <8 x i1> %14 to i8
  %cmp.i3837.not = icmp eq i8 %15, 0
  br i1 %cmp.i3837.not, label %for_exit, label %for_loop, !llvm.loop !15

for_exit:                                         ; preds = %for_step
  %notequal_bestIdx_load_ = icmp ne <8 x i32> %bestIdx.1, splat (i32 -1)
  %notequal_bestIdx_load__to_boolvec = sext <8 x i1> %notequal_bestIdx_load_ to <8 x i32>
  %16 = bitcast <8 x i1> %notequal_bestIdx_load_ to i8
  %cmp.i3839.not = icmp eq i8 %16, 0
  br i1 %cmp.i3839.not, label %safe_if_after_true, label %safe_if_run_true

for_loop650:                                      ; preds = %partial_inner_only, %for_step651
  %17 = phi i8 [ %21, %for_step651 ], [ %61, %partial_inner_only ]
  %"oldMask&test6594640" = phi <8 x i32> [ %"oldMask&test659", %for_step651 ], [ %cmp576_to_boolvec, %partial_inner_only ]
  %dx656.04639 = phi <8 x i32> [ %dx_load955_plus1, %for_step651 ], [ splat (i32 -1), %partial_inner_only ]
  %bestIdx648.04638 = phi <8 x i32> [ %bestIdx648.1, %for_step651 ], [ splat (i32 -1), %partial_inner_only ]
  %bestDistSq647.04637 = phi <8 x float> [ %bestDistSq647.1, %for_step651 ], [ splat (float 0x47EFFFFFE0000000), %partial_inner_only ]
  %add_cell607667_x_dx_load669 = add nsw <8 x i32> %dx656.04639, %blend.i16.i.v
  %greaterequal_nx_load670_GridDimensions671_x_broadcast673.not = icmp ult <8 x i32> %add_cell607667_x_dx_load669, %get_element635_broadcast636
  %"oldMask&test675" = select <8 x i1> %greaterequal_nx_load670_GridDimensions671_x_broadcast673.not, <8 x i32> zeroinitializer, <8 x i32> %"oldMask&test6594640"
  %18 = icmp slt <8 x i32> %"oldMask&test675", zeroinitializer
  %19 = bitcast <8 x i1> %18 to i8
  %"equal_finished&func688_internal_mask&function_mask665" = icmp eq i8 %17, %19
  br i1 %"equal_finished&func688_internal_mask&function_mask665", label %for_step651, label %not_all_continued_or_breaked690

for_step651:                                      ; preds = %not_all_continued_or_breaked690, %for_step706, %for_loop650
  %bestDistSq647.1 = phi <8 x float> [ %bestDistSq647.04637, %for_loop650 ], [ %bestDistSq647.04637, %not_all_continued_or_breaked690 ], [ %bestDistSq647.3, %for_step706 ]
  %bestIdx648.1 = phi <8 x i32> [ %bestIdx648.04638, %for_loop650 ], [ %bestIdx648.04638, %not_all_continued_or_breaked690 ], [ %bestIdx648.3, %for_step706 ]
  %internal_mask_memory.10 = phi <8 x i32> [ zeroinitializer, %for_loop650 ], [ %new_mask701, %not_all_continued_or_breaked690 ], [ %new_mask701, %for_step706 ]
  %"mask|continue_mask952" = or <8 x i32> %internal_mask_memory.10, %"oldMask&test675"
  %dx_load955_plus1 = add nsw <8 x i32> %dx656.04639, splat (i32 1)
  %lessequal_dx_load657_.inv = icmp sgt <8 x i32> %dx656.04639, zeroinitializer
  %"oldMask&test659" = select <8 x i1> %lessequal_dx_load657_.inv, <8 x i32> zeroinitializer, <8 x i32> %"mask|continue_mask952"
  %20 = icmp slt <8 x i32> %"oldMask&test659", zeroinitializer
  %21 = bitcast <8 x i1> %20 to i8
  %cmp.i3840.not = icmp eq i8 %21, 0
  br i1 %cmp.i3840.not, label %for_exit652, label %for_loop650, !llvm.loop !16

for_exit652:                                      ; preds = %for_step651, %partial_inner_only
  %bestDistSq647.0.lcssa = phi <8 x float> [ splat (float 0x47EFFFFFE0000000), %partial_inner_only ], [ %bestDistSq647.1, %for_step651 ]
  %bestIdx648.0.lcssa = phi <8 x i32> [ splat (i32 -1), %partial_inner_only ], [ %bestIdx648.1, %for_step651 ]
  %notequal_bestIdx_load961_ = icmp ne <8 x i32> %bestIdx648.0.lcssa, splat (i32 -1)
  %22 = select <8 x i1> %notequal_bestIdx_load961_, <8 x i1> %cmp576, <8 x i1> zeroinitializer
  %23 = bitcast <8 x i1> %22 to i8
  %cmp.i3843.not = icmp eq i8 %23, 0
  br i1 %cmp.i3843.not, label %safe_if_after_true964, label %safe_if_run_true965

common.ret:                                       ; preds = %for_exit2275, %safe_if_run_true2390, %safe_if_after_true2242, %for_exit997, %safe_if_run_true1112, %safe_if_after_true964, %partial_inner_all_outer1244, %partial_inner_all_outer
  ret void

outer_not_in_extras.partial_inner_all_outer_crit_edge: ; preds = %if_done
  %24 = trunc nsw i64 %indvars.iv.next4679 to i32
  br label %partial_inner_all_outer, !llvm.loop !10

partial_inner_all_outer:                          ; preds = %outer_not_in_extras.partial_inner_all_outer_crit_edge, %outer_not_in_extras.preheader
  %counter.1.lcssa = phi i32 [ %24, %outer_not_in_extras.partial_inner_all_outer_crit_edge ], [ %__startIndex, %outer_not_in_extras.preheader ]
  %before_full_end = icmp slt i32 %counter.1.lcssa, %ret.i.i
  br i1 %before_full_end, label %partial_inner_only, label %common.ret

not_all_continued_or_breaked:                     ; preds = %for_loop
  %new_mask134 = xor <8 x i32> %"oldMask&test122", %"oldMask&test4604"
  %25 = icmp slt <8 x i32> %new_mask134, zeroinitializer
  %26 = bitcast <8 x i1> %25 to i8
  %cmp.i3844.not4594 = icmp eq i8 %26, 0
  br i1 %cmp.i3844.not4594, label %for_step, label %for_loop138

for_loop138:                                      ; preds = %not_all_continued_or_breaked, %for_step139
  %27 = phi i8 [ %32, %for_step139 ], [ %26, %not_all_continued_or_breaked ]
  %"oldMask&test1454598" = phi <8 x i32> [ %"oldMask&test145", %for_step139 ], [ %new_mask134, %not_all_continued_or_breaked ]
  %dy.04597 = phi <8 x i32> [ %dy_load353_plus1, %for_step139 ], [ splat (i32 -1), %not_all_continued_or_breaked ]
  %bestIdx.24596 = phi <8 x i32> [ %bestIdx.3, %for_step139 ], [ %bestIdx.04602, %not_all_continued_or_breaked ]
  %bestDistSq.24595 = phi <8 x float> [ %bestDistSq.3, %for_step139 ], [ %bestDistSq.04601, %not_all_continued_or_breaked ]
  %add_cell152_y_dy_load154 = add nsw <8 x i32> %dy.04597, %blend.i4179.v
  %greaterequal_ny_load_GridDimensions155_y_broadcast157.not = icmp ult <8 x i32> %add_cell152_y_dy_load154, %get_element98_broadcast99
  %"oldMask&test159" = select <8 x i1> %greaterequal_ny_load_GridDimensions155_y_broadcast157.not, <8 x i32> zeroinitializer, <8 x i32> %"oldMask&test1454598"
  %28 = icmp slt <8 x i32> %"oldMask&test159", zeroinitializer
  %29 = bitcast <8 x i1> %28 to i8
  %"equal_finished&func172_internal_mask&function_mask151" = icmp eq i8 %27, %29
  br i1 %"equal_finished&func172_internal_mask&function_mask151", label %for_step139, label %not_all_continued_or_breaked174

for_test247.for_step139.loopexit_crit_edge:       ; preds = %for_loop248
  %30 = bitcast <8 x float> %blend.i4188 to <8 x i32>
  br label %for_step139

for_step139:                                      ; preds = %not_all_continued_or_breaked233, %for_test247.for_step139.loopexit_crit_edge, %not_all_continued_or_breaked174, %for_loop138
  %bestDistSq.3 = phi <8 x float> [ %bestDistSq.24595, %for_loop138 ], [ %bestDistSq.24595, %not_all_continued_or_breaked174 ], [ %blend.i.i, %for_test247.for_step139.loopexit_crit_edge ], [ %bestDistSq.24595, %not_all_continued_or_breaked233 ]
  %bestIdx.3 = phi <8 x i32> [ %bestIdx.24596, %for_loop138 ], [ %bestIdx.24596, %not_all_continued_or_breaked174 ], [ %30, %for_test247.for_step139.loopexit_crit_edge ], [ %bestIdx.24596, %not_all_continued_or_breaked233 ]
  %continue_lanes_memory142.1 = phi <8 x i32> [ %"oldMask&test159", %for_loop138 ], [ %"mask|continueMask222", %not_all_continued_or_breaked174 ], [ %"mask|continueMask222", %for_test247.for_step139.loopexit_crit_edge ], [ %"mask|continueMask222", %not_all_continued_or_breaked233 ]
  %internal_mask_memory.4 = phi <8 x i32> [ zeroinitializer, %for_loop138 ], [ zeroinitializer, %not_all_continued_or_breaked174 ], [ %new_mask244, %for_test247.for_step139.loopexit_crit_edge ], [ %new_mask244, %not_all_continued_or_breaked233 ]
  %"mask|continue_mask350" = or <8 x i32> %internal_mask_memory.4, %continue_lanes_memory142.1
  %dy_load353_plus1 = add nsw <8 x i32> %dy.04597, splat (i32 1)
  %lessequal_dy_load_.inv = icmp sgt <8 x i32> %dy.04597, zeroinitializer
  %"oldMask&test145" = select <8 x i1> %lessequal_dy_load_.inv, <8 x i32> zeroinitializer, <8 x i32> %"mask|continue_mask350"
  %31 = icmp slt <8 x i32> %"oldMask&test145", zeroinitializer
  %32 = bitcast <8 x i1> %31 to i8
  %cmp.i3844.not = icmp eq i8 %32, 0
  br i1 %cmp.i3844.not, label %for_step, label %for_loop138, !llvm.loop !17

not_all_continued_or_breaked174:                  ; preds = %for_loop138
  %new_mask185 = xor <8 x i32> %"oldMask&test159", %"oldMask&test1454598"
  %mul_ny_load188_GridDimensions189_x_broadcast191 = mul nsw <8 x i32> %add_cell152_y_dy_load154, %get_element95_broadcast96
  %add_mul_ny_load188_GridDimensions189_x_broadcast191_nx_load192 = add nsw <8 x i32> %mul_ny_load188_GridDimensions189_x_broadcast191, %add_cell115_x_dx_load117
  %CellStartEnd_load193__data = load ptr, ptr %CellStartEnd, align 8
  %mul__cellHash_load = shl nsw <8 x i32> %add_mul_ny_load188_GridDimensions189_x_broadcast191_nx_load192, splat (i32 3)
  %v_1.i4180 = tail call <8 x i32> @llvm.x86.avx2.gather.d.d.256(<8 x i32> undef, ptr readonly %CellStartEnd_load193__data, <8 x i32> %mul__cellHash_load, <8 x i32> %new_mask185, i8 1)
  %33 = or disjoint <8 x i32> %mul__cellHash_load, splat (i32 4)
  %v_1.i4181 = tail call <8 x i32> @llvm.x86.avx2.gather.d.d.256(<8 x i32> undef, ptr readonly %CellStartEnd_load193__data, <8 x i32> %33, <8 x i32> %new_mask185, i8 1)
  %isneg3393 = icmp slt <8 x i32> %v_1.i4180, zeroinitializer
  %"oldMask&test217" = select <8 x i1> %isneg3393, <8 x i32> %new_mask185, <8 x i32> zeroinitializer
  %"mask|continueMask222" = or <8 x i32> %"oldMask&test217", %"oldMask&test159"
  %34 = icmp slt <8 x i32> %"mask|continueMask222", zeroinitializer
  %35 = bitcast <8 x i1> %34 to i8
  %"equal_finished&func230_internal_mask&function_mask151" = icmp eq i8 %27, %35
  br i1 %"equal_finished&func230_internal_mask&function_mask151", label %for_step139, label %not_all_continued_or_breaked233

not_all_continued_or_breaked233:                  ; preds = %not_all_continued_or_breaked174
  %new_mask244 = xor <8 x i32> %"mask|continueMask222", %"oldMask&test1454598"
  %less_i_load_end_load4585 = icmp slt <8 x i32> %v_1.i4180, %v_1.i4181
  %"oldMask&test2564586" = select <8 x i1> %less_i_load_end_load4585, <8 x i32> %new_mask244, <8 x i32> zeroinitializer
  %36 = icmp slt <8 x i32> %"oldMask&test2564586", zeroinitializer
  %37 = bitcast <8 x i1> %36 to i8
  %cmp.i3848.not4587 = icmp eq i8 %37, 0
  br i1 %cmp.i3848.not4587, label %for_step139, label %for_loop248.lr.ph

for_loop248.lr.ph:                                ; preds = %not_all_continued_or_breaked233
  %38 = bitcast <8 x i32> %bestIdx.24596 to <8 x float>
  br label %for_loop248

for_loop248:                                      ; preds = %for_loop248.lr.ph, %for_loop248
  %"oldMask&test2564591" = phi <8 x i32> [ %"oldMask&test2564586", %for_loop248.lr.ph ], [ %"oldMask&test256", %for_loop248 ]
  %i.04590 = phi <8 x i32> [ %v_1.i4180, %for_loop248.lr.ph ], [ %i_load342_plus1, %for_loop248 ]
  %bestIdx.44589 = phi <8 x float> [ %38, %for_loop248.lr.ph ], [ %blend.i4188, %for_loop248 ]
  %bestDistSq.44588 = phi <8 x float> [ %bestDistSq.24595, %for_loop248.lr.ph ], [ %blend.i.i, %for_loop248 ]
  %mul__i_load263 = shl nsw <8 x i32> %i.04590, splat (i32 3)
  %mask.i = bitcast <8 x i32> %"oldMask&test2564591" to <8 x float>
  %v_1.i4182 = tail call <8 x float> @llvm.x86.avx2.gather.d.ps.256(<8 x float> undef, ptr readonly %SortedPositions_ptr, <8 x i32> %mul__i_load263, <8 x float> %mask.i, i8 1)
  %39 = or disjoint <8 x i32> %mul__i_load263, splat (i32 4)
  %v_1.i4184 = tail call <8 x float> @llvm.x86.avx2.gather.d.ps.256(<8 x float> undef, ptr readonly %SortedPositions_ptr, <8 x i32> %39, <8 x float> %mask.i, i8 1)
  %sub_a14_x_b26_x.i.i = fsub <8 x float> %v_1.i4182, %v_1.i
  %mul_a13_x_b25_x.i.i.i = fmul <8 x float> %sub_a14_x_b26_x.i.i, %sub_a14_x_b26_x.i.i
  %sub_a19_y_b211_y.i.i = fsub <8 x float> %v_1.i4184, %v_1.i4167
  %mul_a17_y_b29_y.i.i.i = fmul <8 x float> %sub_a19_y_b211_y.i.i, %sub_a19_y_b211_y.i.i
  %add_mul_a13_x_b25_x_mul_a17_y_b29_y.i.i.i = fadd <8 x float> %mul_a13_x_b25_x.i.i.i, %mul_a17_y_b29_y.i.i.i
  %less_distSq_load316_bestDistSq_load = fcmp olt <8 x float> %add_mul_a13_x_b25_x_mul_a17_y_b29_y.i.i.i, %bestDistSq.44588
  %40 = bitcast <8 x i32> %"oldMask&test2564591" to <8 x float>
  %mask_as_float.i.i = select <8 x i1> %less_distSq_load316_bestDistSq_load, <8 x float> %40, <8 x float> zeroinitializer
  %blend.i.i = call <8 x float> @llvm.x86.avx.blendv.ps.256(<8 x float> %bestDistSq.44588, <8 x float> %add_mul_a13_x_b25_x_mul_a17_y_b29_y.i.i.i, <8 x float> %mask_as_float.i.i)
  %newAsFloat.i4187 = bitcast <8 x i32> %i.04590 to <8 x float>
  %blend.i4188 = call <8 x float> @llvm.x86.avx.blendv.ps.256(<8 x float> %bestIdx.44589, <8 x float> %newAsFloat.i4187, <8 x float> %mask_as_float.i.i)
  %i_load342_plus1 = add nsw <8 x i32> %i.04590, splat (i32 1)
  %less_i_load_end_load = icmp slt <8 x i32> %i_load342_plus1, %v_1.i4181
  %"oldMask&test256" = select <8 x i1> %less_i_load_end_load, <8 x i32> %"oldMask&test2564591", <8 x i32> zeroinitializer
  %41 = icmp slt <8 x i32> %"oldMask&test256", zeroinitializer
  %42 = bitcast <8 x i1> %41 to i8
  %cmp.i3848.not = icmp eq i8 %42, 0
  br i1 %cmp.i3848.not, label %for_test247.for_step139.loopexit_crit_edge, label %for_loop248, !llvm.loop !18

if_done:                                          ; preds = %for_exit398, %safe_if_run_true512, %safe_if_after_true
  %indvars.iv.next4679 = add nsw i64 %indvars.iv4678, 8
  %before_aligned_end48 = icmp slt i64 %indvars.iv.next4679, %5
  br i1 %before_aligned_end48, label %foreach_full_body, label %outer_not_in_extras.partial_inner_all_outer_crit_edge, !llvm.loop !10

safe_if_after_true:                               ; preds = %safe_if_run_true, %for_exit
  %"~test" = xor <8 x i32> %notequal_bestIdx_load__to_boolvec, splat (i32 -1)
  %43 = xor <8 x i1> %notequal_bestIdx_load_, splat (i1 true)
  %44 = bitcast <8 x i1> %43 to i8
  %cmp.i3850.not = icmp eq i8 %44, 0
  br i1 %cmp.i3850.not, label %if_done, label %for_test395.preheader

for_test395.preheader:                            ; preds = %safe_if_after_true
  %"oldMask&test4064606" = select <8 x i1> %less_i_load403_SortedLength_load_broadcast4044605, <8 x i32> %"~test", <8 x i32> zeroinitializer
  %45 = icmp slt <8 x i32> %"oldMask&test4064606", zeroinitializer
  %46 = bitcast <8 x i1> %45 to i8
  %cmp.i3851.not4607 = icmp eq i8 %46, 0
  br i1 %cmp.i3851.not4607, label %for_exit398, label %not_all_continued_or_breaked460.lr.ph

not_all_continued_or_breaked460.lr.ph:            ; preds = %for_test395.preheader
  %47 = bitcast <8 x i32> %bestIdx.1 to <8 x float>
  br label %not_all_continued_or_breaked460

safe_if_run_true:                                 ; preds = %for_exit
  %mul__bestIdx_load379 = shl nsw <8 x i32> %bestIdx.1, splat (i32 3)
  %48 = or disjoint <8 x i32> %mul__bestIdx_load379, splat (i32 4)
  %new_add3437 = sext <8 x i32> %48 to <8 x i64>
  %vecmask_1.i = shufflevector <8 x i32> %notequal_bestIdx_load__to_boolvec, <8 x i32> poison, <4 x i32> <i32 0, i32 1, i32 2, i32 3>
  %vecmask_2.i = shufflevector <8 x i32> %notequal_bestIdx_load__to_boolvec, <8 x i32> poison, <4 x i32> <i32 4, i32 5, i32 6, i32 7>
  %offsets_1.i = shufflevector <8 x i64> %new_add3437, <8 x i64> poison, <4 x i32> <i32 0, i32 1, i32 2, i32 3>
  %offsets_2.i = shufflevector <8 x i64> %new_add3437, <8 x i64> poison, <4 x i32> <i32 4, i32 5, i32 6, i32 7>
  %v1_1.i = tail call <4 x i32> @llvm.x86.avx2.gather.q.d.256(<4 x i32> undef, ptr %HashIndex_ptr, <4 x i64> %offsets_1.i, <4 x i32> %vecmask_1.i, i8 1)
  %v2_1.i = tail call <4 x i32> @llvm.x86.avx2.gather.q.d.256(<4 x i32> undef, ptr %HashIndex_ptr, <4 x i64> %offsets_2.i, <4 x i32> %vecmask_2.i, i8 1)
  %v.i = shufflevector <4 x i32> %v1_1.i, <4 x i32> %v2_1.i, <8 x i32> <i32 0, i32 1, i32 2, i32 3, i32 4, i32 5, i32 6, i32 7>
  %val.i = bitcast <8 x i32> %v.i to <8 x float>
  call void @llvm.masked.store.v8f32.p0(<8 x float> %val.i, ptr %ptr, i32 1, <8 x i1> %notequal_bestIdx_load_)
  br label %safe_if_after_true

for_test395.for_exit398_crit_edge:                ; preds = %not_all_continued_or_breaked460
  %49 = bitcast <8 x float> %blend.i4195 to <8 x i32>
  br label %for_exit398

for_exit398:                                      ; preds = %for_test395.for_exit398_crit_edge, %for_test395.preheader
  %bestIdx.5.lcssa = phi <8 x i32> [ %49, %for_test395.for_exit398_crit_edge ], [ %bestIdx.1, %for_test395.preheader ]
  %notequal_bestIdx_load508_ = icmp eq <8 x i32> %bestIdx.5.lcssa, splat (i32 -1)
  %"oldMask&test513" = select <8 x i1> %notequal_bestIdx_load508_, <8 x i32> zeroinitializer, <8 x i32> %"~test"
  %50 = icmp slt <8 x i32> %"oldMask&test513", zeroinitializer
  %51 = bitcast <8 x i1> %50 to i8
  %cmp.i3853.not = icmp eq i8 %51, 0
  br i1 %cmp.i3853.not, label %if_done, label %safe_if_run_true512

not_all_continued_or_breaked460:                  ; preds = %not_all_continued_or_breaked460.lr.ph, %not_all_continued_or_breaked460
  %indvars.iv4671 = phi i64 [ 0, %not_all_continued_or_breaked460.lr.ph ], [ %indvars.iv.next4672, %not_all_continued_or_breaked460 ]
  %"oldMask&test4064613" = phi <8 x i32> [ %"oldMask&test4064606", %not_all_continued_or_breaked460.lr.ph ], [ %"oldMask&test406", %not_all_continued_or_breaked460 ]
  %i402.04612 = phi <8 x i32> [ zeroinitializer, %not_all_continued_or_breaked460.lr.ph ], [ %i_load502_plus1, %not_all_continued_or_breaked460 ]
  %bestIdx.54609 = phi <8 x float> [ %47, %not_all_continued_or_breaked460.lr.ph ], [ %blend.i4195, %not_all_continued_or_breaked460 ]
  %bestDistSq.54608 = phi <8 x float> [ %bestDistSq.1, %not_all_continued_or_breaked460.lr.ph ], [ %blend.i.i4191, %not_all_continued_or_breaked460 ]
  %52 = shl nsw i64 %indvars.iv4671, 3
  %ptr3450 = getelementptr i8, ptr %SortedPositions_ptr, i64 %52, !filename !11, !first_line !19, !first_column !20, !last_line !19, !last_column !21
  %SortedPositions_ptr_load415_offset_load34493451 = load float, ptr %ptr3450, align 4
  %SortedPositions_ptr_load415_offset_load34493452 = insertelement <8 x float> poison, float %SortedPositions_ptr_load415_offset_load34493451, i64 0
  %SortedPositions_ptr_load415_offset_load34493453 = shufflevector <8 x float> %SortedPositions_ptr_load415_offset_load34493452, <8 x float> poison, <8 x i32> zeroinitializer, !filename !11, !first_line !19, !first_column !20, !last_line !19, !last_column !21
  %sub_a14_x_b26_x.i.i3963 = fsub <8 x float> %SortedPositions_ptr_load415_offset_load34493453, %v_1.i
  %mul_a13_x_b25_x.i.i.i3972 = fmul <8 x float> %sub_a14_x_b26_x.i.i3963, %sub_a14_x_b26_x.i.i3963
  %gep4616 = getelementptr i8, ptr %invariant.gep4615, i64 %52
  %SortedPositions_ptr_load415_offset_load43034593464 = load float, ptr %gep4616, align 4
  %SortedPositions_ptr_load415_offset_load43034593465 = insertelement <8 x float> poison, float %SortedPositions_ptr_load415_offset_load43034593464, i64 0
  %SortedPositions_ptr_load415_offset_load43034593466 = shufflevector <8 x float> %SortedPositions_ptr_load415_offset_load43034593465, <8 x float> poison, <8 x i32> zeroinitializer, !filename !11, !first_line !19, !first_column !20, !last_line !19, !last_column !21
  %sub_a19_y_b211_y.i.i3964 = fsub <8 x float> %SortedPositions_ptr_load415_offset_load43034593466, %v_1.i4167
  %mul_a17_y_b29_y.i.i.i3973 = fmul <8 x float> %sub_a19_y_b211_y.i.i3964, %sub_a19_y_b211_y.i.i3964
  %add_mul_a13_x_b25_x_mul_a17_y_b29_y.i.i.i3974 = fadd <8 x float> %mul_a13_x_b25_x.i.i.i3972, %mul_a17_y_b29_y.i.i.i3973
  %less_distSq_load474_bestDistSq_load475 = fcmp olt <8 x float> %add_mul_a13_x_b25_x_mul_a17_y_b29_y.i.i.i3974, %bestDistSq.54608
  %53 = bitcast <8 x i32> %"oldMask&test4064613" to <8 x float>
  %mask_as_float.i.i4189 = select <8 x i1> %less_distSq_load474_bestDistSq_load475, <8 x float> %53, <8 x float> zeroinitializer
  %blend.i.i4191 = call <8 x float> @llvm.x86.avx.blendv.ps.256(<8 x float> %bestDistSq.54608, <8 x float> %add_mul_a13_x_b25_x_mul_a17_y_b29_y.i.i.i3974, <8 x float> %mask_as_float.i.i4189)
  %newAsFloat.i4194 = bitcast <8 x i32> %i402.04612 to <8 x float>
  %blend.i4195 = call <8 x float> @llvm.x86.avx.blendv.ps.256(<8 x float> %bestIdx.54609, <8 x float> %newAsFloat.i4194, <8 x float> %mask_as_float.i.i4189)
  %indvars.iv.next4672 = add nuw nsw i64 %indvars.iv4671, 1
  %i_load502_plus1 = add nuw nsw <8 x i32> %i402.04612, splat (i32 1)
  %less_i_load403_SortedLength_load_broadcast404 = icmp slt <8 x i32> %i_load502_plus1, %SortedLength_load_broadcast404
  %"oldMask&test406" = select <8 x i1> %less_i_load403_SortedLength_load_broadcast404, <8 x i32> %"oldMask&test4064613", <8 x i32> zeroinitializer
  %54 = icmp slt <8 x i32> %"oldMask&test406", zeroinitializer
  %55 = bitcast <8 x i1> %54 to i8
  %cmp.i3851.not = icmp eq i8 %55, 0
  br i1 %cmp.i3851.not, label %for_test395.for_exit398_crit_edge, label %not_all_continued_or_breaked460, !llvm.loop !22

safe_if_run_true512:                              ; preds = %for_exit398
  %mul__bestIdx_load521 = shl nsw <8 x i32> %bestIdx.5.lcssa, splat (i32 3)
  %56 = or disjoint <8 x i32> %mul__bestIdx_load521, splat (i32 4)
  %new_add3471 = sext <8 x i32> %56 to <8 x i64>
  %vecmask_1.i4196 = shufflevector <8 x i32> %"oldMask&test513", <8 x i32> poison, <4 x i32> <i32 0, i32 1, i32 2, i32 3>
  %vecmask_2.i4197 = shufflevector <8 x i32> %"oldMask&test513", <8 x i32> poison, <4 x i32> <i32 4, i32 5, i32 6, i32 7>
  %offsets_1.i4198 = shufflevector <8 x i64> %new_add3471, <8 x i64> poison, <4 x i32> <i32 0, i32 1, i32 2, i32 3>
  %offsets_2.i4199 = shufflevector <8 x i64> %new_add3471, <8 x i64> poison, <4 x i32> <i32 4, i32 5, i32 6, i32 7>
  %v1_1.i4200 = tail call <4 x i32> @llvm.x86.avx2.gather.q.d.256(<4 x i32> undef, ptr %HashIndex_ptr, <4 x i64> %offsets_1.i4198, <4 x i32> %vecmask_1.i4196, i8 1)
  %v2_1.i4201 = tail call <4 x i32> @llvm.x86.avx2.gather.q.d.256(<4 x i32> undef, ptr %HashIndex_ptr, <4 x i64> %offsets_2.i4199, <4 x i32> %vecmask_2.i4197, i8 1)
  %v.i4202 = shufflevector <4 x i32> %v1_1.i4200, <4 x i32> %v2_1.i4201, <8 x i32> <i32 0, i32 1, i32 2, i32 3, i32 4, i32 5, i32 6, i32 7>
  %val.i4203 = bitcast <8 x i32> %v.i4202 to <8 x float>
  call void @llvm.x86.avx.maskstore.ps.256(ptr nonnull %ptr, <8 x i32> %"oldMask&test513", <8 x float> %val.i4203)
  br label %if_done

partial_inner_only:                               ; preds = %partial_inner_all_outer
  %smear_counter_init571 = insertelement <8 x i32> poison, i32 %counter.1.lcssa, i64 0
  %smear_counter572 = shufflevector <8 x i32> %smear_counter_init571, <8 x i32> poison, <8 x i32> zeroinitializer
  %iter_val573 = add nsw <8 x i32> %smear_counter572, <i32 0, i32 1, i32 2, i32 3, i32 4, i32 5, i32 6, i32 7>
  %smear_end_init574 = insertelement <8 x i32> poison, i32 %ret.i.i, i64 0
  %smear_end575 = shufflevector <8 x i32> %smear_end_init574, <8 x i32> poison, <8 x i32> zeroinitializer
  %cmp576 = icmp slt <8 x i32> %iter_val573, %smear_end575
  %cmp576_to_boolvec = sext <8 x i1> %cmp576 to <8 x i32>
  %mul__index_load581.elt0 = shl nsw i32 %counter.1.lcssa, 2
  %57 = sext i32 %mul__index_load581.elt0 to i64
  %ptr3406 = getelementptr i8, ptr %Results_ptr, i64 %57
  call void @llvm.masked.store.v8f32.p0(<8 x float> splat (float 0xFFFFFFFFE0000000), ptr %ptr3406, i32 1, <8 x i1> %cmp576)
  %mul__index_load589 = shl nsw <8 x i32> %iter_val573, splat (i32 3)
  %mask.i4204 = bitcast <8 x i32> %cmp576_to_boolvec to <8 x float>
  %v_1.i4205 = tail call <8 x float> @llvm.x86.avx2.gather.d.ps.256(<8 x float> undef, ptr readonly %QueryPositions_ptr, <8 x i32> %mul__index_load589, <8 x float> %mask.i4204, i8 1)
  %58 = or disjoint <8 x i32> %mul__index_load589, splat (i32 4)
  %v_1.i4208 = tail call <8 x float> @llvm.x86.avx2.gather.d.ps.256(<8 x float> undef, ptr readonly %QueryPositions_ptr, <8 x i32> %58, <8 x float> %mask.i4204, i8 1)
  %get_element610_broadcast = insertelement <8 x float> poison, float %GridOrigin_ptr_load_load.unpack, i64 0
  %get_element610_broadcast611 = shufflevector <8 x float> %get_element610_broadcast, <8 x float> poison, <8 x i32> zeroinitializer
  %get_element613_broadcast = insertelement <8 x float> poison, float %GridOrigin_ptr_load_load.unpack3386, i64 0
  %get_element613_broadcast614 = shufflevector <8 x float> %get_element613_broadcast, <8 x float> poison, <8 x i32> zeroinitializer
  %sub_a14_x_b26_x.i3918 = fsub <8 x float> %v_1.i4205, %get_element610_broadcast611
  %sub_a19_y_b211_y.i3919 = fsub <8 x float> %v_1.i4208, %get_element613_broadcast614
  %GridResolutionInv_load619_broadcast = insertelement <8 x float> poison, float %GridResolutionInv_ptr_load_load, i64 0
  %GridResolutionInv_load619_broadcast620 = shufflevector <8 x float> %GridResolutionInv_load619_broadcast, <8 x float> poison, <8 x i32> zeroinitializer
  %mul_v14_x_s_load.i3925 = fmul <8 x float> %GridResolutionInv_load619_broadcast620, %sub_a14_x_b26_x.i3918
  %mul_v17_y_s_load9.i3926 = fmul <8 x float> %GridResolutionInv_load619_broadcast620, %sub_a19_y_b211_y.i3919
  %call.i.i.i3932 = tail call <8 x float> @llvm.x86.avx.round.ps.256(<8 x float> %mul_v14_x_s_load.i3925, i32 9)
  %call.i.i3.i3933 = tail call <8 x float> @llvm.x86.avx.round.ps.256(<8 x float> %mul_v17_y_s_load9.i3926, i32 9)
  %v12_x_to_int32.i3939 = fptosi <8 x float> %call.i.i.i3932 to <8 x i32>
  %v14_y_to_int32.i3940 = fptosi <8 x float> %call.i.i3.i3933 to <8 x i32>
  %get_element635_broadcast = insertelement <8 x i32> poison, i32 %GridDimensions_ptr_load_load.unpack, i64 0
  %get_element635_broadcast636 = shufflevector <8 x i32> %get_element635_broadcast, <8 x i32> poison, <8 x i32> zeroinitializer
  %get_element638_broadcast = insertelement <8 x i32> poison, i32 %GridDimensions_ptr_load_load.unpack3389, i64 0
  %get_element638_broadcast639 = shufflevector <8 x i32> %get_element638_broadcast, <8 x i32> poison, <8 x i32> zeroinitializer
  %sub_a14_x_b_load.i3950 = add nsw <8 x i32> %get_element635_broadcast636, splat (i32 -1)
  %sub_a17_y_b_load9.i3951 = add nsw <8 x i32> %get_element638_broadcast639, splat (i32 -1)
  %59 = call <8 x i32> @llvm.smax.v8i32(<8 x i32> %v12_x_to_int32.i3939, <8 x i32> zeroinitializer)
  %60 = call <8 x i32> @llvm.smax.v8i32(<8 x i32> %v14_y_to_int32.i3940, <8 x i32> zeroinitializer)
  %blend.i16.i.v = call <8 x i32> @llvm.smin.v8i32(<8 x i32> %59, <8 x i32> %sub_a14_x_b_load.i3950)
  %blend.i20.i.v = call <8 x i32> @llvm.smin.v8i32(<8 x i32> %60, <8 x i32> %sub_a17_y_b_load9.i3951)
  %61 = bitcast <8 x i1> %cmp576 to i8
  %cmp.i3840.not4636 = icmp eq i8 %61, 0
  br i1 %cmp.i3840.not4636, label %for_exit652, label %for_loop650

not_all_continued_or_breaked690:                  ; preds = %for_loop650
  %new_mask701 = xor <8 x i32> %"oldMask&test675", %"oldMask&test6594640"
  %62 = icmp slt <8 x i32> %new_mask701, zeroinitializer
  %63 = bitcast <8 x i1> %62 to i8
  %cmp.i3855.not4629 = icmp eq i8 %63, 0
  br i1 %cmp.i3855.not4629, label %for_step651, label %for_loop705

for_loop705:                                      ; preds = %not_all_continued_or_breaked690, %for_step706
  %64 = phi i8 [ %69, %for_step706 ], [ %63, %not_all_continued_or_breaked690 ]
  %"oldMask&test7144633" = phi <8 x i32> [ %"oldMask&test714", %for_step706 ], [ %new_mask701, %not_all_continued_or_breaked690 ]
  %dy711.04632 = phi <8 x i32> [ %dy_load944_plus1, %for_step706 ], [ splat (i32 -1), %not_all_continued_or_breaked690 ]
  %bestIdx648.24631 = phi <8 x i32> [ %bestIdx648.3, %for_step706 ], [ %bestIdx648.04638, %not_all_continued_or_breaked690 ]
  %bestDistSq647.24630 = phi <8 x float> [ %bestDistSq647.3, %for_step706 ], [ %bestDistSq647.04637, %not_all_continued_or_breaked690 ]
  %add_cell607722_y_dy_load724 = add nsw <8 x i32> %dy711.04632, %blend.i20.i.v
  %greaterequal_ny_load725_GridDimensions726_y_broadcast728.not = icmp ult <8 x i32> %add_cell607722_y_dy_load724, %get_element638_broadcast639
  %"oldMask&test730" = select <8 x i1> %greaterequal_ny_load725_GridDimensions726_y_broadcast728.not, <8 x i32> zeroinitializer, <8 x i32> %"oldMask&test7144633"
  %65 = icmp slt <8 x i32> %"oldMask&test730", zeroinitializer
  %66 = bitcast <8 x i1> %65 to i8
  %"equal_finished&func743_internal_mask&function_mask720" = icmp eq i8 %64, %66
  br i1 %"equal_finished&func743_internal_mask&function_mask720", label %for_step706, label %not_all_continued_or_breaked745

for_test825.for_step706.loopexit_crit_edge:       ; preds = %for_loop826
  %67 = bitcast <8 x float> %blend.i4226 to <8 x i32>
  br label %for_step706

for_step706:                                      ; preds = %not_all_continued_or_breaked811, %for_test825.for_step706.loopexit_crit_edge, %not_all_continued_or_breaked745, %for_loop705
  %bestDistSq647.3 = phi <8 x float> [ %bestDistSq647.24630, %for_loop705 ], [ %bestDistSq647.24630, %not_all_continued_or_breaked745 ], [ %blend.i.i4222, %for_test825.for_step706.loopexit_crit_edge ], [ %bestDistSq647.24630, %not_all_continued_or_breaked811 ]
  %bestIdx648.3 = phi <8 x i32> [ %bestIdx648.24631, %for_loop705 ], [ %bestIdx648.24631, %not_all_continued_or_breaked745 ], [ %67, %for_test825.for_step706.loopexit_crit_edge ], [ %bestIdx648.24631, %not_all_continued_or_breaked811 ]
  %continue_lanes_memory709.1 = phi <8 x i32> [ %"oldMask&test730", %for_loop705 ], [ %"mask|continueMask800", %not_all_continued_or_breaked745 ], [ %"mask|continueMask800", %for_test825.for_step706.loopexit_crit_edge ], [ %"mask|continueMask800", %not_all_continued_or_breaked811 ]
  %internal_mask_memory.12 = phi <8 x i32> [ zeroinitializer, %for_loop705 ], [ zeroinitializer, %not_all_continued_or_breaked745 ], [ %new_mask822, %for_test825.for_step706.loopexit_crit_edge ], [ %new_mask822, %not_all_continued_or_breaked811 ]
  %"mask|continue_mask941" = or <8 x i32> %internal_mask_memory.12, %continue_lanes_memory709.1
  %dy_load944_plus1 = add nsw <8 x i32> %dy711.04632, splat (i32 1)
  %lessequal_dy_load712_.inv = icmp sgt <8 x i32> %dy711.04632, zeroinitializer
  %"oldMask&test714" = select <8 x i1> %lessequal_dy_load712_.inv, <8 x i32> zeroinitializer, <8 x i32> %"mask|continue_mask941"
  %68 = icmp slt <8 x i32> %"oldMask&test714", zeroinitializer
  %69 = bitcast <8 x i1> %68 to i8
  %cmp.i3855.not = icmp eq i8 %69, 0
  br i1 %cmp.i3855.not, label %for_step651, label %for_loop705, !llvm.loop !23

not_all_continued_or_breaked745:                  ; preds = %for_loop705
  %new_mask756 = xor <8 x i32> %"oldMask&test730", %"oldMask&test7144633"
  %mul_ny_load760_GridDimensions761_x_broadcast763 = mul nsw <8 x i32> %add_cell607722_y_dy_load724, %get_element635_broadcast636
  %add_mul_ny_load760_GridDimensions761_x_broadcast763_nx_load764 = add nsw <8 x i32> %mul_ny_load760_GridDimensions761_x_broadcast763, %add_cell607667_x_dx_load669
  %CellStartEnd_load767768__data = load ptr, ptr %CellStartEnd, align 8
  %mul__cellHash_load766 = shl nsw <8 x i32> %add_mul_ny_load760_GridDimensions761_x_broadcast763_nx_load764, splat (i32 3)
  %v_1.i4210 = tail call <8 x i32> @llvm.x86.avx2.gather.d.d.256(<8 x i32> undef, ptr readonly %CellStartEnd_load767768__data, <8 x i32> %mul__cellHash_load766, <8 x i32> %new_mask756, i8 1)
  %70 = or disjoint <8 x i32> %mul__cellHash_load766, splat (i32 4)
  %v_1.i4212 = tail call <8 x i32> @llvm.x86.avx2.gather.d.d.256(<8 x i32> undef, ptr readonly %CellStartEnd_load767768__data, <8 x i32> %70, <8 x i32> %new_mask756, i8 1)
  %isneg3392 = icmp slt <8 x i32> %v_1.i4210, zeroinitializer
  %"oldMask&test795" = select <8 x i1> %isneg3392, <8 x i32> %new_mask756, <8 x i32> zeroinitializer
  %"mask|continueMask800" = or <8 x i32> %"oldMask&test795", %"oldMask&test730"
  %71 = icmp slt <8 x i32> %"mask|continueMask800", zeroinitializer
  %72 = bitcast <8 x i1> %71 to i8
  %"equal_finished&func808_internal_mask&function_mask720" = icmp eq i8 %64, %72
  br i1 %"equal_finished&func808_internal_mask&function_mask720", label %for_step706, label %not_all_continued_or_breaked811

not_all_continued_or_breaked811:                  ; preds = %not_all_continued_or_breaked745
  %new_mask822 = xor <8 x i32> %"mask|continueMask800", %"oldMask&test7144633"
  %less_i_load834_end_load8354620 = icmp slt <8 x i32> %v_1.i4210, %v_1.i4212
  %"oldMask&test8374621" = select <8 x i1> %less_i_load834_end_load8354620, <8 x i32> %new_mask822, <8 x i32> zeroinitializer
  %73 = icmp slt <8 x i32> %"oldMask&test8374621", zeroinitializer
  %74 = bitcast <8 x i1> %73 to i8
  %cmp.i3859.not4622 = icmp eq i8 %74, 0
  br i1 %cmp.i3859.not4622, label %for_step706, label %for_loop826.lr.ph

for_loop826.lr.ph:                                ; preds = %not_all_continued_or_breaked811
  %75 = bitcast <8 x i32> %bestIdx648.24631 to <8 x float>
  br label %for_loop826

for_loop826:                                      ; preds = %for_loop826.lr.ph, %for_loop826
  %"oldMask&test8374626" = phi <8 x i32> [ %"oldMask&test8374621", %for_loop826.lr.ph ], [ %"oldMask&test837", %for_loop826 ]
  %i832.04625 = phi <8 x i32> [ %v_1.i4210, %for_loop826.lr.ph ], [ %i_load933_plus1, %for_loop826 ]
  %bestIdx648.44624 = phi <8 x float> [ %75, %for_loop826.lr.ph ], [ %blend.i4226, %for_loop826 ]
  %bestDistSq647.44623 = phi <8 x float> [ %bestDistSq647.24630, %for_loop826.lr.ph ], [ %blend.i.i4222, %for_loop826 ]
  %mul__i_load845 = shl nsw <8 x i32> %i832.04625, splat (i32 3)
  %mask.i4214 = bitcast <8 x i32> %"oldMask&test8374626" to <8 x float>
  %v_1.i4215 = tail call <8 x float> @llvm.x86.avx2.gather.d.ps.256(<8 x float> undef, ptr readonly %SortedPositions_ptr, <8 x i32> %mul__i_load845, <8 x float> %mask.i4214, i8 1)
  %76 = or disjoint <8 x i32> %mul__i_load845, splat (i32 4)
  %v_1.i4218 = tail call <8 x float> @llvm.x86.avx2.gather.d.ps.256(<8 x float> undef, ptr readonly %SortedPositions_ptr, <8 x i32> %76, <8 x float> %mask.i4214, i8 1)
  %sub_a14_x_b26_x.i.i3975 = fsub <8 x float> %v_1.i4215, %v_1.i4205
  %mul_a13_x_b25_x.i.i.i3984 = fmul <8 x float> %sub_a14_x_b26_x.i.i3975, %sub_a14_x_b26_x.i.i3975
  %sub_a19_y_b211_y.i.i3976 = fsub <8 x float> %v_1.i4218, %v_1.i4208
  %mul_a17_y_b29_y.i.i.i3985 = fmul <8 x float> %sub_a19_y_b211_y.i.i3976, %sub_a19_y_b211_y.i.i3976
  %add_mul_a13_x_b25_x_mul_a17_y_b29_y.i.i.i3986 = fadd <8 x float> %mul_a13_x_b25_x.i.i.i3984, %mul_a17_y_b29_y.i.i.i3985
  %less_distSq_load905_bestDistSq_load906 = fcmp olt <8 x float> %add_mul_a13_x_b25_x_mul_a17_y_b29_y.i.i.i3986, %bestDistSq647.44623
  %77 = bitcast <8 x i32> %"oldMask&test8374626" to <8 x float>
  %mask_as_float.i.i4220 = select <8 x i1> %less_distSq_load905_bestDistSq_load906, <8 x float> %77, <8 x float> zeroinitializer
  %blend.i.i4222 = call <8 x float> @llvm.x86.avx.blendv.ps.256(<8 x float> %bestDistSq647.44623, <8 x float> %add_mul_a13_x_b25_x_mul_a17_y_b29_y.i.i.i3986, <8 x float> %mask_as_float.i.i4220)
  %newAsFloat.i4225 = bitcast <8 x i32> %i832.04625 to <8 x float>
  %blend.i4226 = call <8 x float> @llvm.x86.avx.blendv.ps.256(<8 x float> %bestIdx648.44624, <8 x float> %newAsFloat.i4225, <8 x float> %mask_as_float.i.i4220)
  %i_load933_plus1 = add nsw <8 x i32> %i832.04625, splat (i32 1)
  %less_i_load834_end_load835 = icmp slt <8 x i32> %i_load933_plus1, %v_1.i4212
  %"oldMask&test837" = select <8 x i1> %less_i_load834_end_load835, <8 x i32> %"oldMask&test8374626", <8 x i32> zeroinitializer
  %78 = icmp slt <8 x i32> %"oldMask&test837", zeroinitializer
  %79 = bitcast <8 x i1> %78 to i8
  %cmp.i3859.not = icmp eq i8 %79, 0
  br i1 %cmp.i3859.not, label %for_test825.for_step706.loopexit_crit_edge, label %for_loop826, !llvm.loop !24

safe_if_after_true964:                            ; preds = %safe_if_run_true965, %for_exit652
  %"oldMask&~test989" = select <8 x i1> %notequal_bestIdx_load961_, <8 x i32> zeroinitializer, <8 x i32> %cmp576_to_boolvec
  %not.notequal_bestIdx_load961_ = xor <8 x i1> %notequal_bestIdx_load961_, splat (i1 true)
  %80 = select <8 x i1> %not.notequal_bestIdx_load961_, <8 x i1> %cmp576, <8 x i1> zeroinitializer
  %81 = bitcast <8 x i1> %80 to i8
  %cmp.i3861.not = icmp eq i8 %81, 0
  br i1 %cmp.i3861.not, label %common.ret, label %for_test994.preheader

for_test994.preheader:                            ; preds = %safe_if_after_true964
  %SortedLength_load1003_broadcast = insertelement <8 x i32> poison, i32 %SortedLength_ptr_load_load, i64 0
  %SortedLength_load1003_broadcast1004 = shufflevector <8 x i32> %SortedLength_load1003_broadcast, <8 x i32> poison, <8 x i32> zeroinitializer
  %less_i_load1002_SortedLength_load1003_broadcast10044643 = icmp sgt <8 x i32> %SortedLength_load1003_broadcast1004, zeroinitializer
  %"oldMask&test10064644" = select <8 x i1> %less_i_load1002_SortedLength_load1003_broadcast10044643, <8 x i32> %"oldMask&~test989", <8 x i32> zeroinitializer
  %82 = icmp slt <8 x i32> %"oldMask&test10064644", zeroinitializer
  %83 = bitcast <8 x i1> %82 to i8
  %cmp.i3862.not4645 = icmp eq i8 %83, 0
  br i1 %cmp.i3862.not4645, label %for_exit997, label %not_all_continued_or_breaked1060.lr.ph

not_all_continued_or_breaked1060.lr.ph:           ; preds = %for_test994.preheader
  %invariant.gep4653 = getelementptr i8, ptr %SortedPositions_ptr, i64 4
  %84 = bitcast <8 x i32> %bestIdx648.0.lcssa to <8 x float>
  br label %not_all_continued_or_breaked1060

safe_if_run_true965:                              ; preds = %for_exit652
  %"oldMask&test966" = select <8 x i1> %notequal_bestIdx_load961_, <8 x i32> %cmp576_to_boolvec, <8 x i32> zeroinitializer
  %mul__bestIdx_load974 = shl nsw <8 x i32> %bestIdx648.0.lcssa, splat (i32 3)
  %85 = or disjoint <8 x i32> %mul__bestIdx_load974, splat (i32 4)
  %new_add3503 = sext <8 x i32> %85 to <8 x i64>
  %vecmask_1.i4227 = shufflevector <8 x i32> %"oldMask&test966", <8 x i32> poison, <4 x i32> <i32 0, i32 1, i32 2, i32 3>
  %vecmask_2.i4228 = shufflevector <8 x i32> %"oldMask&test966", <8 x i32> poison, <4 x i32> <i32 4, i32 5, i32 6, i32 7>
  %offsets_1.i4229 = shufflevector <8 x i64> %new_add3503, <8 x i64> poison, <4 x i32> <i32 0, i32 1, i32 2, i32 3>
  %offsets_2.i4230 = shufflevector <8 x i64> %new_add3503, <8 x i64> poison, <4 x i32> <i32 4, i32 5, i32 6, i32 7>
  %v1_1.i4231 = tail call <4 x i32> @llvm.x86.avx2.gather.q.d.256(<4 x i32> undef, ptr %HashIndex_ptr, <4 x i64> %offsets_1.i4229, <4 x i32> %vecmask_1.i4227, i8 1)
  %v2_1.i4232 = tail call <4 x i32> @llvm.x86.avx2.gather.q.d.256(<4 x i32> undef, ptr %HashIndex_ptr, <4 x i64> %offsets_2.i4230, <4 x i32> %vecmask_2.i4228, i8 1)
  %v.i4233 = shufflevector <4 x i32> %v1_1.i4231, <4 x i32> %v2_1.i4232, <8 x i32> <i32 0, i32 1, i32 2, i32 3, i32 4, i32 5, i32 6, i32 7>
  %val.i4234 = bitcast <8 x i32> %v.i4233 to <8 x float>
  call void @llvm.x86.avx.maskstore.ps.256(ptr %ptr3406, <8 x i32> %"oldMask&test966", <8 x float> %val.i4234)
  br label %safe_if_after_true964

for_test994.for_exit997_crit_edge:                ; preds = %not_all_continued_or_breaked1060
  %86 = bitcast <8 x float> %blend.i4241 to <8 x i32>
  br label %for_exit997

for_exit997:                                      ; preds = %for_test994.for_exit997_crit_edge, %for_test994.preheader
  %bestIdx648.5.lcssa = phi <8 x i32> [ %86, %for_test994.for_exit997_crit_edge ], [ %bestIdx648.0.lcssa, %for_test994.preheader ]
  %notequal_bestIdx_load1108_ = icmp eq <8 x i32> %bestIdx648.5.lcssa, splat (i32 -1)
  %"oldMask&test1113" = select <8 x i1> %notequal_bestIdx_load1108_, <8 x i32> zeroinitializer, <8 x i32> %"oldMask&~test989"
  %87 = icmp slt <8 x i32> %"oldMask&test1113", zeroinitializer
  %88 = bitcast <8 x i1> %87 to i8
  %cmp.i3864.not = icmp eq i8 %88, 0
  br i1 %cmp.i3864.not, label %common.ret, label %safe_if_run_true1112

not_all_continued_or_breaked1060:                 ; preds = %not_all_continued_or_breaked1060.lr.ph, %not_all_continued_or_breaked1060
  %indvars.iv4682 = phi i64 [ 0, %not_all_continued_or_breaked1060.lr.ph ], [ %indvars.iv.next4683, %not_all_continued_or_breaked1060 ]
  %"oldMask&test10064651" = phi <8 x i32> [ %"oldMask&test10064644", %not_all_continued_or_breaked1060.lr.ph ], [ %"oldMask&test1006", %not_all_continued_or_breaked1060 ]
  %i1001.04650 = phi <8 x i32> [ zeroinitializer, %not_all_continued_or_breaked1060.lr.ph ], [ %i_load1102_plus1, %not_all_continued_or_breaked1060 ]
  %bestIdx648.54647 = phi <8 x float> [ %84, %not_all_continued_or_breaked1060.lr.ph ], [ %blend.i4241, %not_all_continued_or_breaked1060 ]
  %bestDistSq647.54646 = phi <8 x float> [ %bestDistSq647.0.lcssa, %not_all_continued_or_breaked1060.lr.ph ], [ %blend.i.i4237, %not_all_continued_or_breaked1060 ]
  %89 = shl nsw i64 %indvars.iv4682, 3
  %ptr3520 = getelementptr i8, ptr %SortedPositions_ptr, i64 %89
  %SortedPositions_ptr_load1015_offset_load35193521 = load float, ptr %ptr3520, align 4
  %SortedPositions_ptr_load1015_offset_load35193522 = insertelement <8 x float> poison, float %SortedPositions_ptr_load1015_offset_load35193521, i64 0
  %SortedPositions_ptr_load1015_offset_load35193523 = shufflevector <8 x float> %SortedPositions_ptr_load1015_offset_load35193522, <8 x float> poison, <8 x i32> zeroinitializer
  %sub_a14_x_b26_x.i.i3987 = fsub <8 x float> %SortedPositions_ptr_load1015_offset_load35193523, %v_1.i4205
  %mul_a13_x_b25_x.i.i.i3996 = fmul <8 x float> %sub_a14_x_b26_x.i.i3987, %sub_a14_x_b26_x.i.i3987
  %gep4654 = getelementptr i8, ptr %invariant.gep4653, i64 %89
  %SortedPositions_ptr_load1015_offset_load103035293534 = load float, ptr %gep4654, align 4
  %SortedPositions_ptr_load1015_offset_load103035293535 = insertelement <8 x float> poison, float %SortedPositions_ptr_load1015_offset_load103035293534, i64 0
  %SortedPositions_ptr_load1015_offset_load103035293536 = shufflevector <8 x float> %SortedPositions_ptr_load1015_offset_load103035293535, <8 x float> poison, <8 x i32> zeroinitializer
  %sub_a19_y_b211_y.i.i3988 = fsub <8 x float> %SortedPositions_ptr_load1015_offset_load103035293536, %v_1.i4208
  %mul_a17_y_b29_y.i.i.i3997 = fmul <8 x float> %sub_a19_y_b211_y.i.i3988, %sub_a19_y_b211_y.i.i3988
  %add_mul_a13_x_b25_x_mul_a17_y_b29_y.i.i.i3998 = fadd <8 x float> %mul_a13_x_b25_x.i.i.i3996, %mul_a17_y_b29_y.i.i.i3997
  %less_distSq_load1074_bestDistSq_load1075 = fcmp olt <8 x float> %add_mul_a13_x_b25_x_mul_a17_y_b29_y.i.i.i3998, %bestDistSq647.54646
  %90 = bitcast <8 x i32> %"oldMask&test10064651" to <8 x float>
  %mask_as_float.i.i4235 = select <8 x i1> %less_distSq_load1074_bestDistSq_load1075, <8 x float> %90, <8 x float> zeroinitializer
  %blend.i.i4237 = call <8 x float> @llvm.x86.avx.blendv.ps.256(<8 x float> %bestDistSq647.54646, <8 x float> %add_mul_a13_x_b25_x_mul_a17_y_b29_y.i.i.i3998, <8 x float> %mask_as_float.i.i4235)
  %newAsFloat.i4240 = bitcast <8 x i32> %i1001.04650 to <8 x float>
  %blend.i4241 = call <8 x float> @llvm.x86.avx.blendv.ps.256(<8 x float> %bestIdx648.54647, <8 x float> %newAsFloat.i4240, <8 x float> %mask_as_float.i.i4235)
  %indvars.iv.next4683 = add nuw nsw i64 %indvars.iv4682, 1
  %i_load1102_plus1 = add nuw nsw <8 x i32> %i1001.04650, splat (i32 1)
  %less_i_load1002_SortedLength_load1003_broadcast1004 = icmp slt <8 x i32> %i_load1102_plus1, %SortedLength_load1003_broadcast1004
  %"oldMask&test1006" = select <8 x i1> %less_i_load1002_SortedLength_load1003_broadcast1004, <8 x i32> %"oldMask&test10064651", <8 x i32> zeroinitializer
  %91 = icmp slt <8 x i32> %"oldMask&test1006", zeroinitializer
  %92 = bitcast <8 x i1> %91 to i8
  %cmp.i3862.not = icmp eq i8 %92, 0
  br i1 %cmp.i3862.not, label %for_test994.for_exit997_crit_edge, label %not_all_continued_or_breaked1060, !llvm.loop !25

safe_if_run_true1112:                             ; preds = %for_exit997
  %mul__bestIdx_load1121 = shl nsw <8 x i32> %bestIdx648.5.lcssa, splat (i32 3)
  %93 = or disjoint <8 x i32> %mul__bestIdx_load1121, splat (i32 4)
  %new_add3541 = sext <8 x i32> %93 to <8 x i64>
  %vecmask_1.i4242 = shufflevector <8 x i32> %"oldMask&test1113", <8 x i32> poison, <4 x i32> <i32 0, i32 1, i32 2, i32 3>
  %vecmask_2.i4243 = shufflevector <8 x i32> %"oldMask&test1113", <8 x i32> poison, <4 x i32> <i32 4, i32 5, i32 6, i32 7>
  %offsets_1.i4244 = shufflevector <8 x i64> %new_add3541, <8 x i64> poison, <4 x i32> <i32 0, i32 1, i32 2, i32 3>
  %offsets_2.i4245 = shufflevector <8 x i64> %new_add3541, <8 x i64> poison, <4 x i32> <i32 4, i32 5, i32 6, i32 7>
  %v1_1.i4246 = tail call <4 x i32> @llvm.x86.avx2.gather.q.d.256(<4 x i32> undef, ptr %HashIndex_ptr, <4 x i64> %offsets_1.i4244, <4 x i32> %vecmask_1.i4242, i8 1)
  %v2_1.i4247 = tail call <4 x i32> @llvm.x86.avx2.gather.q.d.256(<4 x i32> undef, ptr %HashIndex_ptr, <4 x i64> %offsets_2.i4245, <4 x i32> %vecmask_2.i4243, i8 1)
  %v.i4248 = shufflevector <4 x i32> %v1_1.i4246, <4 x i32> %v2_1.i4247, <8 x i32> <i32 0, i32 1, i32 2, i32 3, i32 4, i32 5, i32 6, i32 7>
  %val.i4249 = bitcast <8 x i32> %v.i4248 to <8 x float>
  call void @llvm.x86.avx.maskstore.ps.256(ptr %ptr3406, <8 x i32> %"oldMask&test1113", <8 x float> %val.i4249)
  br label %common.ret

foreach_full_body1200:                            ; preds = %foreach_full_body1200.lr.ph, %if_done1636
  %indvars.iv4660 = phi i64 [ %2, %foreach_full_body1200.lr.ph ], [ %indvars.iv.next4661, %if_done1636 ]
  %94 = trunc nsw i64 %indvars.iv4660 to i32
  %smear_counter_init1251 = insertelement <8 x i32> poison, i32 %94, i64 0
  %smear_counter1252 = shufflevector <8 x i32> %smear_counter_init1251, <8 x i32> poison, <8 x i32> zeroinitializer
  %iter_val1253 = add nsw <8 x i32> %smear_counter1252, <i32 0, i32 1, i32 2, i32 3, i32 4, i32 5, i32 6, i32 7>
  %95 = shl nsw i64 %indvars.iv4660, 2
  %ptr3560 = getelementptr i8, ptr %Results_ptr, i64 %95
  store <8 x i32> splat (i32 -1), ptr %ptr3560, align 4, !filename !11, !first_line !12, !first_column !13, !last_line !12, !last_column !14
  %mul__index_load1262 = shl nsw <8 x i32> %iter_val1253, splat (i32 3)
  %v_1.i4250 = tail call <8 x float> @llvm.x86.avx2.gather.d.ps.256(<8 x float> undef, ptr readonly %QueryPositions_ptr, <8 x i32> %mul__index_load1262, <8 x float> splat (float 0xFFFFFFFFE0000000), i8 1)
  %96 = or disjoint <8 x i32> %mul__index_load1262, splat (i32 4)
  %v_1.i4252 = tail call <8 x float> @llvm.x86.avx2.gather.d.ps.256(<8 x float> undef, ptr readonly %QueryPositions_ptr, <8 x i32> %96, <8 x float> splat (float 0xFFFFFFFFE0000000), i8 1)
  %sub_a14_x_b26_x.i3999 = fsub <8 x float> %v_1.i4250, %get_element1283_broadcast1284
  %sub_a19_y_b211_y.i4000 = fsub <8 x float> %v_1.i4252, %get_element1286_broadcast1287
  %mul_v14_x_s_load.i4006 = fmul <8 x float> %GridResolutionInv_load1292_broadcast1293, %sub_a14_x_b26_x.i3999
  %mul_v17_y_s_load9.i4007 = fmul <8 x float> %GridResolutionInv_load1292_broadcast1293, %sub_a19_y_b211_y.i4000
  %call.i.i.i4013 = tail call <8 x float> @llvm.x86.avx.round.ps.256(<8 x float> %mul_v14_x_s_load.i4006, i32 9)
  %call.i.i3.i4014 = tail call <8 x float> @llvm.x86.avx.round.ps.256(<8 x float> %mul_v17_y_s_load9.i4007, i32 9)
  %v12_x_to_int32.i4020 = fptosi <8 x float> %call.i.i.i4013 to <8 x i32>
  %v14_y_to_int32.i4021 = fptosi <8 x float> %call.i.i3.i4014 to <8 x i32>
  %97 = call <8 x i32> @llvm.smax.v8i32(<8 x i32> %v12_x_to_int32.i4020, <8 x i32> zeroinitializer)
  %98 = call <8 x i32> @llvm.smax.v8i32(<8 x i32> %v14_y_to_int32.i4021, <8 x i32> zeroinitializer)
  %blend.i4265.v = call <8 x i32> @llvm.smin.v8i32(<8 x i32> %97, <8 x i32> %sub_a14_x_b_load.i4031)
  %blend.i4269.v = call <8 x i32> @llvm.smin.v8i32(<8 x i32> %98, <8 x i32> %sub_a17_y_b_load9.i4032)
  br label %for_loop1323

for_loop1323:                                     ; preds = %foreach_full_body1200, %for_step1324
  %99 = phi i8 [ -1, %foreach_full_body1200 ], [ %103, %for_step1324 ]
  %"oldMask&test13324536" = phi <8 x i32> [ splat (i32 -1), %foreach_full_body1200 ], [ %"oldMask&test1332", %for_step1324 ]
  %dx1329.04535 = phi <8 x i32> [ splat (i32 -1), %foreach_full_body1200 ], [ %dx_load1628_plus1, %for_step1324 ]
  %bestIdx1321.04534 = phi <8 x i32> [ splat (i32 -1), %foreach_full_body1200 ], [ %bestIdx1321.1, %for_step1324 ]
  %bestDistSq1320.04533 = phi <8 x float> [ splat (float 0x47EFFFFFE0000000), %foreach_full_body1200 ], [ %bestDistSq1320.1, %for_step1324 ]
  %add_cell12801340_x_dx_load1342 = add nsw <8 x i32> %dx1329.04535, %blend.i4265.v
  %greaterequal_nx_load1343_GridDimensions11841344_x_broadcast1346.not = icmp ult <8 x i32> %add_cell12801340_x_dx_load1342, %get_element1308_broadcast1309
  %"oldMask&test1348" = select <8 x i1> %greaterequal_nx_load1343_GridDimensions11841344_x_broadcast1346.not, <8 x i32> zeroinitializer, <8 x i32> %"oldMask&test13324536"
  %100 = icmp slt <8 x i32> %"oldMask&test1348", zeroinitializer
  %101 = bitcast <8 x i1> %100 to i8
  %"equal_finished&func1361_internal_mask&function_mask1338" = icmp eq i8 %99, %101
  br i1 %"equal_finished&func1361_internal_mask&function_mask1338", label %for_step1324, label %not_all_continued_or_breaked1363

for_step1324:                                     ; preds = %not_all_continued_or_breaked1363, %for_step1379, %for_loop1323
  %bestDistSq1320.1 = phi <8 x float> [ %bestDistSq1320.04533, %for_loop1323 ], [ %bestDistSq1320.04533, %not_all_continued_or_breaked1363 ], [ %bestDistSq1320.3, %for_step1379 ]
  %bestIdx1321.1 = phi <8 x i32> [ %bestIdx1321.04534, %for_loop1323 ], [ %bestIdx1321.04534, %not_all_continued_or_breaked1363 ], [ %bestIdx1321.3, %for_step1379 ]
  %internal_mask_memory.19 = phi <8 x i32> [ zeroinitializer, %for_loop1323 ], [ %new_mask1374, %not_all_continued_or_breaked1363 ], [ %new_mask1374, %for_step1379 ]
  %"mask|continue_mask1625" = or <8 x i32> %internal_mask_memory.19, %"oldMask&test1348"
  %dx_load1628_plus1 = add nsw <8 x i32> %dx1329.04535, splat (i32 1)
  %lessequal_dx_load1330_.inv = icmp sgt <8 x i32> %dx1329.04535, zeroinitializer
  %"oldMask&test1332" = select <8 x i1> %lessequal_dx_load1330_.inv, <8 x i32> zeroinitializer, <8 x i32> %"mask|continue_mask1625"
  %102 = icmp slt <8 x i32> %"oldMask&test1332", zeroinitializer
  %103 = bitcast <8 x i1> %102 to i8
  %cmp.i3866.not = icmp eq i8 %103, 0
  br i1 %cmp.i3866.not, label %for_exit1325, label %for_loop1323, !llvm.loop !26

for_exit1325:                                     ; preds = %for_step1324
  %notequal_bestIdx_load1634_ = icmp ne <8 x i32> %bestIdx1321.1, splat (i32 -1)
  %notequal_bestIdx_load1634__to_boolvec = sext <8 x i1> %notequal_bestIdx_load1634_ to <8 x i32>
  %104 = bitcast <8 x i1> %notequal_bestIdx_load1634_ to i8
  %cmp.i3869.not = icmp eq i8 %104, 0
  br i1 %cmp.i3869.not, label %safe_if_after_true1637, label %safe_if_run_true1638

for_loop1928:                                     ; preds = %partial_inner_only1843, %for_step1929
  %105 = phi i8 [ %109, %for_step1929 ], [ %149, %partial_inner_only1843 ]
  %"oldMask&test19374570" = phi <8 x i32> [ %"oldMask&test1937", %for_step1929 ], [ %cmp1852_to_boolvec, %partial_inner_only1843 ]
  %dx1934.04569 = phi <8 x i32> [ %dx_load2233_plus1, %for_step1929 ], [ splat (i32 -1), %partial_inner_only1843 ]
  %bestIdx1926.04568 = phi <8 x i32> [ %bestIdx1926.1, %for_step1929 ], [ splat (i32 -1), %partial_inner_only1843 ]
  %bestDistSq1925.04567 = phi <8 x float> [ %bestDistSq1925.1, %for_step1929 ], [ splat (float 0x47EFFFFFE0000000), %partial_inner_only1843 ]
  %add_cell18851945_x_dx_load1947 = add nsw <8 x i32> %dx1934.04569, %blend.i16.i4425.v
  %greaterequal_nx_load1948_GridDimensions11841949_x_broadcast1951.not = icmp ult <8 x i32> %add_cell18851945_x_dx_load1947, %get_element1913_broadcast1914
  %"oldMask&test1953" = select <8 x i1> %greaterequal_nx_load1948_GridDimensions11841949_x_broadcast1951.not, <8 x i32> zeroinitializer, <8 x i32> %"oldMask&test19374570"
  %106 = icmp slt <8 x i32> %"oldMask&test1953", zeroinitializer
  %107 = bitcast <8 x i1> %106 to i8
  %"equal_finished&func1966_internal_mask&function_mask1943" = icmp eq i8 %105, %107
  br i1 %"equal_finished&func1966_internal_mask&function_mask1943", label %for_step1929, label %not_all_continued_or_breaked1968

for_step1929:                                     ; preds = %not_all_continued_or_breaked1968, %for_step1984, %for_loop1928
  %bestDistSq1925.1 = phi <8 x float> [ %bestDistSq1925.04567, %for_loop1928 ], [ %bestDistSq1925.04567, %not_all_continued_or_breaked1968 ], [ %bestDistSq1925.3, %for_step1984 ]
  %bestIdx1926.1 = phi <8 x i32> [ %bestIdx1926.04568, %for_loop1928 ], [ %bestIdx1926.04568, %not_all_continued_or_breaked1968 ], [ %bestIdx1926.3, %for_step1984 ]
  %internal_mask_memory.27 = phi <8 x i32> [ zeroinitializer, %for_loop1928 ], [ %new_mask1979, %not_all_continued_or_breaked1968 ], [ %new_mask1979, %for_step1984 ]
  %"mask|continue_mask2230" = or <8 x i32> %internal_mask_memory.27, %"oldMask&test1953"
  %dx_load2233_plus1 = add nsw <8 x i32> %dx1934.04569, splat (i32 1)
  %lessequal_dx_load1935_.inv = icmp sgt <8 x i32> %dx1934.04569, zeroinitializer
  %"oldMask&test1937" = select <8 x i1> %lessequal_dx_load1935_.inv, <8 x i32> zeroinitializer, <8 x i32> %"mask|continue_mask2230"
  %108 = icmp slt <8 x i32> %"oldMask&test1937", zeroinitializer
  %109 = bitcast <8 x i1> %108 to i8
  %cmp.i3870.not = icmp eq i8 %109, 0
  br i1 %cmp.i3870.not, label %for_exit1930, label %for_loop1928, !llvm.loop !27

for_exit1930:                                     ; preds = %for_step1929, %partial_inner_only1843
  %bestDistSq1925.0.lcssa = phi <8 x float> [ splat (float 0x47EFFFFFE0000000), %partial_inner_only1843 ], [ %bestDistSq1925.1, %for_step1929 ]
  %bestIdx1926.0.lcssa = phi <8 x i32> [ splat (i32 -1), %partial_inner_only1843 ], [ %bestIdx1926.1, %for_step1929 ]
  %notequal_bestIdx_load2239_ = icmp ne <8 x i32> %bestIdx1926.0.lcssa, splat (i32 -1)
  %110 = select <8 x i1> %notequal_bestIdx_load2239_, <8 x i1> %cmp1852, <8 x i1> zeroinitializer
  %111 = bitcast <8 x i1> %110 to i8
  %cmp.i3873.not = icmp eq i8 %111, 0
  br i1 %cmp.i3873.not, label %safe_if_after_true2242, label %safe_if_run_true2243

outer_not_in_extras1226.partial_inner_all_outer1244_crit_edge: ; preds = %if_done1636
  %112 = trunc nsw i64 %indvars.iv.next4661 to i32
  br label %partial_inner_all_outer1244, !llvm.loop !8

partial_inner_all_outer1244:                      ; preds = %outer_not_in_extras1226.partial_inner_all_outer1244_crit_edge, %outer_not_in_extras1226.preheader
  %counter1220.1.lcssa = phi i32 [ %112, %outer_not_in_extras1226.partial_inner_all_outer1244_crit_edge ], [ %__startIndex, %outer_not_in_extras1226.preheader ]
  %before_full_end1845 = icmp slt i32 %counter1220.1.lcssa, %ret.i.i
  br i1 %before_full_end1845, label %partial_inner_only1843, label %common.ret

not_all_continued_or_breaked1363:                 ; preds = %for_loop1323
  %new_mask1374 = xor <8 x i32> %"oldMask&test1348", %"oldMask&test13324536"
  %113 = icmp slt <8 x i32> %new_mask1374, zeroinitializer
  %114 = bitcast <8 x i1> %113 to i8
  %cmp.i3874.not4526 = icmp eq i8 %114, 0
  br i1 %cmp.i3874.not4526, label %for_step1324, label %for_loop1378

for_loop1378:                                     ; preds = %not_all_continued_or_breaked1363, %for_step1379
  %115 = phi i8 [ %120, %for_step1379 ], [ %114, %not_all_continued_or_breaked1363 ]
  %"oldMask&test13874530" = phi <8 x i32> [ %"oldMask&test1387", %for_step1379 ], [ %new_mask1374, %not_all_continued_or_breaked1363 ]
  %dy1384.04529 = phi <8 x i32> [ %dy_load1617_plus1, %for_step1379 ], [ splat (i32 -1), %not_all_continued_or_breaked1363 ]
  %bestIdx1321.24528 = phi <8 x i32> [ %bestIdx1321.3, %for_step1379 ], [ %bestIdx1321.04534, %not_all_continued_or_breaked1363 ]
  %bestDistSq1320.24527 = phi <8 x float> [ %bestDistSq1320.3, %for_step1379 ], [ %bestDistSq1320.04533, %not_all_continued_or_breaked1363 ]
  %add_cell12801395_y_dy_load1397 = add nsw <8 x i32> %dy1384.04529, %blend.i4269.v
  %greaterequal_ny_load1398_GridDimensions11841399_y_broadcast1401.not = icmp ult <8 x i32> %add_cell12801395_y_dy_load1397, %get_element1311_broadcast1312
  %"oldMask&test1403" = select <8 x i1> %greaterequal_ny_load1398_GridDimensions11841399_y_broadcast1401.not, <8 x i32> zeroinitializer, <8 x i32> %"oldMask&test13874530"
  %116 = icmp slt <8 x i32> %"oldMask&test1403", zeroinitializer
  %117 = bitcast <8 x i1> %116 to i8
  %"equal_finished&func1416_internal_mask&function_mask1393" = icmp eq i8 %115, %117
  br i1 %"equal_finished&func1416_internal_mask&function_mask1393", label %for_step1379, label %not_all_continued_or_breaked1418

for_test1498.for_step1379.loopexit_crit_edge:     ; preds = %for_loop1499
  %118 = bitcast <8 x float> %blend.i4286 to <8 x i32>
  br label %for_step1379

for_step1379:                                     ; preds = %not_all_continued_or_breaked1484, %for_test1498.for_step1379.loopexit_crit_edge, %not_all_continued_or_breaked1418, %for_loop1378
  %bestDistSq1320.3 = phi <8 x float> [ %bestDistSq1320.24527, %for_loop1378 ], [ %bestDistSq1320.24527, %not_all_continued_or_breaked1418 ], [ %blend.i.i4282, %for_test1498.for_step1379.loopexit_crit_edge ], [ %bestDistSq1320.24527, %not_all_continued_or_breaked1484 ]
  %bestIdx1321.3 = phi <8 x i32> [ %bestIdx1321.24528, %for_loop1378 ], [ %bestIdx1321.24528, %not_all_continued_or_breaked1418 ], [ %118, %for_test1498.for_step1379.loopexit_crit_edge ], [ %bestIdx1321.24528, %not_all_continued_or_breaked1484 ]
  %continue_lanes_memory1382.1 = phi <8 x i32> [ %"oldMask&test1403", %for_loop1378 ], [ %"mask|continueMask1473", %not_all_continued_or_breaked1418 ], [ %"mask|continueMask1473", %for_test1498.for_step1379.loopexit_crit_edge ], [ %"mask|continueMask1473", %not_all_continued_or_breaked1484 ]
  %internal_mask_memory.21 = phi <8 x i32> [ zeroinitializer, %for_loop1378 ], [ zeroinitializer, %not_all_continued_or_breaked1418 ], [ %new_mask1495, %for_test1498.for_step1379.loopexit_crit_edge ], [ %new_mask1495, %not_all_continued_or_breaked1484 ]
  %"mask|continue_mask1614" = or <8 x i32> %internal_mask_memory.21, %continue_lanes_memory1382.1
  %dy_load1617_plus1 = add nsw <8 x i32> %dy1384.04529, splat (i32 1)
  %lessequal_dy_load1385_.inv = icmp sgt <8 x i32> %dy1384.04529, zeroinitializer
  %"oldMask&test1387" = select <8 x i1> %lessequal_dy_load1385_.inv, <8 x i32> zeroinitializer, <8 x i32> %"mask|continue_mask1614"
  %119 = icmp slt <8 x i32> %"oldMask&test1387", zeroinitializer
  %120 = bitcast <8 x i1> %119 to i8
  %cmp.i3874.not = icmp eq i8 %120, 0
  br i1 %cmp.i3874.not, label %for_step1324, label %for_loop1378, !llvm.loop !28

not_all_continued_or_breaked1418:                 ; preds = %for_loop1378
  %new_mask1429 = xor <8 x i32> %"oldMask&test1403", %"oldMask&test13874530"
  %mul_ny_load1433_GridDimensions11841434_x_broadcast1436 = mul nsw <8 x i32> %add_cell12801395_y_dy_load1397, %get_element1308_broadcast1309
  %add_mul_ny_load1433_GridDimensions11841434_x_broadcast1436_nx_load1437 = add nsw <8 x i32> %mul_ny_load1433_GridDimensions11841434_x_broadcast1436, %add_cell12801340_x_dx_load1342
  %CellStartEnd_load14401441__data = load ptr, ptr %CellStartEnd, align 8
  %mul__cellHash_load1439 = shl nsw <8 x i32> %add_mul_ny_load1433_GridDimensions11841434_x_broadcast1436_nx_load1437, splat (i32 3)
  %v_1.i4270 = tail call <8 x i32> @llvm.x86.avx2.gather.d.d.256(<8 x i32> undef, ptr readonly %CellStartEnd_load14401441__data, <8 x i32> %mul__cellHash_load1439, <8 x i32> %new_mask1429, i8 1)
  %121 = or disjoint <8 x i32> %mul__cellHash_load1439, splat (i32 4)
  %v_1.i4272 = tail call <8 x i32> @llvm.x86.avx2.gather.d.d.256(<8 x i32> undef, ptr readonly %CellStartEnd_load14401441__data, <8 x i32> %121, <8 x i32> %new_mask1429, i8 1)
  %isneg3391 = icmp slt <8 x i32> %v_1.i4270, zeroinitializer
  %"oldMask&test1468" = select <8 x i1> %isneg3391, <8 x i32> %new_mask1429, <8 x i32> zeroinitializer
  %"mask|continueMask1473" = or <8 x i32> %"oldMask&test1468", %"oldMask&test1403"
  %122 = icmp slt <8 x i32> %"mask|continueMask1473", zeroinitializer
  %123 = bitcast <8 x i1> %122 to i8
  %"equal_finished&func1481_internal_mask&function_mask1393" = icmp eq i8 %115, %123
  br i1 %"equal_finished&func1481_internal_mask&function_mask1393", label %for_step1379, label %not_all_continued_or_breaked1484

not_all_continued_or_breaked1484:                 ; preds = %not_all_continued_or_breaked1418
  %new_mask1495 = xor <8 x i32> %"mask|continueMask1473", %"oldMask&test13874530"
  %less_i_load1507_end_load15084518 = icmp slt <8 x i32> %v_1.i4270, %v_1.i4272
  %"oldMask&test15104519" = select <8 x i1> %less_i_load1507_end_load15084518, <8 x i32> %new_mask1495, <8 x i32> zeroinitializer
  %124 = icmp slt <8 x i32> %"oldMask&test15104519", zeroinitializer
  %125 = bitcast <8 x i1> %124 to i8
  %cmp.i3878.not4520 = icmp eq i8 %125, 0
  br i1 %cmp.i3878.not4520, label %for_step1379, label %for_loop1499.lr.ph

for_loop1499.lr.ph:                               ; preds = %not_all_continued_or_breaked1484
  %126 = bitcast <8 x i32> %bestIdx1321.24528 to <8 x float>
  br label %for_loop1499

for_loop1499:                                     ; preds = %for_loop1499.lr.ph, %for_loop1499
  %"oldMask&test15104524" = phi <8 x i32> [ %"oldMask&test15104519", %for_loop1499.lr.ph ], [ %"oldMask&test1510", %for_loop1499 ]
  %i1505.04523 = phi <8 x i32> [ %v_1.i4270, %for_loop1499.lr.ph ], [ %i_load1606_plus1, %for_loop1499 ]
  %bestIdx1321.44522 = phi <8 x float> [ %126, %for_loop1499.lr.ph ], [ %blend.i4286, %for_loop1499 ]
  %bestDistSq1320.44521 = phi <8 x float> [ %bestDistSq1320.24527, %for_loop1499.lr.ph ], [ %blend.i.i4282, %for_loop1499 ]
  %mul__i_load1518 = shl nsw <8 x i32> %i1505.04523, splat (i32 3)
  %mask.i4274 = bitcast <8 x i32> %"oldMask&test15104524" to <8 x float>
  %v_1.i4275 = tail call <8 x float> @llvm.x86.avx2.gather.d.ps.256(<8 x float> undef, ptr readonly %SortedPositions_ptr, <8 x i32> %mul__i_load1518, <8 x float> %mask.i4274, i8 1)
  %127 = or disjoint <8 x i32> %mul__i_load1518, splat (i32 4)
  %v_1.i4278 = tail call <8 x float> @llvm.x86.avx2.gather.d.ps.256(<8 x float> undef, ptr readonly %SortedPositions_ptr, <8 x i32> %127, <8 x float> %mask.i4274, i8 1)
  %sub_a14_x_b26_x.i.i4112 = fsub <8 x float> %v_1.i4275, %v_1.i4250
  %mul_a13_x_b25_x.i.i.i4121 = fmul <8 x float> %sub_a14_x_b26_x.i.i4112, %sub_a14_x_b26_x.i.i4112
  %sub_a19_y_b211_y.i.i4113 = fsub <8 x float> %v_1.i4278, %v_1.i4252
  %mul_a17_y_b29_y.i.i.i4122 = fmul <8 x float> %sub_a19_y_b211_y.i.i4113, %sub_a19_y_b211_y.i.i4113
  %add_mul_a13_x_b25_x_mul_a17_y_b29_y.i.i.i4123 = fadd <8 x float> %mul_a13_x_b25_x.i.i.i4121, %mul_a17_y_b29_y.i.i.i4122
  %less_distSq_load1578_bestDistSq_load1579 = fcmp olt <8 x float> %add_mul_a13_x_b25_x_mul_a17_y_b29_y.i.i.i4123, %bestDistSq1320.44521
  %128 = bitcast <8 x i32> %"oldMask&test15104524" to <8 x float>
  %mask_as_float.i.i4280 = select <8 x i1> %less_distSq_load1578_bestDistSq_load1579, <8 x float> %128, <8 x float> zeroinitializer
  %blend.i.i4282 = call <8 x float> @llvm.x86.avx.blendv.ps.256(<8 x float> %bestDistSq1320.44521, <8 x float> %add_mul_a13_x_b25_x_mul_a17_y_b29_y.i.i.i4123, <8 x float> %mask_as_float.i.i4280)
  %newAsFloat.i4285 = bitcast <8 x i32> %i1505.04523 to <8 x float>
  %blend.i4286 = call <8 x float> @llvm.x86.avx.blendv.ps.256(<8 x float> %bestIdx1321.44522, <8 x float> %newAsFloat.i4285, <8 x float> %mask_as_float.i.i4280)
  %i_load1606_plus1 = add nsw <8 x i32> %i1505.04523, splat (i32 1)
  %less_i_load1507_end_load1508 = icmp slt <8 x i32> %i_load1606_plus1, %v_1.i4272
  %"oldMask&test1510" = select <8 x i1> %less_i_load1507_end_load1508, <8 x i32> %"oldMask&test15104524", <8 x i32> zeroinitializer
  %129 = icmp slt <8 x i32> %"oldMask&test1510", zeroinitializer
  %130 = bitcast <8 x i1> %129 to i8
  %cmp.i3878.not = icmp eq i8 %130, 0
  br i1 %cmp.i3878.not, label %for_test1498.for_step1379.loopexit_crit_edge, label %for_loop1499, !llvm.loop !29

if_done1636:                                      ; preds = %for_exit1670, %safe_if_run_true1785, %safe_if_after_true1637
  %indvars.iv.next4661 = add nsw i64 %indvars.iv4660, 8
  %before_aligned_end1246 = icmp slt i64 %indvars.iv.next4661, %3
  br i1 %before_aligned_end1246, label %foreach_full_body1200, label %outer_not_in_extras1226.partial_inner_all_outer1244_crit_edge, !llvm.loop !8

safe_if_after_true1637:                           ; preds = %safe_if_run_true1638, %for_exit1325
  %"~test1661" = xor <8 x i32> %notequal_bestIdx_load1634__to_boolvec, splat (i32 -1)
  %131 = xor <8 x i1> %notequal_bestIdx_load1634_, splat (i1 true)
  %132 = bitcast <8 x i1> %131 to i8
  %cmp.i3880.not = icmp eq i8 %132, 0
  br i1 %cmp.i3880.not, label %if_done1636, label %for_test1667.preheader

for_test1667.preheader:                           ; preds = %safe_if_after_true1637
  %"oldMask&test16794538" = select <8 x i1> %less_i_load1675_SortedLength_load1676_broadcast16774537, <8 x i32> %"~test1661", <8 x i32> zeroinitializer
  %133 = icmp slt <8 x i32> %"oldMask&test16794538", zeroinitializer
  %134 = bitcast <8 x i1> %133 to i8
  %cmp.i3881.not4539 = icmp eq i8 %134, 0
  br i1 %cmp.i3881.not4539, label %for_exit1670, label %not_all_continued_or_breaked1733.lr.ph

not_all_continued_or_breaked1733.lr.ph:           ; preds = %for_test1667.preheader
  %135 = bitcast <8 x i32> %bestIdx1321.1 to <8 x float>
  br label %not_all_continued_or_breaked1733

safe_if_run_true1638:                             ; preds = %for_exit1325
  %mul__bestIdx_load1647 = shl nsw <8 x i32> %bestIdx1321.1, splat (i32 3)
  %136 = or disjoint <8 x i32> %mul__bestIdx_load1647, splat (i32 4)
  %new_add3608 = sext <8 x i32> %136 to <8 x i64>
  %vecmask_1.i4287 = shufflevector <8 x i32> %notequal_bestIdx_load1634__to_boolvec, <8 x i32> poison, <4 x i32> <i32 0, i32 1, i32 2, i32 3>
  %vecmask_2.i4288 = shufflevector <8 x i32> %notequal_bestIdx_load1634__to_boolvec, <8 x i32> poison, <4 x i32> <i32 4, i32 5, i32 6, i32 7>
  %offsets_1.i4289 = shufflevector <8 x i64> %new_add3608, <8 x i64> poison, <4 x i32> <i32 0, i32 1, i32 2, i32 3>
  %offsets_2.i4290 = shufflevector <8 x i64> %new_add3608, <8 x i64> poison, <4 x i32> <i32 4, i32 5, i32 6, i32 7>
  %v1_1.i4291 = tail call <4 x i32> @llvm.x86.avx2.gather.q.d.256(<4 x i32> undef, ptr %HashIndex_ptr, <4 x i64> %offsets_1.i4289, <4 x i32> %vecmask_1.i4287, i8 1)
  %v2_1.i4292 = tail call <4 x i32> @llvm.x86.avx2.gather.q.d.256(<4 x i32> undef, ptr %HashIndex_ptr, <4 x i64> %offsets_2.i4290, <4 x i32> %vecmask_2.i4288, i8 1)
  %v.i4293 = shufflevector <4 x i32> %v1_1.i4291, <4 x i32> %v2_1.i4292, <8 x i32> <i32 0, i32 1, i32 2, i32 3, i32 4, i32 5, i32 6, i32 7>
  %val.i4294 = bitcast <8 x i32> %v.i4293 to <8 x float>
  call void @llvm.masked.store.v8f32.p0(<8 x float> %val.i4294, ptr %ptr3560, i32 1, <8 x i1> %notequal_bestIdx_load1634_)
  br label %safe_if_after_true1637

for_test1667.for_exit1670_crit_edge:              ; preds = %not_all_continued_or_breaked1733
  %137 = bitcast <8 x float> %blend.i4301 to <8 x i32>
  br label %for_exit1670

for_exit1670:                                     ; preds = %for_test1667.for_exit1670_crit_edge, %for_test1667.preheader
  %bestIdx1321.5.lcssa = phi <8 x i32> [ %137, %for_test1667.for_exit1670_crit_edge ], [ %bestIdx1321.1, %for_test1667.preheader ]
  %notequal_bestIdx_load1781_ = icmp eq <8 x i32> %bestIdx1321.5.lcssa, splat (i32 -1)
  %"oldMask&test1786" = select <8 x i1> %notequal_bestIdx_load1781_, <8 x i32> zeroinitializer, <8 x i32> %"~test1661"
  %138 = icmp slt <8 x i32> %"oldMask&test1786", zeroinitializer
  %139 = bitcast <8 x i1> %138 to i8
  %cmp.i3883.not = icmp eq i8 %139, 0
  br i1 %cmp.i3883.not, label %if_done1636, label %safe_if_run_true1785

not_all_continued_or_breaked1733:                 ; preds = %not_all_continued_or_breaked1733.lr.ph, %not_all_continued_or_breaked1733
  %indvars.iv = phi i64 [ 0, %not_all_continued_or_breaked1733.lr.ph ], [ %indvars.iv.next, %not_all_continued_or_breaked1733 ]
  %"oldMask&test16794545" = phi <8 x i32> [ %"oldMask&test16794538", %not_all_continued_or_breaked1733.lr.ph ], [ %"oldMask&test1679", %not_all_continued_or_breaked1733 ]
  %i1674.04544 = phi <8 x i32> [ zeroinitializer, %not_all_continued_or_breaked1733.lr.ph ], [ %i_load1775_plus1, %not_all_continued_or_breaked1733 ]
  %bestIdx1321.54541 = phi <8 x float> [ %135, %not_all_continued_or_breaked1733.lr.ph ], [ %blend.i4301, %not_all_continued_or_breaked1733 ]
  %bestDistSq1320.54540 = phi <8 x float> [ %bestDistSq1320.1, %not_all_continued_or_breaked1733.lr.ph ], [ %blend.i.i4297, %not_all_continued_or_breaked1733 ]
  %140 = shl nsw i64 %indvars.iv, 3
  %ptr3621 = getelementptr i8, ptr %SortedPositions_ptr, i64 %140, !filename !11, !first_line !19, !first_column !20, !last_line !19, !last_column !21
  %SortedPositions_ptr_load1688_offset_load36203622 = load float, ptr %ptr3621, align 4
  %SortedPositions_ptr_load1688_offset_load36203623 = insertelement <8 x float> poison, float %SortedPositions_ptr_load1688_offset_load36203622, i64 0
  %SortedPositions_ptr_load1688_offset_load36203624 = shufflevector <8 x float> %SortedPositions_ptr_load1688_offset_load36203623, <8 x float> poison, <8 x i32> zeroinitializer, !filename !11, !first_line !19, !first_column !20, !last_line !19, !last_column !21
  %sub_a14_x_b26_x.i.i4124 = fsub <8 x float> %SortedPositions_ptr_load1688_offset_load36203624, %v_1.i4250
  %mul_a13_x_b25_x.i.i.i4133 = fmul <8 x float> %sub_a14_x_b26_x.i.i4124, %sub_a14_x_b26_x.i.i4124
  %gep = getelementptr i8, ptr %invariant.gep, i64 %140
  %SortedPositions_ptr_load1688_offset_load170336303635 = load float, ptr %gep, align 4
  %SortedPositions_ptr_load1688_offset_load170336303636 = insertelement <8 x float> poison, float %SortedPositions_ptr_load1688_offset_load170336303635, i64 0
  %SortedPositions_ptr_load1688_offset_load170336303637 = shufflevector <8 x float> %SortedPositions_ptr_load1688_offset_load170336303636, <8 x float> poison, <8 x i32> zeroinitializer, !filename !11, !first_line !19, !first_column !20, !last_line !19, !last_column !21
  %sub_a19_y_b211_y.i.i4125 = fsub <8 x float> %SortedPositions_ptr_load1688_offset_load170336303637, %v_1.i4252
  %mul_a17_y_b29_y.i.i.i4134 = fmul <8 x float> %sub_a19_y_b211_y.i.i4125, %sub_a19_y_b211_y.i.i4125
  %add_mul_a13_x_b25_x_mul_a17_y_b29_y.i.i.i4135 = fadd <8 x float> %mul_a13_x_b25_x.i.i.i4133, %mul_a17_y_b29_y.i.i.i4134
  %less_distSq_load1747_bestDistSq_load1748 = fcmp olt <8 x float> %add_mul_a13_x_b25_x_mul_a17_y_b29_y.i.i.i4135, %bestDistSq1320.54540
  %141 = bitcast <8 x i32> %"oldMask&test16794545" to <8 x float>
  %mask_as_float.i.i4295 = select <8 x i1> %less_distSq_load1747_bestDistSq_load1748, <8 x float> %141, <8 x float> zeroinitializer
  %blend.i.i4297 = call <8 x float> @llvm.x86.avx.blendv.ps.256(<8 x float> %bestDistSq1320.54540, <8 x float> %add_mul_a13_x_b25_x_mul_a17_y_b29_y.i.i.i4135, <8 x float> %mask_as_float.i.i4295)
  %newAsFloat.i4300 = bitcast <8 x i32> %i1674.04544 to <8 x float>
  %blend.i4301 = call <8 x float> @llvm.x86.avx.blendv.ps.256(<8 x float> %bestIdx1321.54541, <8 x float> %newAsFloat.i4300, <8 x float> %mask_as_float.i.i4295)
  %indvars.iv.next = add nuw nsw i64 %indvars.iv, 1
  %i_load1775_plus1 = add nuw nsw <8 x i32> %i1674.04544, splat (i32 1)
  %less_i_load1675_SortedLength_load1676_broadcast1677 = icmp slt <8 x i32> %i_load1775_plus1, %SortedLength_load1676_broadcast1677
  %"oldMask&test1679" = select <8 x i1> %less_i_load1675_SortedLength_load1676_broadcast1677, <8 x i32> %"oldMask&test16794545", <8 x i32> zeroinitializer
  %142 = icmp slt <8 x i32> %"oldMask&test1679", zeroinitializer
  %143 = bitcast <8 x i1> %142 to i8
  %cmp.i3881.not = icmp eq i8 %143, 0
  br i1 %cmp.i3881.not, label %for_test1667.for_exit1670_crit_edge, label %not_all_continued_or_breaked1733, !llvm.loop !30

safe_if_run_true1785:                             ; preds = %for_exit1670
  %mul__bestIdx_load1794 = shl nsw <8 x i32> %bestIdx1321.5.lcssa, splat (i32 3)
  %144 = or disjoint <8 x i32> %mul__bestIdx_load1794, splat (i32 4)
  %new_add3642 = sext <8 x i32> %144 to <8 x i64>
  %vecmask_1.i4302 = shufflevector <8 x i32> %"oldMask&test1786", <8 x i32> poison, <4 x i32> <i32 0, i32 1, i32 2, i32 3>
  %vecmask_2.i4303 = shufflevector <8 x i32> %"oldMask&test1786", <8 x i32> poison, <4 x i32> <i32 4, i32 5, i32 6, i32 7>
  %offsets_1.i4304 = shufflevector <8 x i64> %new_add3642, <8 x i64> poison, <4 x i32> <i32 0, i32 1, i32 2, i32 3>
  %offsets_2.i4305 = shufflevector <8 x i64> %new_add3642, <8 x i64> poison, <4 x i32> <i32 4, i32 5, i32 6, i32 7>
  %v1_1.i4306 = tail call <4 x i32> @llvm.x86.avx2.gather.q.d.256(<4 x i32> undef, ptr %HashIndex_ptr, <4 x i64> %offsets_1.i4304, <4 x i32> %vecmask_1.i4302, i8 1)
  %v2_1.i4307 = tail call <4 x i32> @llvm.x86.avx2.gather.q.d.256(<4 x i32> undef, ptr %HashIndex_ptr, <4 x i64> %offsets_2.i4305, <4 x i32> %vecmask_2.i4303, i8 1)
  %v.i4308 = shufflevector <4 x i32> %v1_1.i4306, <4 x i32> %v2_1.i4307, <8 x i32> <i32 0, i32 1, i32 2, i32 3, i32 4, i32 5, i32 6, i32 7>
  %val.i4309 = bitcast <8 x i32> %v.i4308 to <8 x float>
  call void @llvm.x86.avx.maskstore.ps.256(ptr nonnull %ptr3560, <8 x i32> %"oldMask&test1786", <8 x float> %val.i4309)
  br label %if_done1636

partial_inner_only1843:                           ; preds = %partial_inner_all_outer1244
  %smear_counter_init1847 = insertelement <8 x i32> poison, i32 %counter1220.1.lcssa, i64 0
  %smear_counter1848 = shufflevector <8 x i32> %smear_counter_init1847, <8 x i32> poison, <8 x i32> zeroinitializer
  %iter_val1849 = add nsw <8 x i32> %smear_counter1848, <i32 0, i32 1, i32 2, i32 3, i32 4, i32 5, i32 6, i32 7>
  %smear_end_init1850 = insertelement <8 x i32> poison, i32 %ret.i.i, i64 0
  %smear_end1851 = shufflevector <8 x i32> %smear_end_init1850, <8 x i32> poison, <8 x i32> zeroinitializer
  %cmp1852 = icmp slt <8 x i32> %iter_val1849, %smear_end1851
  %cmp1852_to_boolvec = sext <8 x i1> %cmp1852 to <8 x i32>
  %mul__index_load1859.elt0 = shl nsw i32 %counter1220.1.lcssa, 2
  %145 = sext i32 %mul__index_load1859.elt0 to i64
  %ptr3576 = getelementptr i8, ptr %Results_ptr, i64 %145
  call void @llvm.masked.store.v8f32.p0(<8 x float> splat (float 0xFFFFFFFFE0000000), ptr %ptr3576, i32 1, <8 x i1> %cmp1852)
  %mul__index_load1867 = shl nsw <8 x i32> %iter_val1849, splat (i32 3)
  %mask.i4310 = bitcast <8 x i32> %cmp1852_to_boolvec to <8 x float>
  %v_1.i4311 = tail call <8 x float> @llvm.x86.avx2.gather.d.ps.256(<8 x float> undef, ptr readonly %QueryPositions_ptr, <8 x i32> %mul__index_load1867, <8 x float> %mask.i4310, i8 1)
  %146 = or disjoint <8 x i32> %mul__index_load1867, splat (i32 4)
  %v_1.i4314 = tail call <8 x float> @llvm.x86.avx2.gather.d.ps.256(<8 x float> undef, ptr readonly %QueryPositions_ptr, <8 x i32> %146, <8 x float> %mask.i4310, i8 1)
  %get_element1888_broadcast = insertelement <8 x float> poison, float %GridOrigin_ptr_load_load.unpack, i64 0
  %get_element1888_broadcast1889 = shufflevector <8 x float> %get_element1888_broadcast, <8 x float> poison, <8 x i32> zeroinitializer
  %get_element1891_broadcast = insertelement <8 x float> poison, float %GridOrigin_ptr_load_load.unpack3386, i64 0
  %get_element1891_broadcast1892 = shufflevector <8 x float> %get_element1891_broadcast, <8 x float> poison, <8 x i32> zeroinitializer
  %sub_a14_x_b26_x.i4073 = fsub <8 x float> %v_1.i4311, %get_element1888_broadcast1889
  %sub_a19_y_b211_y.i4074 = fsub <8 x float> %v_1.i4314, %get_element1891_broadcast1892
  %GridResolutionInv_load1897_broadcast = insertelement <8 x float> poison, float %GridResolutionInv_ptr_load_load, i64 0
  %GridResolutionInv_load1897_broadcast1898 = shufflevector <8 x float> %GridResolutionInv_load1897_broadcast, <8 x float> poison, <8 x i32> zeroinitializer
  %mul_v14_x_s_load.i4080 = fmul <8 x float> %GridResolutionInv_load1897_broadcast1898, %sub_a14_x_b26_x.i4073
  %mul_v17_y_s_load9.i4081 = fmul <8 x float> %GridResolutionInv_load1897_broadcast1898, %sub_a19_y_b211_y.i4074
  %call.i.i.i4087 = tail call <8 x float> @llvm.x86.avx.round.ps.256(<8 x float> %mul_v14_x_s_load.i4080, i32 9)
  %call.i.i3.i4088 = tail call <8 x float> @llvm.x86.avx.round.ps.256(<8 x float> %mul_v17_y_s_load9.i4081, i32 9)
  %v12_x_to_int32.i4094 = fptosi <8 x float> %call.i.i.i4087 to <8 x i32>
  %v14_y_to_int32.i4095 = fptosi <8 x float> %call.i.i3.i4088 to <8 x i32>
  %get_element1913_broadcast = insertelement <8 x i32> poison, i32 %GridDimensions_ptr_load_load.unpack, i64 0
  %get_element1913_broadcast1914 = shufflevector <8 x i32> %get_element1913_broadcast, <8 x i32> poison, <8 x i32> zeroinitializer
  %get_element1916_broadcast = insertelement <8 x i32> poison, i32 %GridDimensions_ptr_load_load.unpack3389, i64 0
  %get_element1916_broadcast1917 = shufflevector <8 x i32> %get_element1916_broadcast, <8 x i32> poison, <8 x i32> zeroinitializer
  %sub_a14_x_b_load.i4105 = add nsw <8 x i32> %get_element1913_broadcast1914, splat (i32 -1)
  %sub_a17_y_b_load9.i4106 = add nsw <8 x i32> %get_element1916_broadcast1917, splat (i32 -1)
  %147 = call <8 x i32> @llvm.smax.v8i32(<8 x i32> %v12_x_to_int32.i4094, <8 x i32> zeroinitializer)
  %148 = call <8 x i32> @llvm.smax.v8i32(<8 x i32> %v14_y_to_int32.i4095, <8 x i32> zeroinitializer)
  %blend.i16.i4425.v = call <8 x i32> @llvm.smin.v8i32(<8 x i32> %147, <8 x i32> %sub_a14_x_b_load.i4105)
  %blend.i20.i4430.v = call <8 x i32> @llvm.smin.v8i32(<8 x i32> %148, <8 x i32> %sub_a17_y_b_load9.i4106)
  %149 = bitcast <8 x i1> %cmp1852 to i8
  %cmp.i3870.not4566 = icmp eq i8 %149, 0
  br i1 %cmp.i3870.not4566, label %for_exit1930, label %for_loop1928

not_all_continued_or_breaked1968:                 ; preds = %for_loop1928
  %new_mask1979 = xor <8 x i32> %"oldMask&test1953", %"oldMask&test19374570"
  %150 = icmp slt <8 x i32> %new_mask1979, zeroinitializer
  %151 = bitcast <8 x i1> %150 to i8
  %cmp.i3885.not4559 = icmp eq i8 %151, 0
  br i1 %cmp.i3885.not4559, label %for_step1929, label %for_loop1983

for_loop1983:                                     ; preds = %not_all_continued_or_breaked1968, %for_step1984
  %152 = phi i8 [ %157, %for_step1984 ], [ %151, %not_all_continued_or_breaked1968 ]
  %"oldMask&test19924563" = phi <8 x i32> [ %"oldMask&test1992", %for_step1984 ], [ %new_mask1979, %not_all_continued_or_breaked1968 ]
  %dy1989.04562 = phi <8 x i32> [ %dy_load2222_plus1, %for_step1984 ], [ splat (i32 -1), %not_all_continued_or_breaked1968 ]
  %bestIdx1926.24561 = phi <8 x i32> [ %bestIdx1926.3, %for_step1984 ], [ %bestIdx1926.04568, %not_all_continued_or_breaked1968 ]
  %bestDistSq1925.24560 = phi <8 x float> [ %bestDistSq1925.3, %for_step1984 ], [ %bestDistSq1925.04567, %not_all_continued_or_breaked1968 ]
  %add_cell18852000_y_dy_load2002 = add nsw <8 x i32> %dy1989.04562, %blend.i20.i4430.v
  %greaterequal_ny_load2003_GridDimensions11842004_y_broadcast2006.not = icmp ult <8 x i32> %add_cell18852000_y_dy_load2002, %get_element1916_broadcast1917
  %"oldMask&test2008" = select <8 x i1> %greaterequal_ny_load2003_GridDimensions11842004_y_broadcast2006.not, <8 x i32> zeroinitializer, <8 x i32> %"oldMask&test19924563"
  %153 = icmp slt <8 x i32> %"oldMask&test2008", zeroinitializer
  %154 = bitcast <8 x i1> %153 to i8
  %"equal_finished&func2021_internal_mask&function_mask1998" = icmp eq i8 %152, %154
  br i1 %"equal_finished&func2021_internal_mask&function_mask1998", label %for_step1984, label %not_all_continued_or_breaked2023

for_test2103.for_step1984.loopexit_crit_edge:     ; preds = %for_loop2104
  %155 = bitcast <8 x float> %blend.i4332 to <8 x i32>
  br label %for_step1984

for_step1984:                                     ; preds = %not_all_continued_or_breaked2089, %for_test2103.for_step1984.loopexit_crit_edge, %not_all_continued_or_breaked2023, %for_loop1983
  %bestDistSq1925.3 = phi <8 x float> [ %bestDistSq1925.24560, %for_loop1983 ], [ %bestDistSq1925.24560, %not_all_continued_or_breaked2023 ], [ %blend.i.i4328, %for_test2103.for_step1984.loopexit_crit_edge ], [ %bestDistSq1925.24560, %not_all_continued_or_breaked2089 ]
  %bestIdx1926.3 = phi <8 x i32> [ %bestIdx1926.24561, %for_loop1983 ], [ %bestIdx1926.24561, %not_all_continued_or_breaked2023 ], [ %155, %for_test2103.for_step1984.loopexit_crit_edge ], [ %bestIdx1926.24561, %not_all_continued_or_breaked2089 ]
  %continue_lanes_memory1987.1 = phi <8 x i32> [ %"oldMask&test2008", %for_loop1983 ], [ %"mask|continueMask2078", %not_all_continued_or_breaked2023 ], [ %"mask|continueMask2078", %for_test2103.for_step1984.loopexit_crit_edge ], [ %"mask|continueMask2078", %not_all_continued_or_breaked2089 ]
  %internal_mask_memory.29 = phi <8 x i32> [ zeroinitializer, %for_loop1983 ], [ zeroinitializer, %not_all_continued_or_breaked2023 ], [ %new_mask2100, %for_test2103.for_step1984.loopexit_crit_edge ], [ %new_mask2100, %not_all_continued_or_breaked2089 ]
  %"mask|continue_mask2219" = or <8 x i32> %internal_mask_memory.29, %continue_lanes_memory1987.1
  %dy_load2222_plus1 = add nsw <8 x i32> %dy1989.04562, splat (i32 1)
  %lessequal_dy_load1990_.inv = icmp sgt <8 x i32> %dy1989.04562, zeroinitializer
  %"oldMask&test1992" = select <8 x i1> %lessequal_dy_load1990_.inv, <8 x i32> zeroinitializer, <8 x i32> %"mask|continue_mask2219"
  %156 = icmp slt <8 x i32> %"oldMask&test1992", zeroinitializer
  %157 = bitcast <8 x i1> %156 to i8
  %cmp.i3885.not = icmp eq i8 %157, 0
  br i1 %cmp.i3885.not, label %for_step1929, label %for_loop1983, !llvm.loop !31

not_all_continued_or_breaked2023:                 ; preds = %for_loop1983
  %new_mask2034 = xor <8 x i32> %"oldMask&test2008", %"oldMask&test19924563"
  %mul_ny_load2038_GridDimensions11842039_x_broadcast2041 = mul nsw <8 x i32> %add_cell18852000_y_dy_load2002, %get_element1913_broadcast1914
  %add_mul_ny_load2038_GridDimensions11842039_x_broadcast2041_nx_load2042 = add nsw <8 x i32> %mul_ny_load2038_GridDimensions11842039_x_broadcast2041, %add_cell18851945_x_dx_load1947
  %CellStartEnd_load20452046__data = load ptr, ptr %CellStartEnd, align 8
  %mul__cellHash_load2044 = shl nsw <8 x i32> %add_mul_ny_load2038_GridDimensions11842039_x_broadcast2041_nx_load2042, splat (i32 3)
  %v_1.i4316 = tail call <8 x i32> @llvm.x86.avx2.gather.d.d.256(<8 x i32> undef, ptr readonly %CellStartEnd_load20452046__data, <8 x i32> %mul__cellHash_load2044, <8 x i32> %new_mask2034, i8 1)
  %158 = or disjoint <8 x i32> %mul__cellHash_load2044, splat (i32 4)
  %v_1.i4318 = tail call <8 x i32> @llvm.x86.avx2.gather.d.d.256(<8 x i32> undef, ptr readonly %CellStartEnd_load20452046__data, <8 x i32> %158, <8 x i32> %new_mask2034, i8 1)
  %isneg = icmp slt <8 x i32> %v_1.i4316, zeroinitializer
  %"oldMask&test2073" = select <8 x i1> %isneg, <8 x i32> %new_mask2034, <8 x i32> zeroinitializer
  %"mask|continueMask2078" = or <8 x i32> %"oldMask&test2073", %"oldMask&test2008"
  %159 = icmp slt <8 x i32> %"mask|continueMask2078", zeroinitializer
  %160 = bitcast <8 x i1> %159 to i8
  %"equal_finished&func2086_internal_mask&function_mask1998" = icmp eq i8 %152, %160
  br i1 %"equal_finished&func2086_internal_mask&function_mask1998", label %for_step1984, label %not_all_continued_or_breaked2089

not_all_continued_or_breaked2089:                 ; preds = %not_all_continued_or_breaked2023
  %new_mask2100 = xor <8 x i32> %"mask|continueMask2078", %"oldMask&test19924563"
  %less_i_load2112_end_load21134550 = icmp slt <8 x i32> %v_1.i4316, %v_1.i4318
  %"oldMask&test21154551" = select <8 x i1> %less_i_load2112_end_load21134550, <8 x i32> %new_mask2100, <8 x i32> zeroinitializer
  %161 = icmp slt <8 x i32> %"oldMask&test21154551", zeroinitializer
  %162 = bitcast <8 x i1> %161 to i8
  %cmp.i3889.not4552 = icmp eq i8 %162, 0
  br i1 %cmp.i3889.not4552, label %for_step1984, label %for_loop2104.lr.ph

for_loop2104.lr.ph:                               ; preds = %not_all_continued_or_breaked2089
  %163 = bitcast <8 x i32> %bestIdx1926.24561 to <8 x float>
  br label %for_loop2104

for_loop2104:                                     ; preds = %for_loop2104.lr.ph, %for_loop2104
  %"oldMask&test21154556" = phi <8 x i32> [ %"oldMask&test21154551", %for_loop2104.lr.ph ], [ %"oldMask&test2115", %for_loop2104 ]
  %i2110.04555 = phi <8 x i32> [ %v_1.i4316, %for_loop2104.lr.ph ], [ %i_load2211_plus1, %for_loop2104 ]
  %bestIdx1926.44554 = phi <8 x float> [ %163, %for_loop2104.lr.ph ], [ %blend.i4332, %for_loop2104 ]
  %bestDistSq1925.44553 = phi <8 x float> [ %bestDistSq1925.24560, %for_loop2104.lr.ph ], [ %blend.i.i4328, %for_loop2104 ]
  %mul__i_load2123 = shl nsw <8 x i32> %i2110.04555, splat (i32 3)
  %mask.i4320 = bitcast <8 x i32> %"oldMask&test21154556" to <8 x float>
  %v_1.i4321 = tail call <8 x float> @llvm.x86.avx2.gather.d.ps.256(<8 x float> undef, ptr readonly %SortedPositions_ptr, <8 x i32> %mul__i_load2123, <8 x float> %mask.i4320, i8 1)
  %164 = or disjoint <8 x i32> %mul__i_load2123, splat (i32 4)
  %v_1.i4324 = tail call <8 x float> @llvm.x86.avx2.gather.d.ps.256(<8 x float> undef, ptr readonly %SortedPositions_ptr, <8 x i32> %164, <8 x float> %mask.i4320, i8 1)
  %sub_a14_x_b26_x.i.i4136 = fsub <8 x float> %v_1.i4321, %v_1.i4311
  %mul_a13_x_b25_x.i.i.i4145 = fmul <8 x float> %sub_a14_x_b26_x.i.i4136, %sub_a14_x_b26_x.i.i4136
  %sub_a19_y_b211_y.i.i4137 = fsub <8 x float> %v_1.i4324, %v_1.i4314
  %mul_a17_y_b29_y.i.i.i4146 = fmul <8 x float> %sub_a19_y_b211_y.i.i4137, %sub_a19_y_b211_y.i.i4137
  %add_mul_a13_x_b25_x_mul_a17_y_b29_y.i.i.i4147 = fadd <8 x float> %mul_a13_x_b25_x.i.i.i4145, %mul_a17_y_b29_y.i.i.i4146
  %less_distSq_load2183_bestDistSq_load2184 = fcmp olt <8 x float> %add_mul_a13_x_b25_x_mul_a17_y_b29_y.i.i.i4147, %bestDistSq1925.44553
  %165 = bitcast <8 x i32> %"oldMask&test21154556" to <8 x float>
  %mask_as_float.i.i4326 = select <8 x i1> %less_distSq_load2183_bestDistSq_load2184, <8 x float> %165, <8 x float> zeroinitializer
  %blend.i.i4328 = call <8 x float> @llvm.x86.avx.blendv.ps.256(<8 x float> %bestDistSq1925.44553, <8 x float> %add_mul_a13_x_b25_x_mul_a17_y_b29_y.i.i.i4147, <8 x float> %mask_as_float.i.i4326)
  %newAsFloat.i4331 = bitcast <8 x i32> %i2110.04555 to <8 x float>
  %blend.i4332 = call <8 x float> @llvm.x86.avx.blendv.ps.256(<8 x float> %bestIdx1926.44554, <8 x float> %newAsFloat.i4331, <8 x float> %mask_as_float.i.i4326)
  %i_load2211_plus1 = add nsw <8 x i32> %i2110.04555, splat (i32 1)
  %less_i_load2112_end_load2113 = icmp slt <8 x i32> %i_load2211_plus1, %v_1.i4318
  %"oldMask&test2115" = select <8 x i1> %less_i_load2112_end_load2113, <8 x i32> %"oldMask&test21154556", <8 x i32> zeroinitializer
  %166 = icmp slt <8 x i32> %"oldMask&test2115", zeroinitializer
  %167 = bitcast <8 x i1> %166 to i8
  %cmp.i3889.not = icmp eq i8 %167, 0
  br i1 %cmp.i3889.not, label %for_test2103.for_step1984.loopexit_crit_edge, label %for_loop2104, !llvm.loop !32

safe_if_after_true2242:                           ; preds = %safe_if_run_true2243, %for_exit1930
  %"oldMask&~test2267" = select <8 x i1> %notequal_bestIdx_load2239_, <8 x i32> zeroinitializer, <8 x i32> %cmp1852_to_boolvec
  %not.notequal_bestIdx_load2239_ = xor <8 x i1> %notequal_bestIdx_load2239_, splat (i1 true)
  %168 = select <8 x i1> %not.notequal_bestIdx_load2239_, <8 x i1> %cmp1852, <8 x i1> zeroinitializer
  %169 = bitcast <8 x i1> %168 to i8
  %cmp.i3891.not = icmp eq i8 %169, 0
  br i1 %cmp.i3891.not, label %common.ret, label %for_test2272.preheader

for_test2272.preheader:                           ; preds = %safe_if_after_true2242
  %SortedLength_load2281_broadcast = insertelement <8 x i32> poison, i32 %SortedLength_ptr_load_load, i64 0
  %SortedLength_load2281_broadcast2282 = shufflevector <8 x i32> %SortedLength_load2281_broadcast, <8 x i32> poison, <8 x i32> zeroinitializer
  %less_i_load2280_SortedLength_load2281_broadcast22824573 = icmp sgt <8 x i32> %SortedLength_load2281_broadcast2282, zeroinitializer
  %"oldMask&test22844574" = select <8 x i1> %less_i_load2280_SortedLength_load2281_broadcast22824573, <8 x i32> %"oldMask&~test2267", <8 x i32> zeroinitializer
  %170 = icmp slt <8 x i32> %"oldMask&test22844574", zeroinitializer
  %171 = bitcast <8 x i1> %170 to i8
  %cmp.i3892.not4575 = icmp eq i8 %171, 0
  br i1 %cmp.i3892.not4575, label %for_exit2275, label %not_all_continued_or_breaked2338.lr.ph

not_all_continued_or_breaked2338.lr.ph:           ; preds = %for_test2272.preheader
  %invariant.gep4583 = getelementptr i8, ptr %SortedPositions_ptr, i64 4
  %172 = bitcast <8 x i32> %bestIdx1926.0.lcssa to <8 x float>
  br label %not_all_continued_or_breaked2338

safe_if_run_true2243:                             ; preds = %for_exit1930
  %"oldMask&test2244" = select <8 x i1> %notequal_bestIdx_load2239_, <8 x i32> %cmp1852_to_boolvec, <8 x i32> zeroinitializer
  %mul__bestIdx_load2252 = shl nsw <8 x i32> %bestIdx1926.0.lcssa, splat (i32 3)
  %173 = or disjoint <8 x i32> %mul__bestIdx_load2252, splat (i32 4)
  %new_add3674 = sext <8 x i32> %173 to <8 x i64>
  %vecmask_1.i4333 = shufflevector <8 x i32> %"oldMask&test2244", <8 x i32> poison, <4 x i32> <i32 0, i32 1, i32 2, i32 3>
  %vecmask_2.i4334 = shufflevector <8 x i32> %"oldMask&test2244", <8 x i32> poison, <4 x i32> <i32 4, i32 5, i32 6, i32 7>
  %offsets_1.i4335 = shufflevector <8 x i64> %new_add3674, <8 x i64> poison, <4 x i32> <i32 0, i32 1, i32 2, i32 3>
  %offsets_2.i4336 = shufflevector <8 x i64> %new_add3674, <8 x i64> poison, <4 x i32> <i32 4, i32 5, i32 6, i32 7>
  %v1_1.i4337 = tail call <4 x i32> @llvm.x86.avx2.gather.q.d.256(<4 x i32> undef, ptr %HashIndex_ptr, <4 x i64> %offsets_1.i4335, <4 x i32> %vecmask_1.i4333, i8 1)
  %v2_1.i4338 = tail call <4 x i32> @llvm.x86.avx2.gather.q.d.256(<4 x i32> undef, ptr %HashIndex_ptr, <4 x i64> %offsets_2.i4336, <4 x i32> %vecmask_2.i4334, i8 1)
  %v.i4339 = shufflevector <4 x i32> %v1_1.i4337, <4 x i32> %v2_1.i4338, <8 x i32> <i32 0, i32 1, i32 2, i32 3, i32 4, i32 5, i32 6, i32 7>
  %val.i4340 = bitcast <8 x i32> %v.i4339 to <8 x float>
  call void @llvm.x86.avx.maskstore.ps.256(ptr %ptr3576, <8 x i32> %"oldMask&test2244", <8 x float> %val.i4340)
  br label %safe_if_after_true2242

for_test2272.for_exit2275_crit_edge:              ; preds = %not_all_continued_or_breaked2338
  %174 = bitcast <8 x float> %blend.i4347 to <8 x i32>
  br label %for_exit2275

for_exit2275:                                     ; preds = %for_test2272.for_exit2275_crit_edge, %for_test2272.preheader
  %bestIdx1926.5.lcssa = phi <8 x i32> [ %174, %for_test2272.for_exit2275_crit_edge ], [ %bestIdx1926.0.lcssa, %for_test2272.preheader ]
  %notequal_bestIdx_load2386_ = icmp eq <8 x i32> %bestIdx1926.5.lcssa, splat (i32 -1)
  %"oldMask&test2391" = select <8 x i1> %notequal_bestIdx_load2386_, <8 x i32> zeroinitializer, <8 x i32> %"oldMask&~test2267"
  %175 = icmp slt <8 x i32> %"oldMask&test2391", zeroinitializer
  %176 = bitcast <8 x i1> %175 to i8
  %cmp.i3894.not = icmp eq i8 %176, 0
  br i1 %cmp.i3894.not, label %common.ret, label %safe_if_run_true2390

not_all_continued_or_breaked2338:                 ; preds = %not_all_continued_or_breaked2338.lr.ph, %not_all_continued_or_breaked2338
  %indvars.iv4664 = phi i64 [ 0, %not_all_continued_or_breaked2338.lr.ph ], [ %indvars.iv.next4665, %not_all_continued_or_breaked2338 ]
  %"oldMask&test22844581" = phi <8 x i32> [ %"oldMask&test22844574", %not_all_continued_or_breaked2338.lr.ph ], [ %"oldMask&test2284", %not_all_continued_or_breaked2338 ]
  %i2279.04580 = phi <8 x i32> [ zeroinitializer, %not_all_continued_or_breaked2338.lr.ph ], [ %i_load2380_plus1, %not_all_continued_or_breaked2338 ]
  %bestIdx1926.54577 = phi <8 x float> [ %172, %not_all_continued_or_breaked2338.lr.ph ], [ %blend.i4347, %not_all_continued_or_breaked2338 ]
  %bestDistSq1925.54576 = phi <8 x float> [ %bestDistSq1925.0.lcssa, %not_all_continued_or_breaked2338.lr.ph ], [ %blend.i.i4343, %not_all_continued_or_breaked2338 ]
  %177 = shl nsw i64 %indvars.iv4664, 3
  %ptr3691 = getelementptr i8, ptr %SortedPositions_ptr, i64 %177
  %SortedPositions_ptr_load2293_offset_load36903692 = load float, ptr %ptr3691, align 4
  %SortedPositions_ptr_load2293_offset_load36903693 = insertelement <8 x float> poison, float %SortedPositions_ptr_load2293_offset_load36903692, i64 0
  %SortedPositions_ptr_load2293_offset_load36903694 = shufflevector <8 x float> %SortedPositions_ptr_load2293_offset_load36903693, <8 x float> poison, <8 x i32> zeroinitializer
  %sub_a14_x_b26_x.i.i4148 = fsub <8 x float> %SortedPositions_ptr_load2293_offset_load36903694, %v_1.i4311
  %mul_a13_x_b25_x.i.i.i4157 = fmul <8 x float> %sub_a14_x_b26_x.i.i4148, %sub_a14_x_b26_x.i.i4148
  %gep4584 = getelementptr i8, ptr %invariant.gep4583, i64 %177
  %SortedPositions_ptr_load2293_offset_load230837003705 = load float, ptr %gep4584, align 4
  %SortedPositions_ptr_load2293_offset_load230837003706 = insertelement <8 x float> poison, float %SortedPositions_ptr_load2293_offset_load230837003705, i64 0
  %SortedPositions_ptr_load2293_offset_load230837003707 = shufflevector <8 x float> %SortedPositions_ptr_load2293_offset_load230837003706, <8 x float> poison, <8 x i32> zeroinitializer
  %sub_a19_y_b211_y.i.i4149 = fsub <8 x float> %SortedPositions_ptr_load2293_offset_load230837003707, %v_1.i4314
  %mul_a17_y_b29_y.i.i.i4158 = fmul <8 x float> %sub_a19_y_b211_y.i.i4149, %sub_a19_y_b211_y.i.i4149
  %add_mul_a13_x_b25_x_mul_a17_y_b29_y.i.i.i4159 = fadd <8 x float> %mul_a13_x_b25_x.i.i.i4157, %mul_a17_y_b29_y.i.i.i4158
  %less_distSq_load2352_bestDistSq_load2353 = fcmp olt <8 x float> %add_mul_a13_x_b25_x_mul_a17_y_b29_y.i.i.i4159, %bestDistSq1925.54576
  %178 = bitcast <8 x i32> %"oldMask&test22844581" to <8 x float>
  %mask_as_float.i.i4341 = select <8 x i1> %less_distSq_load2352_bestDistSq_load2353, <8 x float> %178, <8 x float> zeroinitializer
  %blend.i.i4343 = call <8 x float> @llvm.x86.avx.blendv.ps.256(<8 x float> %bestDistSq1925.54576, <8 x float> %add_mul_a13_x_b25_x_mul_a17_y_b29_y.i.i.i4159, <8 x float> %mask_as_float.i.i4341)
  %newAsFloat.i4346 = bitcast <8 x i32> %i2279.04580 to <8 x float>
  %blend.i4347 = call <8 x float> @llvm.x86.avx.blendv.ps.256(<8 x float> %bestIdx1926.54577, <8 x float> %newAsFloat.i4346, <8 x float> %mask_as_float.i.i4341)
  %indvars.iv.next4665 = add nuw nsw i64 %indvars.iv4664, 1
  %i_load2380_plus1 = add nuw nsw <8 x i32> %i2279.04580, splat (i32 1)
  %less_i_load2280_SortedLength_load2281_broadcast2282 = icmp slt <8 x i32> %i_load2380_plus1, %SortedLength_load2281_broadcast2282
  %"oldMask&test2284" = select <8 x i1> %less_i_load2280_SortedLength_load2281_broadcast2282, <8 x i32> %"oldMask&test22844581", <8 x i32> zeroinitializer
  %179 = icmp slt <8 x i32> %"oldMask&test2284", zeroinitializer
  %180 = bitcast <8 x i1> %179 to i8
  %cmp.i3892.not = icmp eq i8 %180, 0
  br i1 %cmp.i3892.not, label %for_test2272.for_exit2275_crit_edge, label %not_all_continued_or_breaked2338, !llvm.loop !33

safe_if_run_true2390:                             ; preds = %for_exit2275
  %mul__bestIdx_load2399 = shl nsw <8 x i32> %bestIdx1926.5.lcssa, splat (i32 3)
  %181 = or disjoint <8 x i32> %mul__bestIdx_load2399, splat (i32 4)
  %new_add3712 = sext <8 x i32> %181 to <8 x i64>
  %vecmask_1.i4348 = shufflevector <8 x i32> %"oldMask&test2391", <8 x i32> poison, <4 x i32> <i32 0, i32 1, i32 2, i32 3>
  %vecmask_2.i4349 = shufflevector <8 x i32> %"oldMask&test2391", <8 x i32> poison, <4 x i32> <i32 4, i32 5, i32 6, i32 7>
  %offsets_1.i4350 = shufflevector <8 x i64> %new_add3712, <8 x i64> poison, <4 x i32> <i32 0, i32 1, i32 2, i32 3>
  %offsets_2.i4351 = shufflevector <8 x i64> %new_add3712, <8 x i64> poison, <4 x i32> <i32 4, i32 5, i32 6, i32 7>
  %v1_1.i4352 = tail call <4 x i32> @llvm.x86.avx2.gather.q.d.256(<4 x i32> undef, ptr %HashIndex_ptr, <4 x i64> %offsets_1.i4350, <4 x i32> %vecmask_1.i4348, i8 1)
  %v2_1.i4353 = tail call <4 x i32> @llvm.x86.avx2.gather.q.d.256(<4 x i32> undef, ptr %HashIndex_ptr, <4 x i64> %offsets_2.i4351, <4 x i32> %vecmask_2.i4349, i8 1)
  %v.i4354 = shufflevector <4 x i32> %v1_1.i4352, <4 x i32> %v2_1.i4353, <8 x i32> <i32 0, i32 1, i32 2, i32 3, i32 4, i32 5, i32 6, i32 7>
  %val.i4355 = bitcast <8 x i32> %v.i4354 to <8 x float>
  call void @llvm.x86.avx.maskstore.ps.256(ptr %ptr3576, <8 x i32> %"oldMask&test2391", <8 x float> %val.i4355)
  br label %common.ret
}

; Function Attrs: nounwind uwtable
define void @SharpNative_Job__global_namespace__GridSearch2D_ClosestPointJobPointer_Execute_Batch_true_impl___uniuniun_3C_s_5B_unfloat2_5D__3E_un_3C_unf_3E_un_3C_s_5B_unint2_5D__3E_un_3C_s_5B_unfloat2_5D__3E_uniun_3C_s_5B_unfloat2_5D__3E_uniun_3C_s_5B_unint2_5D__3E_uniun_3C_s_5B_unUnsafeList_Context_int2_5D__3E_un_3C_uni_3E_un_3C_unb_3E_un_3C_unf_3E_un_3C_uni_3E_uni(i32 %__startIndex, i32 %__count, ptr noalias readonly captures(none) %GridOrigin_ptr, ptr noalias readonly captures(none) %GridResolutionInv_ptr, ptr noalias readonly captures(none) %GridDimensions_ptr, ptr noalias %QueryPositions_ptr, i32 %QueryPositions_length, ptr noalias %SortedPositions_ptr, i32 %SortedPositions_length, ptr noalias %HashIndex_ptr, i32 %HashIndex_length, ptr noalias readonly captures(none) %CellStartEnd, ptr noalias readonly captures(none) %SortedLength_ptr, ptr noalias readnone captures(none) %IgnoreSelf_ptr, ptr noalias readonly captures(none) %SquaredEpsilonSelf_ptr, ptr noalias captures(none) %Results_ptr, i32 %Results_length, <8 x i32> %__mask) local_unnamed_addr #0 {
allocas:
  %0 = icmp sgt <8 x i32> %__mask, splat (i32 -1)
  %1 = bitcast <8 x i1> %0 to i8
  %cmp.i = icmp eq i8 %1, 0
  %GridOrigin_ptr_load_load.unpack = load float, ptr %GridOrigin_ptr, align 4
  %GridOrigin_ptr_load_load.elt3385 = getelementptr inbounds nuw i8, ptr %GridOrigin_ptr, i64 4
  %GridOrigin_ptr_load_load.unpack3386 = load float, ptr %GridOrigin_ptr_load_load.elt3385, align 4
  %GridResolutionInv_ptr_load_load = load float, ptr %GridResolutionInv_ptr, align 4
  %GridDimensions_ptr_load_load.unpack = load i32, ptr %GridDimensions_ptr, align 4
  %GridDimensions_ptr_load_load.elt3388 = getelementptr inbounds nuw i8, ptr %GridDimensions_ptr, i64 4
  %GridDimensions_ptr_load_load.unpack3389 = load i32, ptr %GridDimensions_ptr_load_load.elt3388, align 4
  %SortedLength_ptr_load_load = load i32, ptr %SortedLength_ptr, align 4
  %SquaredEpsilonSelf_ptr_load_load = load float, ptr %SquaredEpsilonSelf_ptr, align 4
  %add___startIndex_load37___count_load = add nsw i32 %__count, %__startIndex
  %ret.i.i = tail call i32 @llvm.smin.i32(i32 %QueryPositions_length, i32 %add___startIndex_load37___count_load)
  %nitems = sub nsw i32 %ret.i.i, %__startIndex
  %nextras = srem i32 %nitems, 8
  %aligned_end = sub nsw i32 %ret.i.i, %nextras
  %before_aligned_end484633 = icmp slt i32 %__startIndex, %aligned_end
  br i1 %cmp.i, label %outer_not_in_extras.preheader, label %outer_not_in_extras1226.preheader

outer_not_in_extras1226.preheader:                ; preds = %allocas
  br i1 %before_aligned_end484633, label %foreach_full_body1200.lr.ph, label %partial_inner_all_outer1244, !llvm.loop !34

foreach_full_body1200.lr.ph:                      ; preds = %outer_not_in_extras1226.preheader
  %get_element1283_broadcast = insertelement <8 x float> poison, float %GridOrigin_ptr_load_load.unpack, i64 0
  %get_element1283_broadcast1284 = shufflevector <8 x float> %get_element1283_broadcast, <8 x float> poison, <8 x i32> zeroinitializer
  %get_element1286_broadcast = insertelement <8 x float> poison, float %GridOrigin_ptr_load_load.unpack3386, i64 0
  %get_element1286_broadcast1287 = shufflevector <8 x float> %get_element1286_broadcast, <8 x float> poison, <8 x i32> zeroinitializer
  %GridResolutionInv_load1292_broadcast = insertelement <8 x float> poison, float %GridResolutionInv_ptr_load_load, i64 0
  %GridResolutionInv_load1292_broadcast1293 = shufflevector <8 x float> %GridResolutionInv_load1292_broadcast, <8 x float> poison, <8 x i32> zeroinitializer
  %get_element1308_broadcast = insertelement <8 x i32> poison, i32 %GridDimensions_ptr_load_load.unpack, i64 0
  %get_element1308_broadcast1309 = shufflevector <8 x i32> %get_element1308_broadcast, <8 x i32> poison, <8 x i32> zeroinitializer
  %get_element1311_broadcast = insertelement <8 x i32> poison, i32 %GridDimensions_ptr_load_load.unpack3389, i64 0
  %get_element1311_broadcast1312 = shufflevector <8 x i32> %get_element1311_broadcast, <8 x i32> poison, <8 x i32> zeroinitializer
  %sub_a14_x_b_load.i4039 = add nsw <8 x i32> %get_element1308_broadcast1309, splat (i32 -1)
  %sub_a17_y_b_load9.i4040 = add nsw <8 x i32> %get_element1311_broadcast1312, splat (i32 -1)
  %SquaredEpsilonSelf_load1545_broadcast = insertelement <8 x float> poison, float %SquaredEpsilonSelf_ptr_load_load, i64 0
  %SquaredEpsilonSelf_load1545_broadcast1546 = shufflevector <8 x float> %SquaredEpsilonSelf_load1545_broadcast, <8 x float> poison, <8 x i32> zeroinitializer
  %SortedLength_load1676_broadcast = insertelement <8 x i32> poison, i32 %SortedLength_ptr_load_load, i64 0
  %SortedLength_load1676_broadcast1677 = shufflevector <8 x i32> %SortedLength_load1676_broadcast, <8 x i32> poison, <8 x i32> zeroinitializer
  %less_i_load1675_SortedLength_load1676_broadcast16774553 = icmp sgt <8 x i32> %SortedLength_load1676_broadcast1677, zeroinitializer
  %invariant.gep = getelementptr i8, ptr %SortedPositions_ptr, i64 4
  %2 = sext i32 %__startIndex to i64
  %3 = sext i32 %aligned_end to i64
  br label %foreach_full_body1200, !llvm.loop !34

outer_not_in_extras.preheader:                    ; preds = %allocas
  br i1 %before_aligned_end484633, label %foreach_full_body.lr.ph, label %partial_inner_all_outer, !llvm.loop !35

foreach_full_body.lr.ph:                          ; preds = %outer_not_in_extras.preheader
  %get_element_broadcast = insertelement <8 x float> poison, float %GridOrigin_ptr_load_load.unpack, i64 0
  %get_element_broadcast75 = shufflevector <8 x float> %get_element_broadcast, <8 x float> poison, <8 x i32> zeroinitializer
  %get_element76_broadcast = insertelement <8 x float> poison, float %GridOrigin_ptr_load_load.unpack3386, i64 0
  %get_element76_broadcast77 = shufflevector <8 x float> %get_element76_broadcast, <8 x float> poison, <8 x i32> zeroinitializer
  %GridResolutionInv_load_broadcast = insertelement <8 x float> poison, float %GridResolutionInv_ptr_load_load, i64 0
  %GridResolutionInv_load_broadcast82 = shufflevector <8 x float> %GridResolutionInv_load_broadcast, <8 x float> poison, <8 x i32> zeroinitializer
  %get_element95_broadcast = insertelement <8 x i32> poison, i32 %GridDimensions_ptr_load_load.unpack, i64 0
  %get_element95_broadcast96 = shufflevector <8 x i32> %get_element95_broadcast, <8 x i32> poison, <8 x i32> zeroinitializer
  %get_element98_broadcast = insertelement <8 x i32> poison, i32 %GridDimensions_ptr_load_load.unpack3389, i64 0
  %get_element98_broadcast99 = shufflevector <8 x i32> %get_element98_broadcast, <8 x i32> poison, <8 x i32> zeroinitializer
  %sub_a14_x_b_load.i = add nsw <8 x i32> %get_element95_broadcast96, splat (i32 -1)
  %sub_a17_y_b_load9.i = add nsw <8 x i32> %get_element98_broadcast99, splat (i32 -1)
  %SquaredEpsilonSelf_load_broadcast = insertelement <8 x float> poison, float %SquaredEpsilonSelf_ptr_load_load, i64 0
  %SquaredEpsilonSelf_load_broadcast285 = shufflevector <8 x float> %SquaredEpsilonSelf_load_broadcast, <8 x float> poison, <8 x i32> zeroinitializer
  %SortedLength_load_broadcast = insertelement <8 x i32> poison, i32 %SortedLength_ptr_load_load, i64 0
  %SortedLength_load_broadcast404 = shufflevector <8 x i32> %SortedLength_load_broadcast, <8 x i32> poison, <8 x i32> zeroinitializer
  %less_i_load403_SortedLength_load_broadcast4044621 = icmp sgt <8 x i32> %SortedLength_load_broadcast404, zeroinitializer
  %invariant.gep4631 = getelementptr i8, ptr %SortedPositions_ptr, i64 4
  %4 = sext i32 %__startIndex to i64
  %5 = sext i32 %aligned_end to i64
  br label %foreach_full_body, !llvm.loop !35

foreach_full_body:                                ; preds = %foreach_full_body.lr.ph, %if_done
  %indvars.iv4685 = phi i64 [ %4, %foreach_full_body.lr.ph ], [ %indvars.iv.next4686, %if_done ]
  %6 = trunc nsw i64 %indvars.iv4685 to i32
  %smear_counter_init52 = insertelement <8 x i32> poison, i32 %6, i64 0
  %smear_counter53 = shufflevector <8 x i32> %smear_counter_init52, <8 x i32> poison, <8 x i32> zeroinitializer
  %iter_val54 = add nsw <8 x i32> %smear_counter53, <i32 0, i32 1, i32 2, i32 3, i32 4, i32 5, i32 6, i32 7>
  %7 = shl nsw i64 %indvars.iv4685, 2
  %ptr = getelementptr i8, ptr %Results_ptr, i64 %7
  store <8 x i32> splat (i32 -1), ptr %ptr, align 4, !filename !11, !first_line !36, !first_column !13, !last_line !36, !last_column !14
  %mul__index_load61 = shl nsw <8 x i32> %iter_val54, splat (i32 3)
  %v_1.i = tail call <8 x float> @llvm.x86.avx2.gather.d.ps.256(<8 x float> undef, ptr readonly %QueryPositions_ptr, <8 x i32> %mul__index_load61, <8 x float> splat (float 0xFFFFFFFFE0000000), i8 1)
  %8 = or disjoint <8 x i32> %mul__index_load61, splat (i32 4)
  %v_1.i4183 = tail call <8 x float> @llvm.x86.avx2.gather.d.ps.256(<8 x float> undef, ptr readonly %QueryPositions_ptr, <8 x i32> %8, <8 x float> splat (float 0xFFFFFFFFE0000000), i8 1)
  %sub_a14_x_b26_x.i = fsub <8 x float> %v_1.i, %get_element_broadcast75
  %sub_a19_y_b211_y.i = fsub <8 x float> %v_1.i4183, %get_element76_broadcast77
  %mul_v14_x_s_load.i = fmul <8 x float> %GridResolutionInv_load_broadcast82, %sub_a14_x_b26_x.i
  %mul_v17_y_s_load9.i = fmul <8 x float> %GridResolutionInv_load_broadcast82, %sub_a19_y_b211_y.i
  %call.i.i.i = tail call <8 x float> @llvm.x86.avx.round.ps.256(<8 x float> %mul_v14_x_s_load.i, i32 9)
  %call.i.i3.i = tail call <8 x float> @llvm.x86.avx.round.ps.256(<8 x float> %mul_v17_y_s_load9.i, i32 9)
  %v12_x_to_int32.i = fptosi <8 x float> %call.i.i.i to <8 x i32>
  %v14_y_to_int32.i = fptosi <8 x float> %call.i.i3.i to <8 x i32>
  %9 = call <8 x i32> @llvm.smax.v8i32(<8 x i32> %v12_x_to_int32.i, <8 x i32> zeroinitializer)
  %10 = call <8 x i32> @llvm.smax.v8i32(<8 x i32> %v14_y_to_int32.i, <8 x i32> zeroinitializer)
  %blend.i4191.v = call <8 x i32> @llvm.smin.v8i32(<8 x i32> %9, <8 x i32> %sub_a14_x_b_load.i)
  %blend.i4195.v = call <8 x i32> @llvm.smin.v8i32(<8 x i32> %10, <8 x i32> %sub_a17_y_b_load9.i)
  br label %for_loop

for_loop:                                         ; preds = %foreach_full_body, %for_step
  %11 = phi i8 [ -1, %foreach_full_body ], [ %15, %for_step ]
  %"oldMask&test4620" = phi <8 x i32> [ splat (i32 -1), %foreach_full_body ], [ %"oldMask&test", %for_step ]
  %dx.04619 = phi <8 x i32> [ splat (i32 -1), %foreach_full_body ], [ %dx_load364_plus1, %for_step ]
  %bestIdx.04618 = phi <8 x i32> [ splat (i32 -1), %foreach_full_body ], [ %bestIdx.1, %for_step ]
  %bestDistSq.04617 = phi <8 x float> [ splat (float 0x47EFFFFFE0000000), %foreach_full_body ], [ %bestDistSq.1, %for_step ]
  %add_cell115_x_dx_load117 = add nsw <8 x i32> %dx.04619, %blend.i4191.v
  %greaterequal_nx_load_GridDimensions118_x_broadcast120.not = icmp ult <8 x i32> %add_cell115_x_dx_load117, %get_element95_broadcast96
  %"oldMask&test122" = select <8 x i1> %greaterequal_nx_load_GridDimensions118_x_broadcast120.not, <8 x i32> zeroinitializer, <8 x i32> %"oldMask&test4620"
  %12 = icmp slt <8 x i32> %"oldMask&test122", zeroinitializer
  %13 = bitcast <8 x i1> %12 to i8
  %"equal_finished&func_internal_mask&function_mask114" = icmp eq i8 %11, %13
  br i1 %"equal_finished&func_internal_mask&function_mask114", label %for_step, label %not_all_continued_or_breaked

for_step:                                         ; preds = %not_all_continued_or_breaked, %for_step139, %for_loop
  %bestDistSq.1 = phi <8 x float> [ %bestDistSq.04617, %for_loop ], [ %bestDistSq.04617, %not_all_continued_or_breaked ], [ %bestDistSq.3, %for_step139 ]
  %bestIdx.1 = phi <8 x i32> [ %bestIdx.04618, %for_loop ], [ %bestIdx.04618, %not_all_continued_or_breaked ], [ %bestIdx.3, %for_step139 ]
  %internal_mask_memory.2 = phi <8 x i32> [ zeroinitializer, %for_loop ], [ %new_mask134, %not_all_continued_or_breaked ], [ %new_mask134, %for_step139 ]
  %"mask|continue_mask361" = or <8 x i32> %internal_mask_memory.2, %"oldMask&test122"
  %dx_load364_plus1 = add nsw <8 x i32> %dx.04619, splat (i32 1)
  %lessequal_dx_load_.inv = icmp sgt <8 x i32> %dx.04619, zeroinitializer
  %"oldMask&test" = select <8 x i1> %lessequal_dx_load_.inv, <8 x i32> zeroinitializer, <8 x i32> %"mask|continue_mask361"
  %14 = icmp slt <8 x i32> %"oldMask&test", zeroinitializer
  %15 = bitcast <8 x i1> %14 to i8
  %cmp.i3837.not = icmp eq i8 %15, 0
  br i1 %cmp.i3837.not, label %for_exit, label %for_loop, !llvm.loop !37

for_exit:                                         ; preds = %for_step
  %notequal_bestIdx_load_ = icmp ne <8 x i32> %bestIdx.1, splat (i32 -1)
  %notequal_bestIdx_load__to_boolvec = sext <8 x i1> %notequal_bestIdx_load_ to <8 x i32>
  %16 = bitcast <8 x i1> %notequal_bestIdx_load_ to i8
  %cmp.i3839.not = icmp eq i8 %16, 0
  br i1 %cmp.i3839.not, label %safe_if_after_true, label %safe_if_run_true

for_loop650:                                      ; preds = %for_loop650.lr.ph, %for_step651
  %17 = phi i8 [ %67, %for_loop650.lr.ph ], [ %21, %for_step651 ]
  %"oldMask&test6594656" = phi <8 x i32> [ %cmp576_to_boolvec, %for_loop650.lr.ph ], [ %"oldMask&test659", %for_step651 ]
  %dx656.04655 = phi <8 x i32> [ splat (i32 -1), %for_loop650.lr.ph ], [ %dx_load955_plus1, %for_step651 ]
  %bestIdx648.04654 = phi <8 x i32> [ splat (i32 -1), %for_loop650.lr.ph ], [ %bestIdx648.1, %for_step651 ]
  %bestDistSq647.04653 = phi <8 x float> [ splat (float 0x47EFFFFFE0000000), %for_loop650.lr.ph ], [ %bestDistSq647.1, %for_step651 ]
  %add_cell607667_x_dx_load669 = add nsw <8 x i32> %dx656.04655, %blend.i16.i.v
  %greaterequal_nx_load670_GridDimensions671_x_broadcast673.not = icmp ult <8 x i32> %add_cell607667_x_dx_load669, %get_element635_broadcast636
  %"oldMask&test675" = select <8 x i1> %greaterequal_nx_load670_GridDimensions671_x_broadcast673.not, <8 x i32> zeroinitializer, <8 x i32> %"oldMask&test6594656"
  %18 = icmp slt <8 x i32> %"oldMask&test675", zeroinitializer
  %19 = bitcast <8 x i1> %18 to i8
  %"equal_finished&func688_internal_mask&function_mask665" = icmp eq i8 %17, %19
  br i1 %"equal_finished&func688_internal_mask&function_mask665", label %for_step651, label %not_all_continued_or_breaked690

for_step651:                                      ; preds = %not_all_continued_or_breaked690, %for_step706, %for_loop650
  %bestDistSq647.1 = phi <8 x float> [ %bestDistSq647.04653, %for_loop650 ], [ %bestDistSq647.04653, %not_all_continued_or_breaked690 ], [ %bestDistSq647.3, %for_step706 ]
  %bestIdx648.1 = phi <8 x i32> [ %bestIdx648.04654, %for_loop650 ], [ %bestIdx648.04654, %not_all_continued_or_breaked690 ], [ %bestIdx648.3, %for_step706 ]
  %internal_mask_memory.10 = phi <8 x i32> [ zeroinitializer, %for_loop650 ], [ %new_mask701, %not_all_continued_or_breaked690 ], [ %new_mask701, %for_step706 ]
  %"mask|continue_mask952" = or <8 x i32> %internal_mask_memory.10, %"oldMask&test675"
  %dx_load955_plus1 = add nsw <8 x i32> %dx656.04655, splat (i32 1)
  %lessequal_dx_load657_.inv = icmp sgt <8 x i32> %dx656.04655, zeroinitializer
  %"oldMask&test659" = select <8 x i1> %lessequal_dx_load657_.inv, <8 x i32> zeroinitializer, <8 x i32> %"mask|continue_mask952"
  %20 = icmp slt <8 x i32> %"oldMask&test659", zeroinitializer
  %21 = bitcast <8 x i1> %20 to i8
  %cmp.i3840.not = icmp eq i8 %21, 0
  br i1 %cmp.i3840.not, label %for_exit652, label %for_loop650, !llvm.loop !38

for_exit652:                                      ; preds = %for_step651, %partial_inner_only
  %bestDistSq647.0.lcssa = phi <8 x float> [ splat (float 0x47EFFFFFE0000000), %partial_inner_only ], [ %bestDistSq647.1, %for_step651 ]
  %bestIdx648.0.lcssa = phi <8 x i32> [ splat (i32 -1), %partial_inner_only ], [ %bestIdx648.1, %for_step651 ]
  %notequal_bestIdx_load961_ = icmp ne <8 x i32> %bestIdx648.0.lcssa, splat (i32 -1)
  %22 = select <8 x i1> %notequal_bestIdx_load961_, <8 x i1> %cmp576, <8 x i1> zeroinitializer
  %23 = bitcast <8 x i1> %22 to i8
  %cmp.i3843.not = icmp eq i8 %23, 0
  br i1 %cmp.i3843.not, label %safe_if_after_true964, label %safe_if_run_true965

common.ret:                                       ; preds = %for_exit2275, %safe_if_run_true2390, %safe_if_after_true2242, %for_exit997, %safe_if_run_true1112, %safe_if_after_true964, %partial_inner_all_outer1244, %partial_inner_all_outer
  ret void

outer_not_in_extras.partial_inner_all_outer_crit_edge: ; preds = %if_done
  %24 = trunc nsw i64 %indvars.iv.next4686 to i32
  br label %partial_inner_all_outer, !llvm.loop !35

partial_inner_all_outer:                          ; preds = %outer_not_in_extras.partial_inner_all_outer_crit_edge, %outer_not_in_extras.preheader
  %counter.1.lcssa = phi i32 [ %24, %outer_not_in_extras.partial_inner_all_outer_crit_edge ], [ %__startIndex, %outer_not_in_extras.preheader ]
  %before_full_end = icmp slt i32 %counter.1.lcssa, %ret.i.i
  br i1 %before_full_end, label %partial_inner_only, label %common.ret

not_all_continued_or_breaked:                     ; preds = %for_loop
  %new_mask134 = xor <8 x i32> %"oldMask&test122", %"oldMask&test4620"
  %25 = icmp slt <8 x i32> %new_mask134, zeroinitializer
  %26 = bitcast <8 x i1> %25 to i8
  %cmp.i3844.not4610 = icmp eq i8 %26, 0
  br i1 %cmp.i3844.not4610, label %for_step, label %for_loop138

for_loop138:                                      ; preds = %not_all_continued_or_breaked, %for_step139
  %27 = phi i8 [ %31, %for_step139 ], [ %26, %not_all_continued_or_breaked ]
  %"oldMask&test1454614" = phi <8 x i32> [ %"oldMask&test145", %for_step139 ], [ %new_mask134, %not_all_continued_or_breaked ]
  %dy.04613 = phi <8 x i32> [ %dy_load353_plus1, %for_step139 ], [ splat (i32 -1), %not_all_continued_or_breaked ]
  %bestIdx.24612 = phi <8 x i32> [ %bestIdx.3, %for_step139 ], [ %bestIdx.04618, %not_all_continued_or_breaked ]
  %bestDistSq.24611 = phi <8 x float> [ %bestDistSq.3, %for_step139 ], [ %bestDistSq.04617, %not_all_continued_or_breaked ]
  %add_cell152_y_dy_load154 = add nsw <8 x i32> %dy.04613, %blend.i4195.v
  %greaterequal_ny_load_GridDimensions155_y_broadcast157.not = icmp ult <8 x i32> %add_cell152_y_dy_load154, %get_element98_broadcast99
  %"oldMask&test159" = select <8 x i1> %greaterequal_ny_load_GridDimensions155_y_broadcast157.not, <8 x i32> zeroinitializer, <8 x i32> %"oldMask&test1454614"
  %28 = icmp slt <8 x i32> %"oldMask&test159", zeroinitializer
  %29 = bitcast <8 x i1> %28 to i8
  %"equal_finished&func172_internal_mask&function_mask151" = icmp eq i8 %27, %29
  br i1 %"equal_finished&func172_internal_mask&function_mask151", label %for_step139, label %not_all_continued_or_breaked174

for_step139:                                      ; preds = %not_all_continued_or_breaked233, %for_step249, %not_all_continued_or_breaked174, %for_loop138
  %bestDistSq.3 = phi <8 x float> [ %bestDistSq.24611, %for_loop138 ], [ %bestDistSq.24611, %not_all_continued_or_breaked174 ], [ %bestDistSq.24611, %not_all_continued_or_breaked233 ], [ %bestDistSq.5, %for_step249 ]
  %bestIdx.3 = phi <8 x i32> [ %bestIdx.24612, %for_loop138 ], [ %bestIdx.24612, %not_all_continued_or_breaked174 ], [ %bestIdx.24612, %not_all_continued_or_breaked233 ], [ %bestIdx.5, %for_step249 ]
  %continue_lanes_memory142.1 = phi <8 x i32> [ %"oldMask&test159", %for_loop138 ], [ %"mask|continueMask222", %not_all_continued_or_breaked174 ], [ %"mask|continueMask222", %not_all_continued_or_breaked233 ], [ %"mask|continueMask222", %for_step249 ]
  %internal_mask_memory.4 = phi <8 x i32> [ zeroinitializer, %for_loop138 ], [ zeroinitializer, %not_all_continued_or_breaked174 ], [ %new_mask244, %not_all_continued_or_breaked233 ], [ %new_mask244, %for_step249 ]
  %"mask|continue_mask350" = or <8 x i32> %internal_mask_memory.4, %continue_lanes_memory142.1
  %dy_load353_plus1 = add nsw <8 x i32> %dy.04613, splat (i32 1)
  %lessequal_dy_load_.inv = icmp sgt <8 x i32> %dy.04613, zeroinitializer
  %"oldMask&test145" = select <8 x i1> %lessequal_dy_load_.inv, <8 x i32> zeroinitializer, <8 x i32> %"mask|continue_mask350"
  %30 = icmp slt <8 x i32> %"oldMask&test145", zeroinitializer
  %31 = bitcast <8 x i1> %30 to i8
  %cmp.i3844.not = icmp eq i8 %31, 0
  br i1 %cmp.i3844.not, label %for_step, label %for_loop138, !llvm.loop !39

not_all_continued_or_breaked174:                  ; preds = %for_loop138
  %new_mask185 = xor <8 x i32> %"oldMask&test159", %"oldMask&test1454614"
  %mul_ny_load188_GridDimensions189_x_broadcast191 = mul nsw <8 x i32> %add_cell152_y_dy_load154, %get_element95_broadcast96
  %add_mul_ny_load188_GridDimensions189_x_broadcast191_nx_load192 = add nsw <8 x i32> %mul_ny_load188_GridDimensions189_x_broadcast191, %add_cell115_x_dx_load117
  %CellStartEnd_load193__data = load ptr, ptr %CellStartEnd, align 8
  %mul__cellHash_load = shl nsw <8 x i32> %add_mul_ny_load188_GridDimensions189_x_broadcast191_nx_load192, splat (i32 3)
  %v_1.i4196 = tail call <8 x i32> @llvm.x86.avx2.gather.d.d.256(<8 x i32> undef, ptr readonly %CellStartEnd_load193__data, <8 x i32> %mul__cellHash_load, <8 x i32> %new_mask185, i8 1)
  %32 = or disjoint <8 x i32> %mul__cellHash_load, splat (i32 4)
  %v_1.i4197 = tail call <8 x i32> @llvm.x86.avx2.gather.d.d.256(<8 x i32> undef, ptr readonly %CellStartEnd_load193__data, <8 x i32> %32, <8 x i32> %new_mask185, i8 1)
  %isneg3393 = icmp slt <8 x i32> %v_1.i4196, zeroinitializer
  %"oldMask&test217" = select <8 x i1> %isneg3393, <8 x i32> %new_mask185, <8 x i32> zeroinitializer
  %"mask|continueMask222" = or <8 x i32> %"oldMask&test217", %"oldMask&test159"
  %33 = icmp slt <8 x i32> %"mask|continueMask222", zeroinitializer
  %34 = bitcast <8 x i1> %33 to i8
  %"equal_finished&func230_internal_mask&function_mask151" = icmp eq i8 %27, %34
  br i1 %"equal_finished&func230_internal_mask&function_mask151", label %for_step139, label %not_all_continued_or_breaked233

not_all_continued_or_breaked233:                  ; preds = %not_all_continued_or_breaked174
  %new_mask244 = xor <8 x i32> %"mask|continueMask222", %"oldMask&test1454614"
  %less_i_load_end_load4601 = icmp slt <8 x i32> %v_1.i4196, %v_1.i4197
  %"oldMask&test2564602" = select <8 x i1> %less_i_load_end_load4601, <8 x i32> %new_mask244, <8 x i32> zeroinitializer
  %35 = icmp slt <8 x i32> %"oldMask&test2564602", zeroinitializer
  %36 = bitcast <8 x i1> %35 to i8
  %cmp.i3848.not4603 = icmp eq i8 %36, 0
  br i1 %cmp.i3848.not4603, label %for_step139, label %for_loop248

for_loop248:                                      ; preds = %not_all_continued_or_breaked233, %for_step249
  %37 = phi i8 [ %42, %for_step249 ], [ %36, %not_all_continued_or_breaked233 ]
  %"oldMask&test2564607" = phi <8 x i32> [ %"oldMask&test256", %for_step249 ], [ %"oldMask&test2564602", %not_all_continued_or_breaked233 ]
  %i.04606 = phi <8 x i32> [ %i_load342_plus1, %for_step249 ], [ %v_1.i4196, %not_all_continued_or_breaked233 ]
  %bestIdx.44605 = phi <8 x i32> [ %bestIdx.5, %for_step249 ], [ %bestIdx.24612, %not_all_continued_or_breaked233 ]
  %bestDistSq.44604 = phi <8 x float> [ %bestDistSq.5, %for_step249 ], [ %bestDistSq.24611, %not_all_continued_or_breaked233 ]
  %mul__i_load263 = shl nsw <8 x i32> %i.04606, splat (i32 3)
  %mask.i = bitcast <8 x i32> %"oldMask&test2564607" to <8 x float>
  %v_1.i4198 = tail call <8 x float> @llvm.x86.avx2.gather.d.ps.256(<8 x float> undef, ptr readonly %SortedPositions_ptr, <8 x i32> %mul__i_load263, <8 x float> %mask.i, i8 1)
  %38 = or disjoint <8 x i32> %mul__i_load263, splat (i32 4)
  %v_1.i4200 = tail call <8 x float> @llvm.x86.avx2.gather.d.ps.256(<8 x float> undef, ptr readonly %SortedPositions_ptr, <8 x i32> %38, <8 x float> %mask.i, i8 1)
  %sub_a14_x_b26_x.i.i = fsub <8 x float> %v_1.i4198, %v_1.i
  %sub_a19_y_b211_y.i.i = fsub <8 x float> %v_1.i4200, %v_1.i4183
  %mul_a13_x_b25_x.i.i.i = fmul <8 x float> %sub_a14_x_b26_x.i.i, %sub_a14_x_b26_x.i.i
  %mul_a17_y_b29_y.i.i.i = fmul <8 x float> %sub_a19_y_b211_y.i.i, %sub_a19_y_b211_y.i.i
  %add_mul_a13_x_b25_x_mul_a17_y_b29_y.i.i.i = fadd <8 x float> %mul_a13_x_b25_x.i.i.i, %mul_a17_y_b29_y.i.i.i
  %less_distSq_load_SquaredEpsilonSelf_load_broadcast285 = fcmp olt <8 x float> %add_mul_a13_x_b25_x_mul_a17_y_b29_y.i.i.i, %SquaredEpsilonSelf_load_broadcast285
  %"oldMask&test287" = select <8 x i1> %less_distSq_load_SquaredEpsilonSelf_load_broadcast285, <8 x i32> %"oldMask&test2564607", <8 x i32> zeroinitializer
  %39 = icmp slt <8 x i32> %"oldMask&test287", zeroinitializer
  %40 = bitcast <8 x i1> %39 to i8
  %"equal_finished&func300_internal_mask&function_mask262" = icmp eq i8 %37, %40
  br i1 %"equal_finished&func300_internal_mask&function_mask262", label %for_step249, label %not_all_continued_or_breaked302

for_step249:                                      ; preds = %for_loop248, %not_all_continued_or_breaked302
  %bestDistSq.5 = phi <8 x float> [ %bestDistSq.44604, %for_loop248 ], [ %blend.i.i, %not_all_continued_or_breaked302 ]
  %bestIdx.5 = phi <8 x i32> [ %bestIdx.44605, %for_loop248 ], [ %45, %not_all_continued_or_breaked302 ]
  %internal_mask_memory.6 = phi <8 x i32> [ zeroinitializer, %for_loop248 ], [ %new_mask313, %not_all_continued_or_breaked302 ]
  %"mask|continue_mask" = or <8 x i32> %internal_mask_memory.6, %"oldMask&test287"
  %i_load342_plus1 = add nsw <8 x i32> %i.04606, splat (i32 1)
  %less_i_load_end_load = icmp slt <8 x i32> %i_load342_plus1, %v_1.i4197
  %"oldMask&test256" = select <8 x i1> %less_i_load_end_load, <8 x i32> %"mask|continue_mask", <8 x i32> zeroinitializer
  %41 = icmp slt <8 x i32> %"oldMask&test256", zeroinitializer
  %42 = bitcast <8 x i1> %41 to i8
  %cmp.i3848.not = icmp eq i8 %42, 0
  br i1 %cmp.i3848.not, label %for_step139, label %for_loop248, !llvm.loop !40

not_all_continued_or_breaked302:                  ; preds = %for_loop248
  %new_mask313 = xor <8 x i32> %"oldMask&test287", %"oldMask&test2564607"
  %less_distSq_load316_bestDistSq_load = fcmp olt <8 x float> %add_mul_a13_x_b25_x_mul_a17_y_b29_y.i.i.i, %bestDistSq.44604
  %43 = bitcast <8 x i32> %new_mask313 to <8 x float>
  %mask_as_float.i.i = select <8 x i1> %less_distSq_load316_bestDistSq_load, <8 x float> %43, <8 x float> zeroinitializer
  %blend.i.i = call <8 x float> @llvm.x86.avx.blendv.ps.256(<8 x float> %bestDistSq.44604, <8 x float> %add_mul_a13_x_b25_x_mul_a17_y_b29_y.i.i.i, <8 x float> %mask_as_float.i.i)
  %44 = bitcast <8 x i32> %bestIdx.44605 to <8 x float>
  %newAsFloat.i4203 = bitcast <8 x i32> %i.04606 to <8 x float>
  %blend.i4204 = call <8 x float> @llvm.x86.avx.blendv.ps.256(<8 x float> %44, <8 x float> %newAsFloat.i4203, <8 x float> %mask_as_float.i.i)
  %45 = bitcast <8 x float> %blend.i4204 to <8 x i32>
  br label %for_step249

if_done:                                          ; preds = %for_exit398, %safe_if_run_true512, %safe_if_after_true
  %indvars.iv.next4686 = add nsw i64 %indvars.iv4685, 8
  %before_aligned_end48 = icmp slt i64 %indvars.iv.next4686, %5
  br i1 %before_aligned_end48, label %foreach_full_body, label %outer_not_in_extras.partial_inner_all_outer_crit_edge, !llvm.loop !35

safe_if_after_true:                               ; preds = %safe_if_run_true, %for_exit
  %"~test" = xor <8 x i32> %notequal_bestIdx_load__to_boolvec, splat (i32 -1)
  %46 = xor <8 x i1> %notequal_bestIdx_load_, splat (i1 true)
  %47 = bitcast <8 x i1> %46 to i8
  %cmp.i3851.not = icmp eq i8 %47, 0
  br i1 %cmp.i3851.not, label %if_done, label %for_test395.preheader

for_test395.preheader:                            ; preds = %safe_if_after_true
  %"oldMask&test4064622" = select <8 x i1> %less_i_load403_SortedLength_load_broadcast4044621, <8 x i32> %"~test", <8 x i32> zeroinitializer
  %48 = icmp slt <8 x i32> %"oldMask&test4064622", zeroinitializer
  %49 = bitcast <8 x i1> %48 to i8
  %cmp.i3852.not4623 = icmp eq i8 %49, 0
  br i1 %cmp.i3852.not4623, label %for_exit398, label %for_loop396

safe_if_run_true:                                 ; preds = %for_exit
  %mul__bestIdx_load379 = shl nsw <8 x i32> %bestIdx.1, splat (i32 3)
  %50 = or disjoint <8 x i32> %mul__bestIdx_load379, splat (i32 4)
  %new_add3437 = sext <8 x i32> %50 to <8 x i64>
  %vecmask_1.i = shufflevector <8 x i32> %notequal_bestIdx_load__to_boolvec, <8 x i32> poison, <4 x i32> <i32 0, i32 1, i32 2, i32 3>
  %vecmask_2.i = shufflevector <8 x i32> %notequal_bestIdx_load__to_boolvec, <8 x i32> poison, <4 x i32> <i32 4, i32 5, i32 6, i32 7>
  %offsets_1.i = shufflevector <8 x i64> %new_add3437, <8 x i64> poison, <4 x i32> <i32 0, i32 1, i32 2, i32 3>
  %offsets_2.i = shufflevector <8 x i64> %new_add3437, <8 x i64> poison, <4 x i32> <i32 4, i32 5, i32 6, i32 7>
  %v1_1.i = tail call <4 x i32> @llvm.x86.avx2.gather.q.d.256(<4 x i32> undef, ptr %HashIndex_ptr, <4 x i64> %offsets_1.i, <4 x i32> %vecmask_1.i, i8 1)
  %v2_1.i = tail call <4 x i32> @llvm.x86.avx2.gather.q.d.256(<4 x i32> undef, ptr %HashIndex_ptr, <4 x i64> %offsets_2.i, <4 x i32> %vecmask_2.i, i8 1)
  %v.i = shufflevector <4 x i32> %v1_1.i, <4 x i32> %v2_1.i, <8 x i32> <i32 0, i32 1, i32 2, i32 3, i32 4, i32 5, i32 6, i32 7>
  %val.i = bitcast <8 x i32> %v.i to <8 x float>
  call void @llvm.masked.store.v8f32.p0(<8 x float> %val.i, ptr %ptr, i32 1, <8 x i1> %notequal_bestIdx_load_)
  br label %safe_if_after_true

for_loop396:                                      ; preds = %for_test395.preheader, %for_step397
  %indvars.iv4681 = phi i64 [ %indvars.iv.next4682, %for_step397 ], [ 0, %for_test395.preheader ]
  %51 = phi i8 [ %56, %for_step397 ], [ %49, %for_test395.preheader ]
  %"oldMask&test4064629" = phi <8 x i32> [ %"oldMask&test406", %for_step397 ], [ %"oldMask&test4064622", %for_test395.preheader ]
  %i402.04628 = phi <8 x i32> [ %i_load502_plus1, %for_step397 ], [ zeroinitializer, %for_test395.preheader ]
  %bestIdx.64625 = phi <8 x i32> [ %bestIdx.7, %for_step397 ], [ %bestIdx.1, %for_test395.preheader ]
  %bestDistSq.64624 = phi <8 x float> [ %bestDistSq.7, %for_step397 ], [ %bestDistSq.1, %for_test395.preheader ]
  %52 = shl nsw i64 %indvars.iv4681, 3
  %ptr3450 = getelementptr i8, ptr %SortedPositions_ptr, i64 %52, !filename !11, !first_line !41, !first_column !20, !last_line !41, !last_column !21
  %SortedPositions_ptr_load415_offset_load34493451 = load float, ptr %ptr3450, align 4
  %SortedPositions_ptr_load415_offset_load34493452 = insertelement <8 x float> poison, float %SortedPositions_ptr_load415_offset_load34493451, i64 0
  %SortedPositions_ptr_load415_offset_load34493453 = shufflevector <8 x float> %SortedPositions_ptr_load415_offset_load34493452, <8 x float> poison, <8 x i32> zeroinitializer, !filename !11, !first_line !41, !first_column !20, !last_line !41, !last_column !21
  %gep4632 = getelementptr i8, ptr %invariant.gep4631, i64 %52
  %SortedPositions_ptr_load415_offset_load43034593464 = load float, ptr %gep4632, align 4
  %SortedPositions_ptr_load415_offset_load43034593465 = insertelement <8 x float> poison, float %SortedPositions_ptr_load415_offset_load43034593464, i64 0
  %SortedPositions_ptr_load415_offset_load43034593466 = shufflevector <8 x float> %SortedPositions_ptr_load415_offset_load43034593465, <8 x float> poison, <8 x i32> zeroinitializer, !filename !11, !first_line !41, !first_column !20, !last_line !41, !last_column !21
  %sub_a14_x_b26_x.i.i3971 = fsub <8 x float> %SortedPositions_ptr_load415_offset_load34493453, %v_1.i
  %sub_a19_y_b211_y.i.i3972 = fsub <8 x float> %SortedPositions_ptr_load415_offset_load43034593466, %v_1.i4183
  %mul_a13_x_b25_x.i.i.i3980 = fmul <8 x float> %sub_a14_x_b26_x.i.i3971, %sub_a14_x_b26_x.i.i3971
  %mul_a17_y_b29_y.i.i.i3981 = fmul <8 x float> %sub_a19_y_b211_y.i.i3972, %sub_a19_y_b211_y.i.i3972
  %add_mul_a13_x_b25_x_mul_a17_y_b29_y.i.i.i3982 = fadd <8 x float> %mul_a13_x_b25_x.i.i.i3980, %mul_a17_y_b29_y.i.i.i3981
  %less_distSq_load440_SquaredEpsilonSelf_load441_broadcast442 = fcmp olt <8 x float> %add_mul_a13_x_b25_x_mul_a17_y_b29_y.i.i.i3982, %SquaredEpsilonSelf_load_broadcast285
  %"oldMask&test445" = select <8 x i1> %less_distSq_load440_SquaredEpsilonSelf_load441_broadcast442, <8 x i32> %"oldMask&test4064629", <8 x i32> zeroinitializer
  %53 = icmp slt <8 x i32> %"oldMask&test445", zeroinitializer
  %54 = bitcast <8 x i1> %53 to i8
  %"equal_finished&func458_internal_mask&function_mask412" = icmp eq i8 %51, %54
  br i1 %"equal_finished&func458_internal_mask&function_mask412", label %for_step397, label %not_all_continued_or_breaked460

for_step397:                                      ; preds = %for_loop396, %not_all_continued_or_breaked460
  %bestDistSq.7 = phi <8 x float> [ %bestDistSq.64624, %for_loop396 ], [ %blend.i.i4207, %not_all_continued_or_breaked460 ]
  %bestIdx.7 = phi <8 x i32> [ %bestIdx.64625, %for_loop396 ], [ %61, %not_all_continued_or_breaked460 ]
  %internal_mask_memory.8 = phi <8 x i32> [ zeroinitializer, %for_loop396 ], [ %new_mask471, %not_all_continued_or_breaked460 ]
  %"mask|continue_mask499" = or <8 x i32> %internal_mask_memory.8, %"oldMask&test445"
  %indvars.iv.next4682 = add nuw nsw i64 %indvars.iv4681, 1
  %i_load502_plus1 = add nuw nsw <8 x i32> %i402.04628, splat (i32 1)
  %less_i_load403_SortedLength_load_broadcast404 = icmp slt <8 x i32> %i_load502_plus1, %SortedLength_load_broadcast404
  %"oldMask&test406" = select <8 x i1> %less_i_load403_SortedLength_load_broadcast404, <8 x i32> %"mask|continue_mask499", <8 x i32> zeroinitializer
  %55 = icmp slt <8 x i32> %"oldMask&test406", zeroinitializer
  %56 = bitcast <8 x i1> %55 to i8
  %cmp.i3852.not = icmp eq i8 %56, 0
  br i1 %cmp.i3852.not, label %for_exit398, label %for_loop396, !llvm.loop !42

for_exit398:                                      ; preds = %for_step397, %for_test395.preheader
  %bestIdx.6.lcssa = phi <8 x i32> [ %bestIdx.1, %for_test395.preheader ], [ %bestIdx.7, %for_step397 ]
  %notequal_bestIdx_load508_ = icmp eq <8 x i32> %bestIdx.6.lcssa, splat (i32 -1)
  %"oldMask&test513" = select <8 x i1> %notequal_bestIdx_load508_, <8 x i32> zeroinitializer, <8 x i32> %"~test"
  %57 = icmp slt <8 x i32> %"oldMask&test513", zeroinitializer
  %58 = bitcast <8 x i1> %57 to i8
  %cmp.i3855.not = icmp eq i8 %58, 0
  br i1 %cmp.i3855.not, label %if_done, label %safe_if_run_true512

not_all_continued_or_breaked460:                  ; preds = %for_loop396
  %new_mask471 = xor <8 x i32> %"oldMask&test445", %"oldMask&test4064629"
  %less_distSq_load474_bestDistSq_load475 = fcmp olt <8 x float> %add_mul_a13_x_b25_x_mul_a17_y_b29_y.i.i.i3982, %bestDistSq.64624
  %59 = bitcast <8 x i32> %new_mask471 to <8 x float>
  %mask_as_float.i.i4205 = select <8 x i1> %less_distSq_load474_bestDistSq_load475, <8 x float> %59, <8 x float> zeroinitializer
  %blend.i.i4207 = call <8 x float> @llvm.x86.avx.blendv.ps.256(<8 x float> %bestDistSq.64624, <8 x float> %add_mul_a13_x_b25_x_mul_a17_y_b29_y.i.i.i3982, <8 x float> %mask_as_float.i.i4205)
  %60 = bitcast <8 x i32> %bestIdx.64625 to <8 x float>
  %newAsFloat.i4210 = bitcast <8 x i32> %i402.04628 to <8 x float>
  %blend.i4211 = call <8 x float> @llvm.x86.avx.blendv.ps.256(<8 x float> %60, <8 x float> %newAsFloat.i4210, <8 x float> %mask_as_float.i.i4205)
  %61 = bitcast <8 x float> %blend.i4211 to <8 x i32>
  br label %for_step397

safe_if_run_true512:                              ; preds = %for_exit398
  %mul__bestIdx_load521 = shl nsw <8 x i32> %bestIdx.6.lcssa, splat (i32 3)
  %62 = or disjoint <8 x i32> %mul__bestIdx_load521, splat (i32 4)
  %new_add3471 = sext <8 x i32> %62 to <8 x i64>
  %vecmask_1.i4212 = shufflevector <8 x i32> %"oldMask&test513", <8 x i32> poison, <4 x i32> <i32 0, i32 1, i32 2, i32 3>
  %vecmask_2.i4213 = shufflevector <8 x i32> %"oldMask&test513", <8 x i32> poison, <4 x i32> <i32 4, i32 5, i32 6, i32 7>
  %offsets_1.i4214 = shufflevector <8 x i64> %new_add3471, <8 x i64> poison, <4 x i32> <i32 0, i32 1, i32 2, i32 3>
  %offsets_2.i4215 = shufflevector <8 x i64> %new_add3471, <8 x i64> poison, <4 x i32> <i32 4, i32 5, i32 6, i32 7>
  %v1_1.i4216 = tail call <4 x i32> @llvm.x86.avx2.gather.q.d.256(<4 x i32> undef, ptr %HashIndex_ptr, <4 x i64> %offsets_1.i4214, <4 x i32> %vecmask_1.i4212, i8 1)
  %v2_1.i4217 = tail call <4 x i32> @llvm.x86.avx2.gather.q.d.256(<4 x i32> undef, ptr %HashIndex_ptr, <4 x i64> %offsets_2.i4215, <4 x i32> %vecmask_2.i4213, i8 1)
  %v.i4218 = shufflevector <4 x i32> %v1_1.i4216, <4 x i32> %v2_1.i4217, <8 x i32> <i32 0, i32 1, i32 2, i32 3, i32 4, i32 5, i32 6, i32 7>
  %val.i4219 = bitcast <8 x i32> %v.i4218 to <8 x float>
  call void @llvm.x86.avx.maskstore.ps.256(ptr nonnull %ptr, <8 x i32> %"oldMask&test513", <8 x float> %val.i4219)
  br label %if_done

partial_inner_only:                               ; preds = %partial_inner_all_outer
  %smear_counter_init571 = insertelement <8 x i32> poison, i32 %counter.1.lcssa, i64 0
  %smear_counter572 = shufflevector <8 x i32> %smear_counter_init571, <8 x i32> poison, <8 x i32> zeroinitializer
  %iter_val573 = add nsw <8 x i32> %smear_counter572, <i32 0, i32 1, i32 2, i32 3, i32 4, i32 5, i32 6, i32 7>
  %smear_end_init574 = insertelement <8 x i32> poison, i32 %ret.i.i, i64 0
  %smear_end575 = shufflevector <8 x i32> %smear_end_init574, <8 x i32> poison, <8 x i32> zeroinitializer
  %cmp576 = icmp slt <8 x i32> %iter_val573, %smear_end575
  %cmp576_to_boolvec = sext <8 x i1> %cmp576 to <8 x i32>
  %mul__index_load581.elt0 = shl nsw i32 %counter.1.lcssa, 2
  %63 = sext i32 %mul__index_load581.elt0 to i64
  %ptr3406 = getelementptr i8, ptr %Results_ptr, i64 %63
  call void @llvm.masked.store.v8f32.p0(<8 x float> splat (float 0xFFFFFFFFE0000000), ptr %ptr3406, i32 1, <8 x i1> %cmp576)
  %mul__index_load589 = shl nsw <8 x i32> %iter_val573, splat (i32 3)
  %mask.i4220 = bitcast <8 x i32> %cmp576_to_boolvec to <8 x float>
  %v_1.i4221 = tail call <8 x float> @llvm.x86.avx2.gather.d.ps.256(<8 x float> undef, ptr readonly %QueryPositions_ptr, <8 x i32> %mul__index_load589, <8 x float> %mask.i4220, i8 1)
  %64 = or disjoint <8 x i32> %mul__index_load589, splat (i32 4)
  %v_1.i4224 = tail call <8 x float> @llvm.x86.avx2.gather.d.ps.256(<8 x float> undef, ptr readonly %QueryPositions_ptr, <8 x i32> %64, <8 x float> %mask.i4220, i8 1)
  %get_element610_broadcast = insertelement <8 x float> poison, float %GridOrigin_ptr_load_load.unpack, i64 0
  %get_element610_broadcast611 = shufflevector <8 x float> %get_element610_broadcast, <8 x float> poison, <8 x i32> zeroinitializer
  %get_element613_broadcast = insertelement <8 x float> poison, float %GridOrigin_ptr_load_load.unpack3386, i64 0
  %get_element613_broadcast614 = shufflevector <8 x float> %get_element613_broadcast, <8 x float> poison, <8 x i32> zeroinitializer
  %sub_a14_x_b26_x.i3926 = fsub <8 x float> %v_1.i4221, %get_element610_broadcast611
  %sub_a19_y_b211_y.i3927 = fsub <8 x float> %v_1.i4224, %get_element613_broadcast614
  %GridResolutionInv_load619_broadcast = insertelement <8 x float> poison, float %GridResolutionInv_ptr_load_load, i64 0
  %GridResolutionInv_load619_broadcast620 = shufflevector <8 x float> %GridResolutionInv_load619_broadcast, <8 x float> poison, <8 x i32> zeroinitializer
  %mul_v14_x_s_load.i3933 = fmul <8 x float> %GridResolutionInv_load619_broadcast620, %sub_a14_x_b26_x.i3926
  %mul_v17_y_s_load9.i3934 = fmul <8 x float> %GridResolutionInv_load619_broadcast620, %sub_a19_y_b211_y.i3927
  %call.i.i.i3940 = tail call <8 x float> @llvm.x86.avx.round.ps.256(<8 x float> %mul_v14_x_s_load.i3933, i32 9)
  %call.i.i3.i3941 = tail call <8 x float> @llvm.x86.avx.round.ps.256(<8 x float> %mul_v17_y_s_load9.i3934, i32 9)
  %v12_x_to_int32.i3947 = fptosi <8 x float> %call.i.i.i3940 to <8 x i32>
  %v14_y_to_int32.i3948 = fptosi <8 x float> %call.i.i3.i3941 to <8 x i32>
  %get_element635_broadcast = insertelement <8 x i32> poison, i32 %GridDimensions_ptr_load_load.unpack, i64 0
  %get_element635_broadcast636 = shufflevector <8 x i32> %get_element635_broadcast, <8 x i32> poison, <8 x i32> zeroinitializer
  %get_element638_broadcast = insertelement <8 x i32> poison, i32 %GridDimensions_ptr_load_load.unpack3389, i64 0
  %get_element638_broadcast639 = shufflevector <8 x i32> %get_element638_broadcast, <8 x i32> poison, <8 x i32> zeroinitializer
  %sub_a14_x_b_load.i3958 = add nsw <8 x i32> %get_element635_broadcast636, splat (i32 -1)
  %sub_a17_y_b_load9.i3959 = add nsw <8 x i32> %get_element638_broadcast639, splat (i32 -1)
  %65 = call <8 x i32> @llvm.smax.v8i32(<8 x i32> %v12_x_to_int32.i3947, <8 x i32> zeroinitializer)
  %66 = call <8 x i32> @llvm.smax.v8i32(<8 x i32> %v14_y_to_int32.i3948, <8 x i32> zeroinitializer)
  %blend.i16.i.v = call <8 x i32> @llvm.smin.v8i32(<8 x i32> %65, <8 x i32> %sub_a14_x_b_load.i3958)
  %blend.i20.i.v = call <8 x i32> @llvm.smin.v8i32(<8 x i32> %66, <8 x i32> %sub_a17_y_b_load9.i3959)
  %67 = bitcast <8 x i1> %cmp576 to i8
  %cmp.i3840.not4652 = icmp eq i8 %67, 0
  br i1 %cmp.i3840.not4652, label %for_exit652, label %for_loop650.lr.ph

for_loop650.lr.ph:                                ; preds = %partial_inner_only
  %SquaredEpsilonSelf_load872_broadcast = insertelement <8 x float> poison, float %SquaredEpsilonSelf_ptr_load_load, i64 0
  %SquaredEpsilonSelf_load872_broadcast873 = shufflevector <8 x float> %SquaredEpsilonSelf_load872_broadcast, <8 x float> poison, <8 x i32> zeroinitializer
  br label %for_loop650

not_all_continued_or_breaked690:                  ; preds = %for_loop650
  %new_mask701 = xor <8 x i32> %"oldMask&test675", %"oldMask&test6594656"
  %68 = icmp slt <8 x i32> %new_mask701, zeroinitializer
  %69 = bitcast <8 x i1> %68 to i8
  %cmp.i3857.not4645 = icmp eq i8 %69, 0
  br i1 %cmp.i3857.not4645, label %for_step651, label %for_loop705

for_loop705:                                      ; preds = %not_all_continued_or_breaked690, %for_step706
  %70 = phi i8 [ %74, %for_step706 ], [ %69, %not_all_continued_or_breaked690 ]
  %"oldMask&test7144649" = phi <8 x i32> [ %"oldMask&test714", %for_step706 ], [ %new_mask701, %not_all_continued_or_breaked690 ]
  %dy711.04648 = phi <8 x i32> [ %dy_load944_plus1, %for_step706 ], [ splat (i32 -1), %not_all_continued_or_breaked690 ]
  %bestIdx648.24647 = phi <8 x i32> [ %bestIdx648.3, %for_step706 ], [ %bestIdx648.04654, %not_all_continued_or_breaked690 ]
  %bestDistSq647.24646 = phi <8 x float> [ %bestDistSq647.3, %for_step706 ], [ %bestDistSq647.04653, %not_all_continued_or_breaked690 ]
  %add_cell607722_y_dy_load724 = add nsw <8 x i32> %dy711.04648, %blend.i20.i.v
  %greaterequal_ny_load725_GridDimensions726_y_broadcast728.not = icmp ult <8 x i32> %add_cell607722_y_dy_load724, %get_element638_broadcast639
  %"oldMask&test730" = select <8 x i1> %greaterequal_ny_load725_GridDimensions726_y_broadcast728.not, <8 x i32> zeroinitializer, <8 x i32> %"oldMask&test7144649"
  %71 = icmp slt <8 x i32> %"oldMask&test730", zeroinitializer
  %72 = bitcast <8 x i1> %71 to i8
  %"equal_finished&func743_internal_mask&function_mask720" = icmp eq i8 %70, %72
  br i1 %"equal_finished&func743_internal_mask&function_mask720", label %for_step706, label %not_all_continued_or_breaked745

for_step706:                                      ; preds = %not_all_continued_or_breaked811, %for_step827, %not_all_continued_or_breaked745, %for_loop705
  %bestDistSq647.3 = phi <8 x float> [ %bestDistSq647.24646, %for_loop705 ], [ %bestDistSq647.24646, %not_all_continued_or_breaked745 ], [ %bestDistSq647.24646, %not_all_continued_or_breaked811 ], [ %bestDistSq647.5, %for_step827 ]
  %bestIdx648.3 = phi <8 x i32> [ %bestIdx648.24647, %for_loop705 ], [ %bestIdx648.24647, %not_all_continued_or_breaked745 ], [ %bestIdx648.24647, %not_all_continued_or_breaked811 ], [ %bestIdx648.5, %for_step827 ]
  %continue_lanes_memory709.1 = phi <8 x i32> [ %"oldMask&test730", %for_loop705 ], [ %"mask|continueMask800", %not_all_continued_or_breaked745 ], [ %"mask|continueMask800", %not_all_continued_or_breaked811 ], [ %"mask|continueMask800", %for_step827 ]
  %internal_mask_memory.12 = phi <8 x i32> [ zeroinitializer, %for_loop705 ], [ zeroinitializer, %not_all_continued_or_breaked745 ], [ %new_mask822, %not_all_continued_or_breaked811 ], [ %new_mask822, %for_step827 ]
  %"mask|continue_mask941" = or <8 x i32> %internal_mask_memory.12, %continue_lanes_memory709.1
  %dy_load944_plus1 = add nsw <8 x i32> %dy711.04648, splat (i32 1)
  %lessequal_dy_load712_.inv = icmp sgt <8 x i32> %dy711.04648, zeroinitializer
  %"oldMask&test714" = select <8 x i1> %lessequal_dy_load712_.inv, <8 x i32> zeroinitializer, <8 x i32> %"mask|continue_mask941"
  %73 = icmp slt <8 x i32> %"oldMask&test714", zeroinitializer
  %74 = bitcast <8 x i1> %73 to i8
  %cmp.i3857.not = icmp eq i8 %74, 0
  br i1 %cmp.i3857.not, label %for_step651, label %for_loop705, !llvm.loop !43

not_all_continued_or_breaked745:                  ; preds = %for_loop705
  %new_mask756 = xor <8 x i32> %"oldMask&test730", %"oldMask&test7144649"
  %mul_ny_load760_GridDimensions761_x_broadcast763 = mul nsw <8 x i32> %add_cell607722_y_dy_load724, %get_element635_broadcast636
  %add_mul_ny_load760_GridDimensions761_x_broadcast763_nx_load764 = add nsw <8 x i32> %mul_ny_load760_GridDimensions761_x_broadcast763, %add_cell607667_x_dx_load669
  %CellStartEnd_load767768__data = load ptr, ptr %CellStartEnd, align 8
  %mul__cellHash_load766 = shl nsw <8 x i32> %add_mul_ny_load760_GridDimensions761_x_broadcast763_nx_load764, splat (i32 3)
  %v_1.i4226 = tail call <8 x i32> @llvm.x86.avx2.gather.d.d.256(<8 x i32> undef, ptr readonly %CellStartEnd_load767768__data, <8 x i32> %mul__cellHash_load766, <8 x i32> %new_mask756, i8 1)
  %75 = or disjoint <8 x i32> %mul__cellHash_load766, splat (i32 4)
  %v_1.i4228 = tail call <8 x i32> @llvm.x86.avx2.gather.d.d.256(<8 x i32> undef, ptr readonly %CellStartEnd_load767768__data, <8 x i32> %75, <8 x i32> %new_mask756, i8 1)
  %isneg3392 = icmp slt <8 x i32> %v_1.i4226, zeroinitializer
  %"oldMask&test795" = select <8 x i1> %isneg3392, <8 x i32> %new_mask756, <8 x i32> zeroinitializer
  %"mask|continueMask800" = or <8 x i32> %"oldMask&test795", %"oldMask&test730"
  %76 = icmp slt <8 x i32> %"mask|continueMask800", zeroinitializer
  %77 = bitcast <8 x i1> %76 to i8
  %"equal_finished&func808_internal_mask&function_mask720" = icmp eq i8 %70, %77
  br i1 %"equal_finished&func808_internal_mask&function_mask720", label %for_step706, label %not_all_continued_or_breaked811

not_all_continued_or_breaked811:                  ; preds = %not_all_continued_or_breaked745
  %new_mask822 = xor <8 x i32> %"mask|continueMask800", %"oldMask&test7144649"
  %less_i_load834_end_load8354636 = icmp slt <8 x i32> %v_1.i4226, %v_1.i4228
  %"oldMask&test8374637" = select <8 x i1> %less_i_load834_end_load8354636, <8 x i32> %new_mask822, <8 x i32> zeroinitializer
  %78 = icmp slt <8 x i32> %"oldMask&test8374637", zeroinitializer
  %79 = bitcast <8 x i1> %78 to i8
  %cmp.i3861.not4638 = icmp eq i8 %79, 0
  br i1 %cmp.i3861.not4638, label %for_step706, label %for_loop826

for_loop826:                                      ; preds = %not_all_continued_or_breaked811, %for_step827
  %80 = phi i8 [ %85, %for_step827 ], [ %79, %not_all_continued_or_breaked811 ]
  %"oldMask&test8374642" = phi <8 x i32> [ %"oldMask&test837", %for_step827 ], [ %"oldMask&test8374637", %not_all_continued_or_breaked811 ]
  %i832.04641 = phi <8 x i32> [ %i_load933_plus1, %for_step827 ], [ %v_1.i4226, %not_all_continued_or_breaked811 ]
  %bestIdx648.44640 = phi <8 x i32> [ %bestIdx648.5, %for_step827 ], [ %bestIdx648.24647, %not_all_continued_or_breaked811 ]
  %bestDistSq647.44639 = phi <8 x float> [ %bestDistSq647.5, %for_step827 ], [ %bestDistSq647.24646, %not_all_continued_or_breaked811 ]
  %mul__i_load845 = shl nsw <8 x i32> %i832.04641, splat (i32 3)
  %mask.i4230 = bitcast <8 x i32> %"oldMask&test8374642" to <8 x float>
  %v_1.i4231 = tail call <8 x float> @llvm.x86.avx2.gather.d.ps.256(<8 x float> undef, ptr readonly %SortedPositions_ptr, <8 x i32> %mul__i_load845, <8 x float> %mask.i4230, i8 1)
  %81 = or disjoint <8 x i32> %mul__i_load845, splat (i32 4)
  %v_1.i4234 = tail call <8 x float> @llvm.x86.avx2.gather.d.ps.256(<8 x float> undef, ptr readonly %SortedPositions_ptr, <8 x i32> %81, <8 x float> %mask.i4230, i8 1)
  %sub_a14_x_b26_x.i.i3983 = fsub <8 x float> %v_1.i4231, %v_1.i4221
  %sub_a19_y_b211_y.i.i3984 = fsub <8 x float> %v_1.i4234, %v_1.i4224
  %mul_a13_x_b25_x.i.i.i3992 = fmul <8 x float> %sub_a14_x_b26_x.i.i3983, %sub_a14_x_b26_x.i.i3983
  %mul_a17_y_b29_y.i.i.i3993 = fmul <8 x float> %sub_a19_y_b211_y.i.i3984, %sub_a19_y_b211_y.i.i3984
  %add_mul_a13_x_b25_x_mul_a17_y_b29_y.i.i.i3994 = fadd <8 x float> %mul_a13_x_b25_x.i.i.i3992, %mul_a17_y_b29_y.i.i.i3993
  %less_distSq_load871_SquaredEpsilonSelf_load872_broadcast873 = fcmp olt <8 x float> %add_mul_a13_x_b25_x_mul_a17_y_b29_y.i.i.i3994, %SquaredEpsilonSelf_load872_broadcast873
  %"oldMask&test876" = select <8 x i1> %less_distSq_load871_SquaredEpsilonSelf_load872_broadcast873, <8 x i32> %"oldMask&test8374642", <8 x i32> zeroinitializer
  %82 = icmp slt <8 x i32> %"oldMask&test876", zeroinitializer
  %83 = bitcast <8 x i1> %82 to i8
  %"equal_finished&func889_internal_mask&function_mask843" = icmp eq i8 %80, %83
  br i1 %"equal_finished&func889_internal_mask&function_mask843", label %for_step827, label %not_all_continued_or_breaked891

for_step827:                                      ; preds = %for_loop826, %not_all_continued_or_breaked891
  %bestDistSq647.5 = phi <8 x float> [ %bestDistSq647.44639, %for_loop826 ], [ %blend.i.i4238, %not_all_continued_or_breaked891 ]
  %bestIdx648.5 = phi <8 x i32> [ %bestIdx648.44640, %for_loop826 ], [ %88, %not_all_continued_or_breaked891 ]
  %internal_mask_memory.14 = phi <8 x i32> [ zeroinitializer, %for_loop826 ], [ %new_mask902, %not_all_continued_or_breaked891 ]
  %"mask|continue_mask930" = or <8 x i32> %internal_mask_memory.14, %"oldMask&test876"
  %i_load933_plus1 = add nsw <8 x i32> %i832.04641, splat (i32 1)
  %less_i_load834_end_load835 = icmp slt <8 x i32> %i_load933_plus1, %v_1.i4228
  %"oldMask&test837" = select <8 x i1> %less_i_load834_end_load835, <8 x i32> %"mask|continue_mask930", <8 x i32> zeroinitializer
  %84 = icmp slt <8 x i32> %"oldMask&test837", zeroinitializer
  %85 = bitcast <8 x i1> %84 to i8
  %cmp.i3861.not = icmp eq i8 %85, 0
  br i1 %cmp.i3861.not, label %for_step706, label %for_loop826, !llvm.loop !44

not_all_continued_or_breaked891:                  ; preds = %for_loop826
  %new_mask902 = xor <8 x i32> %"oldMask&test876", %"oldMask&test8374642"
  %less_distSq_load905_bestDistSq_load906 = fcmp olt <8 x float> %add_mul_a13_x_b25_x_mul_a17_y_b29_y.i.i.i3994, %bestDistSq647.44639
  %86 = bitcast <8 x i32> %new_mask902 to <8 x float>
  %mask_as_float.i.i4236 = select <8 x i1> %less_distSq_load905_bestDistSq_load906, <8 x float> %86, <8 x float> zeroinitializer
  %blend.i.i4238 = call <8 x float> @llvm.x86.avx.blendv.ps.256(<8 x float> %bestDistSq647.44639, <8 x float> %add_mul_a13_x_b25_x_mul_a17_y_b29_y.i.i.i3994, <8 x float> %mask_as_float.i.i4236)
  %87 = bitcast <8 x i32> %bestIdx648.44640 to <8 x float>
  %newAsFloat.i4241 = bitcast <8 x i32> %i832.04641 to <8 x float>
  %blend.i4242 = call <8 x float> @llvm.x86.avx.blendv.ps.256(<8 x float> %87, <8 x float> %newAsFloat.i4241, <8 x float> %mask_as_float.i.i4236)
  %88 = bitcast <8 x float> %blend.i4242 to <8 x i32>
  br label %for_step827

safe_if_after_true964:                            ; preds = %safe_if_run_true965, %for_exit652
  %"oldMask&~test989" = select <8 x i1> %notequal_bestIdx_load961_, <8 x i32> zeroinitializer, <8 x i32> %cmp576_to_boolvec
  %not.notequal_bestIdx_load961_ = xor <8 x i1> %notequal_bestIdx_load961_, splat (i1 true)
  %89 = select <8 x i1> %not.notequal_bestIdx_load961_, <8 x i1> %cmp576, <8 x i1> zeroinitializer
  %90 = bitcast <8 x i1> %89 to i8
  %cmp.i3864.not = icmp eq i8 %90, 0
  br i1 %cmp.i3864.not, label %common.ret, label %for_test994.preheader

for_test994.preheader:                            ; preds = %safe_if_after_true964
  %SortedLength_load1003_broadcast = insertelement <8 x i32> poison, i32 %SortedLength_ptr_load_load, i64 0
  %SortedLength_load1003_broadcast1004 = shufflevector <8 x i32> %SortedLength_load1003_broadcast, <8 x i32> poison, <8 x i32> zeroinitializer
  %less_i_load1002_SortedLength_load1003_broadcast10044659 = icmp sgt <8 x i32> %SortedLength_load1003_broadcast1004, zeroinitializer
  %"oldMask&test10064660" = select <8 x i1> %less_i_load1002_SortedLength_load1003_broadcast10044659, <8 x i32> %"oldMask&~test989", <8 x i32> zeroinitializer
  %91 = icmp slt <8 x i32> %"oldMask&test10064660", zeroinitializer
  %92 = bitcast <8 x i1> %91 to i8
  %cmp.i3865.not4661 = icmp eq i8 %92, 0
  br i1 %cmp.i3865.not4661, label %for_exit997, label %for_loop995.lr.ph

for_loop995.lr.ph:                                ; preds = %for_test994.preheader
  %invariant.gep4669 = getelementptr i8, ptr %SortedPositions_ptr, i64 4
  %SquaredEpsilonSelf_load1041_broadcast = insertelement <8 x float> poison, float %SquaredEpsilonSelf_ptr_load_load, i64 0
  %SquaredEpsilonSelf_load1041_broadcast1042 = shufflevector <8 x float> %SquaredEpsilonSelf_load1041_broadcast, <8 x float> poison, <8 x i32> zeroinitializer
  br label %for_loop995

safe_if_run_true965:                              ; preds = %for_exit652
  %"oldMask&test966" = select <8 x i1> %notequal_bestIdx_load961_, <8 x i32> %cmp576_to_boolvec, <8 x i32> zeroinitializer
  %mul__bestIdx_load974 = shl nsw <8 x i32> %bestIdx648.0.lcssa, splat (i32 3)
  %93 = or disjoint <8 x i32> %mul__bestIdx_load974, splat (i32 4)
  %new_add3503 = sext <8 x i32> %93 to <8 x i64>
  %vecmask_1.i4243 = shufflevector <8 x i32> %"oldMask&test966", <8 x i32> poison, <4 x i32> <i32 0, i32 1, i32 2, i32 3>
  %vecmask_2.i4244 = shufflevector <8 x i32> %"oldMask&test966", <8 x i32> poison, <4 x i32> <i32 4, i32 5, i32 6, i32 7>
  %offsets_1.i4245 = shufflevector <8 x i64> %new_add3503, <8 x i64> poison, <4 x i32> <i32 0, i32 1, i32 2, i32 3>
  %offsets_2.i4246 = shufflevector <8 x i64> %new_add3503, <8 x i64> poison, <4 x i32> <i32 4, i32 5, i32 6, i32 7>
  %v1_1.i4247 = tail call <4 x i32> @llvm.x86.avx2.gather.q.d.256(<4 x i32> undef, ptr %HashIndex_ptr, <4 x i64> %offsets_1.i4245, <4 x i32> %vecmask_1.i4243, i8 1)
  %v2_1.i4248 = tail call <4 x i32> @llvm.x86.avx2.gather.q.d.256(<4 x i32> undef, ptr %HashIndex_ptr, <4 x i64> %offsets_2.i4246, <4 x i32> %vecmask_2.i4244, i8 1)
  %v.i4249 = shufflevector <4 x i32> %v1_1.i4247, <4 x i32> %v2_1.i4248, <8 x i32> <i32 0, i32 1, i32 2, i32 3, i32 4, i32 5, i32 6, i32 7>
  %val.i4250 = bitcast <8 x i32> %v.i4249 to <8 x float>
  call void @llvm.x86.avx.maskstore.ps.256(ptr %ptr3406, <8 x i32> %"oldMask&test966", <8 x float> %val.i4250)
  br label %safe_if_after_true964

for_loop995:                                      ; preds = %for_loop995.lr.ph, %for_step996
  %indvars.iv4689 = phi i64 [ 0, %for_loop995.lr.ph ], [ %indvars.iv.next4690, %for_step996 ]
  %94 = phi i8 [ %92, %for_loop995.lr.ph ], [ %99, %for_step996 ]
  %"oldMask&test10064667" = phi <8 x i32> [ %"oldMask&test10064660", %for_loop995.lr.ph ], [ %"oldMask&test1006", %for_step996 ]
  %i1001.04666 = phi <8 x i32> [ zeroinitializer, %for_loop995.lr.ph ], [ %i_load1102_plus1, %for_step996 ]
  %bestIdx648.64663 = phi <8 x i32> [ %bestIdx648.0.lcssa, %for_loop995.lr.ph ], [ %bestIdx648.7, %for_step996 ]
  %bestDistSq647.64662 = phi <8 x float> [ %bestDistSq647.0.lcssa, %for_loop995.lr.ph ], [ %bestDistSq647.7, %for_step996 ]
  %95 = shl nsw i64 %indvars.iv4689, 3
  %ptr3520 = getelementptr i8, ptr %SortedPositions_ptr, i64 %95
  %SortedPositions_ptr_load1015_offset_load35193521 = load float, ptr %ptr3520, align 4
  %SortedPositions_ptr_load1015_offset_load35193522 = insertelement <8 x float> poison, float %SortedPositions_ptr_load1015_offset_load35193521, i64 0
  %SortedPositions_ptr_load1015_offset_load35193523 = shufflevector <8 x float> %SortedPositions_ptr_load1015_offset_load35193522, <8 x float> poison, <8 x i32> zeroinitializer
  %gep4670 = getelementptr i8, ptr %invariant.gep4669, i64 %95
  %SortedPositions_ptr_load1015_offset_load103035293534 = load float, ptr %gep4670, align 4
  %SortedPositions_ptr_load1015_offset_load103035293535 = insertelement <8 x float> poison, float %SortedPositions_ptr_load1015_offset_load103035293534, i64 0
  %SortedPositions_ptr_load1015_offset_load103035293536 = shufflevector <8 x float> %SortedPositions_ptr_load1015_offset_load103035293535, <8 x float> poison, <8 x i32> zeroinitializer
  %sub_a14_x_b26_x.i.i3995 = fsub <8 x float> %SortedPositions_ptr_load1015_offset_load35193523, %v_1.i4221
  %sub_a19_y_b211_y.i.i3996 = fsub <8 x float> %SortedPositions_ptr_load1015_offset_load103035293536, %v_1.i4224
  %mul_a13_x_b25_x.i.i.i4004 = fmul <8 x float> %sub_a14_x_b26_x.i.i3995, %sub_a14_x_b26_x.i.i3995
  %mul_a17_y_b29_y.i.i.i4005 = fmul <8 x float> %sub_a19_y_b211_y.i.i3996, %sub_a19_y_b211_y.i.i3996
  %add_mul_a13_x_b25_x_mul_a17_y_b29_y.i.i.i4006 = fadd <8 x float> %mul_a13_x_b25_x.i.i.i4004, %mul_a17_y_b29_y.i.i.i4005
  %less_distSq_load1040_SquaredEpsilonSelf_load1041_broadcast1042 = fcmp olt <8 x float> %add_mul_a13_x_b25_x_mul_a17_y_b29_y.i.i.i4006, %SquaredEpsilonSelf_load1041_broadcast1042
  %"oldMask&test1045" = select <8 x i1> %less_distSq_load1040_SquaredEpsilonSelf_load1041_broadcast1042, <8 x i32> %"oldMask&test10064667", <8 x i32> zeroinitializer
  %96 = icmp slt <8 x i32> %"oldMask&test1045", zeroinitializer
  %97 = bitcast <8 x i1> %96 to i8
  %"equal_finished&func1058_internal_mask&function_mask1012" = icmp eq i8 %94, %97
  br i1 %"equal_finished&func1058_internal_mask&function_mask1012", label %for_step996, label %not_all_continued_or_breaked1060

for_step996:                                      ; preds = %for_loop995, %not_all_continued_or_breaked1060
  %bestDistSq647.7 = phi <8 x float> [ %bestDistSq647.64662, %for_loop995 ], [ %blend.i.i4253, %not_all_continued_or_breaked1060 ]
  %bestIdx648.7 = phi <8 x i32> [ %bestIdx648.64663, %for_loop995 ], [ %104, %not_all_continued_or_breaked1060 ]
  %internal_mask_memory.16 = phi <8 x i32> [ zeroinitializer, %for_loop995 ], [ %new_mask1071, %not_all_continued_or_breaked1060 ]
  %"mask|continue_mask1099" = or <8 x i32> %internal_mask_memory.16, %"oldMask&test1045"
  %indvars.iv.next4690 = add nuw nsw i64 %indvars.iv4689, 1
  %i_load1102_plus1 = add nuw nsw <8 x i32> %i1001.04666, splat (i32 1)
  %less_i_load1002_SortedLength_load1003_broadcast1004 = icmp slt <8 x i32> %i_load1102_plus1, %SortedLength_load1003_broadcast1004
  %"oldMask&test1006" = select <8 x i1> %less_i_load1002_SortedLength_load1003_broadcast1004, <8 x i32> %"mask|continue_mask1099", <8 x i32> zeroinitializer
  %98 = icmp slt <8 x i32> %"oldMask&test1006", zeroinitializer
  %99 = bitcast <8 x i1> %98 to i8
  %cmp.i3865.not = icmp eq i8 %99, 0
  br i1 %cmp.i3865.not, label %for_exit997, label %for_loop995, !llvm.loop !45

for_exit997:                                      ; preds = %for_step996, %for_test994.preheader
  %bestIdx648.6.lcssa = phi <8 x i32> [ %bestIdx648.0.lcssa, %for_test994.preheader ], [ %bestIdx648.7, %for_step996 ]
  %notequal_bestIdx_load1108_ = icmp eq <8 x i32> %bestIdx648.6.lcssa, splat (i32 -1)
  %"oldMask&test1113" = select <8 x i1> %notequal_bestIdx_load1108_, <8 x i32> zeroinitializer, <8 x i32> %"oldMask&~test989"
  %100 = icmp slt <8 x i32> %"oldMask&test1113", zeroinitializer
  %101 = bitcast <8 x i1> %100 to i8
  %cmp.i3868.not = icmp eq i8 %101, 0
  br i1 %cmp.i3868.not, label %common.ret, label %safe_if_run_true1112

not_all_continued_or_breaked1060:                 ; preds = %for_loop995
  %new_mask1071 = xor <8 x i32> %"oldMask&test1045", %"oldMask&test10064667"
  %less_distSq_load1074_bestDistSq_load1075 = fcmp olt <8 x float> %add_mul_a13_x_b25_x_mul_a17_y_b29_y.i.i.i4006, %bestDistSq647.64662
  %102 = bitcast <8 x i32> %new_mask1071 to <8 x float>
  %mask_as_float.i.i4251 = select <8 x i1> %less_distSq_load1074_bestDistSq_load1075, <8 x float> %102, <8 x float> zeroinitializer
  %blend.i.i4253 = call <8 x float> @llvm.x86.avx.blendv.ps.256(<8 x float> %bestDistSq647.64662, <8 x float> %add_mul_a13_x_b25_x_mul_a17_y_b29_y.i.i.i4006, <8 x float> %mask_as_float.i.i4251)
  %103 = bitcast <8 x i32> %bestIdx648.64663 to <8 x float>
  %newAsFloat.i4256 = bitcast <8 x i32> %i1001.04666 to <8 x float>
  %blend.i4257 = call <8 x float> @llvm.x86.avx.blendv.ps.256(<8 x float> %103, <8 x float> %newAsFloat.i4256, <8 x float> %mask_as_float.i.i4251)
  %104 = bitcast <8 x float> %blend.i4257 to <8 x i32>
  br label %for_step996

safe_if_run_true1112:                             ; preds = %for_exit997
  %mul__bestIdx_load1121 = shl nsw <8 x i32> %bestIdx648.6.lcssa, splat (i32 3)
  %105 = or disjoint <8 x i32> %mul__bestIdx_load1121, splat (i32 4)
  %new_add3541 = sext <8 x i32> %105 to <8 x i64>
  %vecmask_1.i4258 = shufflevector <8 x i32> %"oldMask&test1113", <8 x i32> poison, <4 x i32> <i32 0, i32 1, i32 2, i32 3>
  %vecmask_2.i4259 = shufflevector <8 x i32> %"oldMask&test1113", <8 x i32> poison, <4 x i32> <i32 4, i32 5, i32 6, i32 7>
  %offsets_1.i4260 = shufflevector <8 x i64> %new_add3541, <8 x i64> poison, <4 x i32> <i32 0, i32 1, i32 2, i32 3>
  %offsets_2.i4261 = shufflevector <8 x i64> %new_add3541, <8 x i64> poison, <4 x i32> <i32 4, i32 5, i32 6, i32 7>
  %v1_1.i4262 = tail call <4 x i32> @llvm.x86.avx2.gather.q.d.256(<4 x i32> undef, ptr %HashIndex_ptr, <4 x i64> %offsets_1.i4260, <4 x i32> %vecmask_1.i4258, i8 1)
  %v2_1.i4263 = tail call <4 x i32> @llvm.x86.avx2.gather.q.d.256(<4 x i32> undef, ptr %HashIndex_ptr, <4 x i64> %offsets_2.i4261, <4 x i32> %vecmask_2.i4259, i8 1)
  %v.i4264 = shufflevector <4 x i32> %v1_1.i4262, <4 x i32> %v2_1.i4263, <8 x i32> <i32 0, i32 1, i32 2, i32 3, i32 4, i32 5, i32 6, i32 7>
  %val.i4265 = bitcast <8 x i32> %v.i4264 to <8 x float>
  call void @llvm.x86.avx.maskstore.ps.256(ptr %ptr3406, <8 x i32> %"oldMask&test1113", <8 x float> %val.i4265)
  br label %common.ret

foreach_full_body1200:                            ; preds = %foreach_full_body1200.lr.ph, %if_done1636
  %indvars.iv4673 = phi i64 [ %2, %foreach_full_body1200.lr.ph ], [ %indvars.iv.next4674, %if_done1636 ]
  %106 = trunc nsw i64 %indvars.iv4673 to i32
  %smear_counter_init1251 = insertelement <8 x i32> poison, i32 %106, i64 0
  %smear_counter1252 = shufflevector <8 x i32> %smear_counter_init1251, <8 x i32> poison, <8 x i32> zeroinitializer
  %iter_val1253 = add nsw <8 x i32> %smear_counter1252, <i32 0, i32 1, i32 2, i32 3, i32 4, i32 5, i32 6, i32 7>
  %107 = shl nsw i64 %indvars.iv4673, 2
  %ptr3560 = getelementptr i8, ptr %Results_ptr, i64 %107
  store <8 x i32> splat (i32 -1), ptr %ptr3560, align 4, !filename !11, !first_line !36, !first_column !13, !last_line !36, !last_column !14
  %mul__index_load1262 = shl nsw <8 x i32> %iter_val1253, splat (i32 3)
  %v_1.i4266 = tail call <8 x float> @llvm.x86.avx2.gather.d.ps.256(<8 x float> undef, ptr readonly %QueryPositions_ptr, <8 x i32> %mul__index_load1262, <8 x float> splat (float 0xFFFFFFFFE0000000), i8 1)
  %108 = or disjoint <8 x i32> %mul__index_load1262, splat (i32 4)
  %v_1.i4268 = tail call <8 x float> @llvm.x86.avx2.gather.d.ps.256(<8 x float> undef, ptr readonly %QueryPositions_ptr, <8 x i32> %108, <8 x float> splat (float 0xFFFFFFFFE0000000), i8 1)
  %sub_a14_x_b26_x.i4007 = fsub <8 x float> %v_1.i4266, %get_element1283_broadcast1284
  %sub_a19_y_b211_y.i4008 = fsub <8 x float> %v_1.i4268, %get_element1286_broadcast1287
  %mul_v14_x_s_load.i4014 = fmul <8 x float> %GridResolutionInv_load1292_broadcast1293, %sub_a14_x_b26_x.i4007
  %mul_v17_y_s_load9.i4015 = fmul <8 x float> %GridResolutionInv_load1292_broadcast1293, %sub_a19_y_b211_y.i4008
  %call.i.i.i4021 = tail call <8 x float> @llvm.x86.avx.round.ps.256(<8 x float> %mul_v14_x_s_load.i4014, i32 9)
  %call.i.i3.i4022 = tail call <8 x float> @llvm.x86.avx.round.ps.256(<8 x float> %mul_v17_y_s_load9.i4015, i32 9)
  %v12_x_to_int32.i4028 = fptosi <8 x float> %call.i.i.i4021 to <8 x i32>
  %v14_y_to_int32.i4029 = fptosi <8 x float> %call.i.i3.i4022 to <8 x i32>
  %109 = call <8 x i32> @llvm.smax.v8i32(<8 x i32> %v12_x_to_int32.i4028, <8 x i32> zeroinitializer)
  %110 = call <8 x i32> @llvm.smax.v8i32(<8 x i32> %v14_y_to_int32.i4029, <8 x i32> zeroinitializer)
  %blend.i4281.v = call <8 x i32> @llvm.smin.v8i32(<8 x i32> %109, <8 x i32> %sub_a14_x_b_load.i4039)
  %blend.i4285.v = call <8 x i32> @llvm.smin.v8i32(<8 x i32> %110, <8 x i32> %sub_a17_y_b_load9.i4040)
  br label %for_loop1323

for_loop1323:                                     ; preds = %foreach_full_body1200, %for_step1324
  %111 = phi i8 [ -1, %foreach_full_body1200 ], [ %115, %for_step1324 ]
  %"oldMask&test13324552" = phi <8 x i32> [ splat (i32 -1), %foreach_full_body1200 ], [ %"oldMask&test1332", %for_step1324 ]
  %dx1329.04551 = phi <8 x i32> [ splat (i32 -1), %foreach_full_body1200 ], [ %dx_load1628_plus1, %for_step1324 ]
  %bestIdx1321.04550 = phi <8 x i32> [ splat (i32 -1), %foreach_full_body1200 ], [ %bestIdx1321.1, %for_step1324 ]
  %bestDistSq1320.04549 = phi <8 x float> [ splat (float 0x47EFFFFFE0000000), %foreach_full_body1200 ], [ %bestDistSq1320.1, %for_step1324 ]
  %add_cell12801340_x_dx_load1342 = add nsw <8 x i32> %dx1329.04551, %blend.i4281.v
  %greaterequal_nx_load1343_GridDimensions11841344_x_broadcast1346.not = icmp ult <8 x i32> %add_cell12801340_x_dx_load1342, %get_element1308_broadcast1309
  %"oldMask&test1348" = select <8 x i1> %greaterequal_nx_load1343_GridDimensions11841344_x_broadcast1346.not, <8 x i32> zeroinitializer, <8 x i32> %"oldMask&test13324552"
  %112 = icmp slt <8 x i32> %"oldMask&test1348", zeroinitializer
  %113 = bitcast <8 x i1> %112 to i8
  %"equal_finished&func1361_internal_mask&function_mask1338" = icmp eq i8 %111, %113
  br i1 %"equal_finished&func1361_internal_mask&function_mask1338", label %for_step1324, label %not_all_continued_or_breaked1363

for_step1324:                                     ; preds = %not_all_continued_or_breaked1363, %for_step1379, %for_loop1323
  %bestDistSq1320.1 = phi <8 x float> [ %bestDistSq1320.04549, %for_loop1323 ], [ %bestDistSq1320.04549, %not_all_continued_or_breaked1363 ], [ %bestDistSq1320.3, %for_step1379 ]
  %bestIdx1321.1 = phi <8 x i32> [ %bestIdx1321.04550, %for_loop1323 ], [ %bestIdx1321.04550, %not_all_continued_or_breaked1363 ], [ %bestIdx1321.3, %for_step1379 ]
  %internal_mask_memory.19 = phi <8 x i32> [ zeroinitializer, %for_loop1323 ], [ %new_mask1374, %not_all_continued_or_breaked1363 ], [ %new_mask1374, %for_step1379 ]
  %"mask|continue_mask1625" = or <8 x i32> %internal_mask_memory.19, %"oldMask&test1348"
  %dx_load1628_plus1 = add nsw <8 x i32> %dx1329.04551, splat (i32 1)
  %lessequal_dx_load1330_.inv = icmp sgt <8 x i32> %dx1329.04551, zeroinitializer
  %"oldMask&test1332" = select <8 x i1> %lessequal_dx_load1330_.inv, <8 x i32> zeroinitializer, <8 x i32> %"mask|continue_mask1625"
  %114 = icmp slt <8 x i32> %"oldMask&test1332", zeroinitializer
  %115 = bitcast <8 x i1> %114 to i8
  %cmp.i3870.not = icmp eq i8 %115, 0
  br i1 %cmp.i3870.not, label %for_exit1325, label %for_loop1323, !llvm.loop !46

for_exit1325:                                     ; preds = %for_step1324
  %notequal_bestIdx_load1634_ = icmp ne <8 x i32> %bestIdx1321.1, splat (i32 -1)
  %notequal_bestIdx_load1634__to_boolvec = sext <8 x i1> %notequal_bestIdx_load1634_ to <8 x i32>
  %116 = bitcast <8 x i1> %notequal_bestIdx_load1634_ to i8
  %cmp.i3873.not = icmp eq i8 %116, 0
  br i1 %cmp.i3873.not, label %safe_if_after_true1637, label %safe_if_run_true1638

for_loop1928:                                     ; preds = %for_loop1928.lr.ph, %for_step1929
  %117 = phi i8 [ %167, %for_loop1928.lr.ph ], [ %121, %for_step1929 ]
  %"oldMask&test19374586" = phi <8 x i32> [ %cmp1852_to_boolvec, %for_loop1928.lr.ph ], [ %"oldMask&test1937", %for_step1929 ]
  %dx1934.04585 = phi <8 x i32> [ splat (i32 -1), %for_loop1928.lr.ph ], [ %dx_load2233_plus1, %for_step1929 ]
  %bestIdx1926.04584 = phi <8 x i32> [ splat (i32 -1), %for_loop1928.lr.ph ], [ %bestIdx1926.1, %for_step1929 ]
  %bestDistSq1925.04583 = phi <8 x float> [ splat (float 0x47EFFFFFE0000000), %for_loop1928.lr.ph ], [ %bestDistSq1925.1, %for_step1929 ]
  %add_cell18851945_x_dx_load1947 = add nsw <8 x i32> %dx1934.04585, %blend.i16.i4441.v
  %greaterequal_nx_load1948_GridDimensions11841949_x_broadcast1951.not = icmp ult <8 x i32> %add_cell18851945_x_dx_load1947, %get_element1913_broadcast1914
  %"oldMask&test1953" = select <8 x i1> %greaterequal_nx_load1948_GridDimensions11841949_x_broadcast1951.not, <8 x i32> zeroinitializer, <8 x i32> %"oldMask&test19374586"
  %118 = icmp slt <8 x i32> %"oldMask&test1953", zeroinitializer
  %119 = bitcast <8 x i1> %118 to i8
  %"equal_finished&func1966_internal_mask&function_mask1943" = icmp eq i8 %117, %119
  br i1 %"equal_finished&func1966_internal_mask&function_mask1943", label %for_step1929, label %not_all_continued_or_breaked1968

for_step1929:                                     ; preds = %not_all_continued_or_breaked1968, %for_step1984, %for_loop1928
  %bestDistSq1925.1 = phi <8 x float> [ %bestDistSq1925.04583, %for_loop1928 ], [ %bestDistSq1925.04583, %not_all_continued_or_breaked1968 ], [ %bestDistSq1925.3, %for_step1984 ]
  %bestIdx1926.1 = phi <8 x i32> [ %bestIdx1926.04584, %for_loop1928 ], [ %bestIdx1926.04584, %not_all_continued_or_breaked1968 ], [ %bestIdx1926.3, %for_step1984 ]
  %internal_mask_memory.27 = phi <8 x i32> [ zeroinitializer, %for_loop1928 ], [ %new_mask1979, %not_all_continued_or_breaked1968 ], [ %new_mask1979, %for_step1984 ]
  %"mask|continue_mask2230" = or <8 x i32> %internal_mask_memory.27, %"oldMask&test1953"
  %dx_load2233_plus1 = add nsw <8 x i32> %dx1934.04585, splat (i32 1)
  %lessequal_dx_load1935_.inv = icmp sgt <8 x i32> %dx1934.04585, zeroinitializer
  %"oldMask&test1937" = select <8 x i1> %lessequal_dx_load1935_.inv, <8 x i32> zeroinitializer, <8 x i32> %"mask|continue_mask2230"
  %120 = icmp slt <8 x i32> %"oldMask&test1937", zeroinitializer
  %121 = bitcast <8 x i1> %120 to i8
  %cmp.i3874.not = icmp eq i8 %121, 0
  br i1 %cmp.i3874.not, label %for_exit1930, label %for_loop1928, !llvm.loop !47

for_exit1930:                                     ; preds = %for_step1929, %partial_inner_only1843
  %bestDistSq1925.0.lcssa = phi <8 x float> [ splat (float 0x47EFFFFFE0000000), %partial_inner_only1843 ], [ %bestDistSq1925.1, %for_step1929 ]
  %bestIdx1926.0.lcssa = phi <8 x i32> [ splat (i32 -1), %partial_inner_only1843 ], [ %bestIdx1926.1, %for_step1929 ]
  %notequal_bestIdx_load2239_ = icmp ne <8 x i32> %bestIdx1926.0.lcssa, splat (i32 -1)
  %122 = select <8 x i1> %notequal_bestIdx_load2239_, <8 x i1> %cmp1852, <8 x i1> zeroinitializer
  %123 = bitcast <8 x i1> %122 to i8
  %cmp.i3877.not = icmp eq i8 %123, 0
  br i1 %cmp.i3877.not, label %safe_if_after_true2242, label %safe_if_run_true2243

outer_not_in_extras1226.partial_inner_all_outer1244_crit_edge: ; preds = %if_done1636
  %124 = trunc nsw i64 %indvars.iv.next4674 to i32
  br label %partial_inner_all_outer1244, !llvm.loop !34

partial_inner_all_outer1244:                      ; preds = %outer_not_in_extras1226.partial_inner_all_outer1244_crit_edge, %outer_not_in_extras1226.preheader
  %counter1220.1.lcssa = phi i32 [ %124, %outer_not_in_extras1226.partial_inner_all_outer1244_crit_edge ], [ %__startIndex, %outer_not_in_extras1226.preheader ]
  %before_full_end1845 = icmp slt i32 %counter1220.1.lcssa, %ret.i.i
  br i1 %before_full_end1845, label %partial_inner_only1843, label %common.ret

not_all_continued_or_breaked1363:                 ; preds = %for_loop1323
  %new_mask1374 = xor <8 x i32> %"oldMask&test1348", %"oldMask&test13324552"
  %125 = icmp slt <8 x i32> %new_mask1374, zeroinitializer
  %126 = bitcast <8 x i1> %125 to i8
  %cmp.i3878.not4542 = icmp eq i8 %126, 0
  br i1 %cmp.i3878.not4542, label %for_step1324, label %for_loop1378

for_loop1378:                                     ; preds = %not_all_continued_or_breaked1363, %for_step1379
  %127 = phi i8 [ %131, %for_step1379 ], [ %126, %not_all_continued_or_breaked1363 ]
  %"oldMask&test13874546" = phi <8 x i32> [ %"oldMask&test1387", %for_step1379 ], [ %new_mask1374, %not_all_continued_or_breaked1363 ]
  %dy1384.04545 = phi <8 x i32> [ %dy_load1617_plus1, %for_step1379 ], [ splat (i32 -1), %not_all_continued_or_breaked1363 ]
  %bestIdx1321.24544 = phi <8 x i32> [ %bestIdx1321.3, %for_step1379 ], [ %bestIdx1321.04550, %not_all_continued_or_breaked1363 ]
  %bestDistSq1320.24543 = phi <8 x float> [ %bestDistSq1320.3, %for_step1379 ], [ %bestDistSq1320.04549, %not_all_continued_or_breaked1363 ]
  %add_cell12801395_y_dy_load1397 = add nsw <8 x i32> %dy1384.04545, %blend.i4285.v
  %greaterequal_ny_load1398_GridDimensions11841399_y_broadcast1401.not = icmp ult <8 x i32> %add_cell12801395_y_dy_load1397, %get_element1311_broadcast1312
  %"oldMask&test1403" = select <8 x i1> %greaterequal_ny_load1398_GridDimensions11841399_y_broadcast1401.not, <8 x i32> zeroinitializer, <8 x i32> %"oldMask&test13874546"
  %128 = icmp slt <8 x i32> %"oldMask&test1403", zeroinitializer
  %129 = bitcast <8 x i1> %128 to i8
  %"equal_finished&func1416_internal_mask&function_mask1393" = icmp eq i8 %127, %129
  br i1 %"equal_finished&func1416_internal_mask&function_mask1393", label %for_step1379, label %not_all_continued_or_breaked1418

for_step1379:                                     ; preds = %not_all_continued_or_breaked1484, %for_step1500, %not_all_continued_or_breaked1418, %for_loop1378
  %bestDistSq1320.3 = phi <8 x float> [ %bestDistSq1320.24543, %for_loop1378 ], [ %bestDistSq1320.24543, %not_all_continued_or_breaked1418 ], [ %bestDistSq1320.24543, %not_all_continued_or_breaked1484 ], [ %bestDistSq1320.5, %for_step1500 ]
  %bestIdx1321.3 = phi <8 x i32> [ %bestIdx1321.24544, %for_loop1378 ], [ %bestIdx1321.24544, %not_all_continued_or_breaked1418 ], [ %bestIdx1321.24544, %not_all_continued_or_breaked1484 ], [ %bestIdx1321.5, %for_step1500 ]
  %continue_lanes_memory1382.1 = phi <8 x i32> [ %"oldMask&test1403", %for_loop1378 ], [ %"mask|continueMask1473", %not_all_continued_or_breaked1418 ], [ %"mask|continueMask1473", %not_all_continued_or_breaked1484 ], [ %"mask|continueMask1473", %for_step1500 ]
  %internal_mask_memory.21 = phi <8 x i32> [ zeroinitializer, %for_loop1378 ], [ zeroinitializer, %not_all_continued_or_breaked1418 ], [ %new_mask1495, %not_all_continued_or_breaked1484 ], [ %new_mask1495, %for_step1500 ]
  %"mask|continue_mask1614" = or <8 x i32> %internal_mask_memory.21, %continue_lanes_memory1382.1
  %dy_load1617_plus1 = add nsw <8 x i32> %dy1384.04545, splat (i32 1)
  %lessequal_dy_load1385_.inv = icmp sgt <8 x i32> %dy1384.04545, zeroinitializer
  %"oldMask&test1387" = select <8 x i1> %lessequal_dy_load1385_.inv, <8 x i32> zeroinitializer, <8 x i32> %"mask|continue_mask1614"
  %130 = icmp slt <8 x i32> %"oldMask&test1387", zeroinitializer
  %131 = bitcast <8 x i1> %130 to i8
  %cmp.i3878.not = icmp eq i8 %131, 0
  br i1 %cmp.i3878.not, label %for_step1324, label %for_loop1378, !llvm.loop !48

not_all_continued_or_breaked1418:                 ; preds = %for_loop1378
  %new_mask1429 = xor <8 x i32> %"oldMask&test1403", %"oldMask&test13874546"
  %mul_ny_load1433_GridDimensions11841434_x_broadcast1436 = mul nsw <8 x i32> %add_cell12801395_y_dy_load1397, %get_element1308_broadcast1309
  %add_mul_ny_load1433_GridDimensions11841434_x_broadcast1436_nx_load1437 = add nsw <8 x i32> %mul_ny_load1433_GridDimensions11841434_x_broadcast1436, %add_cell12801340_x_dx_load1342
  %CellStartEnd_load14401441__data = load ptr, ptr %CellStartEnd, align 8
  %mul__cellHash_load1439 = shl nsw <8 x i32> %add_mul_ny_load1433_GridDimensions11841434_x_broadcast1436_nx_load1437, splat (i32 3)
  %v_1.i4286 = tail call <8 x i32> @llvm.x86.avx2.gather.d.d.256(<8 x i32> undef, ptr readonly %CellStartEnd_load14401441__data, <8 x i32> %mul__cellHash_load1439, <8 x i32> %new_mask1429, i8 1)
  %132 = or disjoint <8 x i32> %mul__cellHash_load1439, splat (i32 4)
  %v_1.i4288 = tail call <8 x i32> @llvm.x86.avx2.gather.d.d.256(<8 x i32> undef, ptr readonly %CellStartEnd_load14401441__data, <8 x i32> %132, <8 x i32> %new_mask1429, i8 1)
  %isneg3391 = icmp slt <8 x i32> %v_1.i4286, zeroinitializer
  %"oldMask&test1468" = select <8 x i1> %isneg3391, <8 x i32> %new_mask1429, <8 x i32> zeroinitializer
  %"mask|continueMask1473" = or <8 x i32> %"oldMask&test1468", %"oldMask&test1403"
  %133 = icmp slt <8 x i32> %"mask|continueMask1473", zeroinitializer
  %134 = bitcast <8 x i1> %133 to i8
  %"equal_finished&func1481_internal_mask&function_mask1393" = icmp eq i8 %127, %134
  br i1 %"equal_finished&func1481_internal_mask&function_mask1393", label %for_step1379, label %not_all_continued_or_breaked1484

not_all_continued_or_breaked1484:                 ; preds = %not_all_continued_or_breaked1418
  %new_mask1495 = xor <8 x i32> %"mask|continueMask1473", %"oldMask&test13874546"
  %less_i_load1507_end_load15084534 = icmp slt <8 x i32> %v_1.i4286, %v_1.i4288
  %"oldMask&test15104535" = select <8 x i1> %less_i_load1507_end_load15084534, <8 x i32> %new_mask1495, <8 x i32> zeroinitializer
  %135 = icmp slt <8 x i32> %"oldMask&test15104535", zeroinitializer
  %136 = bitcast <8 x i1> %135 to i8
  %cmp.i3882.not4536 = icmp eq i8 %136, 0
  br i1 %cmp.i3882.not4536, label %for_step1379, label %for_loop1499

for_loop1499:                                     ; preds = %not_all_continued_or_breaked1484, %for_step1500
  %137 = phi i8 [ %142, %for_step1500 ], [ %136, %not_all_continued_or_breaked1484 ]
  %"oldMask&test15104540" = phi <8 x i32> [ %"oldMask&test1510", %for_step1500 ], [ %"oldMask&test15104535", %not_all_continued_or_breaked1484 ]
  %i1505.04539 = phi <8 x i32> [ %i_load1606_plus1, %for_step1500 ], [ %v_1.i4286, %not_all_continued_or_breaked1484 ]
  %bestIdx1321.44538 = phi <8 x i32> [ %bestIdx1321.5, %for_step1500 ], [ %bestIdx1321.24544, %not_all_continued_or_breaked1484 ]
  %bestDistSq1320.44537 = phi <8 x float> [ %bestDistSq1320.5, %for_step1500 ], [ %bestDistSq1320.24543, %not_all_continued_or_breaked1484 ]
  %mul__i_load1518 = shl nsw <8 x i32> %i1505.04539, splat (i32 3)
  %mask.i4290 = bitcast <8 x i32> %"oldMask&test15104540" to <8 x float>
  %v_1.i4291 = tail call <8 x float> @llvm.x86.avx2.gather.d.ps.256(<8 x float> undef, ptr readonly %SortedPositions_ptr, <8 x i32> %mul__i_load1518, <8 x float> %mask.i4290, i8 1)
  %138 = or disjoint <8 x i32> %mul__i_load1518, splat (i32 4)
  %v_1.i4294 = tail call <8 x float> @llvm.x86.avx2.gather.d.ps.256(<8 x float> undef, ptr readonly %SortedPositions_ptr, <8 x i32> %138, <8 x float> %mask.i4290, i8 1)
  %sub_a14_x_b26_x.i.i4120 = fsub <8 x float> %v_1.i4291, %v_1.i4266
  %sub_a19_y_b211_y.i.i4121 = fsub <8 x float> %v_1.i4294, %v_1.i4268
  %mul_a13_x_b25_x.i.i.i4129 = fmul <8 x float> %sub_a14_x_b26_x.i.i4120, %sub_a14_x_b26_x.i.i4120
  %mul_a17_y_b29_y.i.i.i4130 = fmul <8 x float> %sub_a19_y_b211_y.i.i4121, %sub_a19_y_b211_y.i.i4121
  %add_mul_a13_x_b25_x_mul_a17_y_b29_y.i.i.i4131 = fadd <8 x float> %mul_a13_x_b25_x.i.i.i4129, %mul_a17_y_b29_y.i.i.i4130
  %less_distSq_load1544_SquaredEpsilonSelf_load1545_broadcast1546 = fcmp olt <8 x float> %add_mul_a13_x_b25_x_mul_a17_y_b29_y.i.i.i4131, %SquaredEpsilonSelf_load1545_broadcast1546
  %"oldMask&test1549" = select <8 x i1> %less_distSq_load1544_SquaredEpsilonSelf_load1545_broadcast1546, <8 x i32> %"oldMask&test15104540", <8 x i32> zeroinitializer
  %139 = icmp slt <8 x i32> %"oldMask&test1549", zeroinitializer
  %140 = bitcast <8 x i1> %139 to i8
  %"equal_finished&func1562_internal_mask&function_mask1516" = icmp eq i8 %137, %140
  br i1 %"equal_finished&func1562_internal_mask&function_mask1516", label %for_step1500, label %not_all_continued_or_breaked1564

for_step1500:                                     ; preds = %for_loop1499, %not_all_continued_or_breaked1564
  %bestDistSq1320.5 = phi <8 x float> [ %bestDistSq1320.44537, %for_loop1499 ], [ %blend.i.i4298, %not_all_continued_or_breaked1564 ]
  %bestIdx1321.5 = phi <8 x i32> [ %bestIdx1321.44538, %for_loop1499 ], [ %145, %not_all_continued_or_breaked1564 ]
  %internal_mask_memory.23 = phi <8 x i32> [ zeroinitializer, %for_loop1499 ], [ %new_mask1575, %not_all_continued_or_breaked1564 ]
  %"mask|continue_mask1603" = or <8 x i32> %internal_mask_memory.23, %"oldMask&test1549"
  %i_load1606_plus1 = add nsw <8 x i32> %i1505.04539, splat (i32 1)
  %less_i_load1507_end_load1508 = icmp slt <8 x i32> %i_load1606_plus1, %v_1.i4288
  %"oldMask&test1510" = select <8 x i1> %less_i_load1507_end_load1508, <8 x i32> %"mask|continue_mask1603", <8 x i32> zeroinitializer
  %141 = icmp slt <8 x i32> %"oldMask&test1510", zeroinitializer
  %142 = bitcast <8 x i1> %141 to i8
  %cmp.i3882.not = icmp eq i8 %142, 0
  br i1 %cmp.i3882.not, label %for_step1379, label %for_loop1499, !llvm.loop !49

not_all_continued_or_breaked1564:                 ; preds = %for_loop1499
  %new_mask1575 = xor <8 x i32> %"oldMask&test1549", %"oldMask&test15104540"
  %less_distSq_load1578_bestDistSq_load1579 = fcmp olt <8 x float> %add_mul_a13_x_b25_x_mul_a17_y_b29_y.i.i.i4131, %bestDistSq1320.44537
  %143 = bitcast <8 x i32> %new_mask1575 to <8 x float>
  %mask_as_float.i.i4296 = select <8 x i1> %less_distSq_load1578_bestDistSq_load1579, <8 x float> %143, <8 x float> zeroinitializer
  %blend.i.i4298 = call <8 x float> @llvm.x86.avx.blendv.ps.256(<8 x float> %bestDistSq1320.44537, <8 x float> %add_mul_a13_x_b25_x_mul_a17_y_b29_y.i.i.i4131, <8 x float> %mask_as_float.i.i4296)
  %144 = bitcast <8 x i32> %bestIdx1321.44538 to <8 x float>
  %newAsFloat.i4301 = bitcast <8 x i32> %i1505.04539 to <8 x float>
  %blend.i4302 = call <8 x float> @llvm.x86.avx.blendv.ps.256(<8 x float> %144, <8 x float> %newAsFloat.i4301, <8 x float> %mask_as_float.i.i4296)
  %145 = bitcast <8 x float> %blend.i4302 to <8 x i32>
  br label %for_step1500

if_done1636:                                      ; preds = %for_exit1670, %safe_if_run_true1785, %safe_if_after_true1637
  %indvars.iv.next4674 = add nsw i64 %indvars.iv4673, 8
  %before_aligned_end1246 = icmp slt i64 %indvars.iv.next4674, %3
  br i1 %before_aligned_end1246, label %foreach_full_body1200, label %outer_not_in_extras1226.partial_inner_all_outer1244_crit_edge, !llvm.loop !34

safe_if_after_true1637:                           ; preds = %safe_if_run_true1638, %for_exit1325
  %"~test1661" = xor <8 x i32> %notequal_bestIdx_load1634__to_boolvec, splat (i32 -1)
  %146 = xor <8 x i1> %notequal_bestIdx_load1634_, splat (i1 true)
  %147 = bitcast <8 x i1> %146 to i8
  %cmp.i3885.not = icmp eq i8 %147, 0
  br i1 %cmp.i3885.not, label %if_done1636, label %for_test1667.preheader

for_test1667.preheader:                           ; preds = %safe_if_after_true1637
  %"oldMask&test16794554" = select <8 x i1> %less_i_load1675_SortedLength_load1676_broadcast16774553, <8 x i32> %"~test1661", <8 x i32> zeroinitializer
  %148 = icmp slt <8 x i32> %"oldMask&test16794554", zeroinitializer
  %149 = bitcast <8 x i1> %148 to i8
  %cmp.i3886.not4555 = icmp eq i8 %149, 0
  br i1 %cmp.i3886.not4555, label %for_exit1670, label %for_loop1668

safe_if_run_true1638:                             ; preds = %for_exit1325
  %mul__bestIdx_load1647 = shl nsw <8 x i32> %bestIdx1321.1, splat (i32 3)
  %150 = or disjoint <8 x i32> %mul__bestIdx_load1647, splat (i32 4)
  %new_add3608 = sext <8 x i32> %150 to <8 x i64>
  %vecmask_1.i4303 = shufflevector <8 x i32> %notequal_bestIdx_load1634__to_boolvec, <8 x i32> poison, <4 x i32> <i32 0, i32 1, i32 2, i32 3>
  %vecmask_2.i4304 = shufflevector <8 x i32> %notequal_bestIdx_load1634__to_boolvec, <8 x i32> poison, <4 x i32> <i32 4, i32 5, i32 6, i32 7>
  %offsets_1.i4305 = shufflevector <8 x i64> %new_add3608, <8 x i64> poison, <4 x i32> <i32 0, i32 1, i32 2, i32 3>
  %offsets_2.i4306 = shufflevector <8 x i64> %new_add3608, <8 x i64> poison, <4 x i32> <i32 4, i32 5, i32 6, i32 7>
  %v1_1.i4307 = tail call <4 x i32> @llvm.x86.avx2.gather.q.d.256(<4 x i32> undef, ptr %HashIndex_ptr, <4 x i64> %offsets_1.i4305, <4 x i32> %vecmask_1.i4303, i8 1)
  %v2_1.i4308 = tail call <4 x i32> @llvm.x86.avx2.gather.q.d.256(<4 x i32> undef, ptr %HashIndex_ptr, <4 x i64> %offsets_2.i4306, <4 x i32> %vecmask_2.i4304, i8 1)
  %v.i4309 = shufflevector <4 x i32> %v1_1.i4307, <4 x i32> %v2_1.i4308, <8 x i32> <i32 0, i32 1, i32 2, i32 3, i32 4, i32 5, i32 6, i32 7>
  %val.i4310 = bitcast <8 x i32> %v.i4309 to <8 x float>
  call void @llvm.masked.store.v8f32.p0(<8 x float> %val.i4310, ptr %ptr3560, i32 1, <8 x i1> %notequal_bestIdx_load1634_)
  br label %safe_if_after_true1637

for_loop1668:                                     ; preds = %for_test1667.preheader, %for_step1669
  %indvars.iv = phi i64 [ %indvars.iv.next, %for_step1669 ], [ 0, %for_test1667.preheader ]
  %151 = phi i8 [ %156, %for_step1669 ], [ %149, %for_test1667.preheader ]
  %"oldMask&test16794561" = phi <8 x i32> [ %"oldMask&test1679", %for_step1669 ], [ %"oldMask&test16794554", %for_test1667.preheader ]
  %i1674.04560 = phi <8 x i32> [ %i_load1775_plus1, %for_step1669 ], [ zeroinitializer, %for_test1667.preheader ]
  %bestIdx1321.64557 = phi <8 x i32> [ %bestIdx1321.7, %for_step1669 ], [ %bestIdx1321.1, %for_test1667.preheader ]
  %bestDistSq1320.64556 = phi <8 x float> [ %bestDistSq1320.7, %for_step1669 ], [ %bestDistSq1320.1, %for_test1667.preheader ]
  %152 = shl nsw i64 %indvars.iv, 3
  %ptr3621 = getelementptr i8, ptr %SortedPositions_ptr, i64 %152, !filename !11, !first_line !41, !first_column !20, !last_line !41, !last_column !21
  %SortedPositions_ptr_load1688_offset_load36203622 = load float, ptr %ptr3621, align 4
  %SortedPositions_ptr_load1688_offset_load36203623 = insertelement <8 x float> poison, float %SortedPositions_ptr_load1688_offset_load36203622, i64 0
  %SortedPositions_ptr_load1688_offset_load36203624 = shufflevector <8 x float> %SortedPositions_ptr_load1688_offset_load36203623, <8 x float> poison, <8 x i32> zeroinitializer, !filename !11, !first_line !41, !first_column !20, !last_line !41, !last_column !21
  %gep = getelementptr i8, ptr %invariant.gep, i64 %152
  %SortedPositions_ptr_load1688_offset_load170336303635 = load float, ptr %gep, align 4
  %SortedPositions_ptr_load1688_offset_load170336303636 = insertelement <8 x float> poison, float %SortedPositions_ptr_load1688_offset_load170336303635, i64 0
  %SortedPositions_ptr_load1688_offset_load170336303637 = shufflevector <8 x float> %SortedPositions_ptr_load1688_offset_load170336303636, <8 x float> poison, <8 x i32> zeroinitializer, !filename !11, !first_line !41, !first_column !20, !last_line !41, !last_column !21
  %sub_a14_x_b26_x.i.i4132 = fsub <8 x float> %SortedPositions_ptr_load1688_offset_load36203624, %v_1.i4266
  %sub_a19_y_b211_y.i.i4133 = fsub <8 x float> %SortedPositions_ptr_load1688_offset_load170336303637, %v_1.i4268
  %mul_a13_x_b25_x.i.i.i4141 = fmul <8 x float> %sub_a14_x_b26_x.i.i4132, %sub_a14_x_b26_x.i.i4132
  %mul_a17_y_b29_y.i.i.i4142 = fmul <8 x float> %sub_a19_y_b211_y.i.i4133, %sub_a19_y_b211_y.i.i4133
  %add_mul_a13_x_b25_x_mul_a17_y_b29_y.i.i.i4143 = fadd <8 x float> %mul_a13_x_b25_x.i.i.i4141, %mul_a17_y_b29_y.i.i.i4142
  %less_distSq_load1713_SquaredEpsilonSelf_load1714_broadcast1715 = fcmp olt <8 x float> %add_mul_a13_x_b25_x_mul_a17_y_b29_y.i.i.i4143, %SquaredEpsilonSelf_load1545_broadcast1546
  %"oldMask&test1718" = select <8 x i1> %less_distSq_load1713_SquaredEpsilonSelf_load1714_broadcast1715, <8 x i32> %"oldMask&test16794561", <8 x i32> zeroinitializer
  %153 = icmp slt <8 x i32> %"oldMask&test1718", zeroinitializer
  %154 = bitcast <8 x i1> %153 to i8
  %"equal_finished&func1731_internal_mask&function_mask1685" = icmp eq i8 %151, %154
  br i1 %"equal_finished&func1731_internal_mask&function_mask1685", label %for_step1669, label %not_all_continued_or_breaked1733

for_step1669:                                     ; preds = %for_loop1668, %not_all_continued_or_breaked1733
  %bestDistSq1320.7 = phi <8 x float> [ %bestDistSq1320.64556, %for_loop1668 ], [ %blend.i.i4313, %not_all_continued_or_breaked1733 ]
  %bestIdx1321.7 = phi <8 x i32> [ %bestIdx1321.64557, %for_loop1668 ], [ %161, %not_all_continued_or_breaked1733 ]
  %internal_mask_memory.25 = phi <8 x i32> [ zeroinitializer, %for_loop1668 ], [ %new_mask1744, %not_all_continued_or_breaked1733 ]
  %"mask|continue_mask1772" = or <8 x i32> %internal_mask_memory.25, %"oldMask&test1718"
  %indvars.iv.next = add nuw nsw i64 %indvars.iv, 1
  %i_load1775_plus1 = add nuw nsw <8 x i32> %i1674.04560, splat (i32 1)
  %less_i_load1675_SortedLength_load1676_broadcast1677 = icmp slt <8 x i32> %i_load1775_plus1, %SortedLength_load1676_broadcast1677
  %"oldMask&test1679" = select <8 x i1> %less_i_load1675_SortedLength_load1676_broadcast1677, <8 x i32> %"mask|continue_mask1772", <8 x i32> zeroinitializer
  %155 = icmp slt <8 x i32> %"oldMask&test1679", zeroinitializer
  %156 = bitcast <8 x i1> %155 to i8
  %cmp.i3886.not = icmp eq i8 %156, 0
  br i1 %cmp.i3886.not, label %for_exit1670, label %for_loop1668, !llvm.loop !50

for_exit1670:                                     ; preds = %for_step1669, %for_test1667.preheader
  %bestIdx1321.6.lcssa = phi <8 x i32> [ %bestIdx1321.1, %for_test1667.preheader ], [ %bestIdx1321.7, %for_step1669 ]
  %notequal_bestIdx_load1781_ = icmp eq <8 x i32> %bestIdx1321.6.lcssa, splat (i32 -1)
  %"oldMask&test1786" = select <8 x i1> %notequal_bestIdx_load1781_, <8 x i32> zeroinitializer, <8 x i32> %"~test1661"
  %157 = icmp slt <8 x i32> %"oldMask&test1786", zeroinitializer
  %158 = bitcast <8 x i1> %157 to i8
  %cmp.i3889.not = icmp eq i8 %158, 0
  br i1 %cmp.i3889.not, label %if_done1636, label %safe_if_run_true1785

not_all_continued_or_breaked1733:                 ; preds = %for_loop1668
  %new_mask1744 = xor <8 x i32> %"oldMask&test1718", %"oldMask&test16794561"
  %less_distSq_load1747_bestDistSq_load1748 = fcmp olt <8 x float> %add_mul_a13_x_b25_x_mul_a17_y_b29_y.i.i.i4143, %bestDistSq1320.64556
  %159 = bitcast <8 x i32> %new_mask1744 to <8 x float>
  %mask_as_float.i.i4311 = select <8 x i1> %less_distSq_load1747_bestDistSq_load1748, <8 x float> %159, <8 x float> zeroinitializer
  %blend.i.i4313 = call <8 x float> @llvm.x86.avx.blendv.ps.256(<8 x float> %bestDistSq1320.64556, <8 x float> %add_mul_a13_x_b25_x_mul_a17_y_b29_y.i.i.i4143, <8 x float> %mask_as_float.i.i4311)
  %160 = bitcast <8 x i32> %bestIdx1321.64557 to <8 x float>
  %newAsFloat.i4316 = bitcast <8 x i32> %i1674.04560 to <8 x float>
  %blend.i4317 = call <8 x float> @llvm.x86.avx.blendv.ps.256(<8 x float> %160, <8 x float> %newAsFloat.i4316, <8 x float> %mask_as_float.i.i4311)
  %161 = bitcast <8 x float> %blend.i4317 to <8 x i32>
  br label %for_step1669

safe_if_run_true1785:                             ; preds = %for_exit1670
  %mul__bestIdx_load1794 = shl nsw <8 x i32> %bestIdx1321.6.lcssa, splat (i32 3)
  %162 = or disjoint <8 x i32> %mul__bestIdx_load1794, splat (i32 4)
  %new_add3642 = sext <8 x i32> %162 to <8 x i64>
  %vecmask_1.i4318 = shufflevector <8 x i32> %"oldMask&test1786", <8 x i32> poison, <4 x i32> <i32 0, i32 1, i32 2, i32 3>
  %vecmask_2.i4319 = shufflevector <8 x i32> %"oldMask&test1786", <8 x i32> poison, <4 x i32> <i32 4, i32 5, i32 6, i32 7>
  %offsets_1.i4320 = shufflevector <8 x i64> %new_add3642, <8 x i64> poison, <4 x i32> <i32 0, i32 1, i32 2, i32 3>
  %offsets_2.i4321 = shufflevector <8 x i64> %new_add3642, <8 x i64> poison, <4 x i32> <i32 4, i32 5, i32 6, i32 7>
  %v1_1.i4322 = tail call <4 x i32> @llvm.x86.avx2.gather.q.d.256(<4 x i32> undef, ptr %HashIndex_ptr, <4 x i64> %offsets_1.i4320, <4 x i32> %vecmask_1.i4318, i8 1)
  %v2_1.i4323 = tail call <4 x i32> @llvm.x86.avx2.gather.q.d.256(<4 x i32> undef, ptr %HashIndex_ptr, <4 x i64> %offsets_2.i4321, <4 x i32> %vecmask_2.i4319, i8 1)
  %v.i4324 = shufflevector <4 x i32> %v1_1.i4322, <4 x i32> %v2_1.i4323, <8 x i32> <i32 0, i32 1, i32 2, i32 3, i32 4, i32 5, i32 6, i32 7>
  %val.i4325 = bitcast <8 x i32> %v.i4324 to <8 x float>
  call void @llvm.x86.avx.maskstore.ps.256(ptr nonnull %ptr3560, <8 x i32> %"oldMask&test1786", <8 x float> %val.i4325)
  br label %if_done1636

partial_inner_only1843:                           ; preds = %partial_inner_all_outer1244
  %smear_counter_init1847 = insertelement <8 x i32> poison, i32 %counter1220.1.lcssa, i64 0
  %smear_counter1848 = shufflevector <8 x i32> %smear_counter_init1847, <8 x i32> poison, <8 x i32> zeroinitializer
  %iter_val1849 = add nsw <8 x i32> %smear_counter1848, <i32 0, i32 1, i32 2, i32 3, i32 4, i32 5, i32 6, i32 7>
  %smear_end_init1850 = insertelement <8 x i32> poison, i32 %ret.i.i, i64 0
  %smear_end1851 = shufflevector <8 x i32> %smear_end_init1850, <8 x i32> poison, <8 x i32> zeroinitializer
  %cmp1852 = icmp slt <8 x i32> %iter_val1849, %smear_end1851
  %cmp1852_to_boolvec = sext <8 x i1> %cmp1852 to <8 x i32>
  %mul__index_load1859.elt0 = shl nsw i32 %counter1220.1.lcssa, 2
  %163 = sext i32 %mul__index_load1859.elt0 to i64
  %ptr3576 = getelementptr i8, ptr %Results_ptr, i64 %163
  call void @llvm.masked.store.v8f32.p0(<8 x float> splat (float 0xFFFFFFFFE0000000), ptr %ptr3576, i32 1, <8 x i1> %cmp1852)
  %mul__index_load1867 = shl nsw <8 x i32> %iter_val1849, splat (i32 3)
  %mask.i4326 = bitcast <8 x i32> %cmp1852_to_boolvec to <8 x float>
  %v_1.i4327 = tail call <8 x float> @llvm.x86.avx2.gather.d.ps.256(<8 x float> undef, ptr readonly %QueryPositions_ptr, <8 x i32> %mul__index_load1867, <8 x float> %mask.i4326, i8 1)
  %164 = or disjoint <8 x i32> %mul__index_load1867, splat (i32 4)
  %v_1.i4330 = tail call <8 x float> @llvm.x86.avx2.gather.d.ps.256(<8 x float> undef, ptr readonly %QueryPositions_ptr, <8 x i32> %164, <8 x float> %mask.i4326, i8 1)
  %get_element1888_broadcast = insertelement <8 x float> poison, float %GridOrigin_ptr_load_load.unpack, i64 0
  %get_element1888_broadcast1889 = shufflevector <8 x float> %get_element1888_broadcast, <8 x float> poison, <8 x i32> zeroinitializer
  %get_element1891_broadcast = insertelement <8 x float> poison, float %GridOrigin_ptr_load_load.unpack3386, i64 0
  %get_element1891_broadcast1892 = shufflevector <8 x float> %get_element1891_broadcast, <8 x float> poison, <8 x i32> zeroinitializer
  %sub_a14_x_b26_x.i4081 = fsub <8 x float> %v_1.i4327, %get_element1888_broadcast1889
  %sub_a19_y_b211_y.i4082 = fsub <8 x float> %v_1.i4330, %get_element1891_broadcast1892
  %GridResolutionInv_load1897_broadcast = insertelement <8 x float> poison, float %GridResolutionInv_ptr_load_load, i64 0
  %GridResolutionInv_load1897_broadcast1898 = shufflevector <8 x float> %GridResolutionInv_load1897_broadcast, <8 x float> poison, <8 x i32> zeroinitializer
  %mul_v14_x_s_load.i4088 = fmul <8 x float> %GridResolutionInv_load1897_broadcast1898, %sub_a14_x_b26_x.i4081
  %mul_v17_y_s_load9.i4089 = fmul <8 x float> %GridResolutionInv_load1897_broadcast1898, %sub_a19_y_b211_y.i4082
  %call.i.i.i4095 = tail call <8 x float> @llvm.x86.avx.round.ps.256(<8 x float> %mul_v14_x_s_load.i4088, i32 9)
  %call.i.i3.i4096 = tail call <8 x float> @llvm.x86.avx.round.ps.256(<8 x float> %mul_v17_y_s_load9.i4089, i32 9)
  %v12_x_to_int32.i4102 = fptosi <8 x float> %call.i.i.i4095 to <8 x i32>
  %v14_y_to_int32.i4103 = fptosi <8 x float> %call.i.i3.i4096 to <8 x i32>
  %get_element1913_broadcast = insertelement <8 x i32> poison, i32 %GridDimensions_ptr_load_load.unpack, i64 0
  %get_element1913_broadcast1914 = shufflevector <8 x i32> %get_element1913_broadcast, <8 x i32> poison, <8 x i32> zeroinitializer
  %get_element1916_broadcast = insertelement <8 x i32> poison, i32 %GridDimensions_ptr_load_load.unpack3389, i64 0
  %get_element1916_broadcast1917 = shufflevector <8 x i32> %get_element1916_broadcast, <8 x i32> poison, <8 x i32> zeroinitializer
  %sub_a14_x_b_load.i4113 = add nsw <8 x i32> %get_element1913_broadcast1914, splat (i32 -1)
  %sub_a17_y_b_load9.i4114 = add nsw <8 x i32> %get_element1916_broadcast1917, splat (i32 -1)
  %165 = call <8 x i32> @llvm.smax.v8i32(<8 x i32> %v12_x_to_int32.i4102, <8 x i32> zeroinitializer)
  %166 = call <8 x i32> @llvm.smax.v8i32(<8 x i32> %v14_y_to_int32.i4103, <8 x i32> zeroinitializer)
  %blend.i16.i4441.v = call <8 x i32> @llvm.smin.v8i32(<8 x i32> %165, <8 x i32> %sub_a14_x_b_load.i4113)
  %blend.i20.i4446.v = call <8 x i32> @llvm.smin.v8i32(<8 x i32> %166, <8 x i32> %sub_a17_y_b_load9.i4114)
  %167 = bitcast <8 x i1> %cmp1852 to i8
  %cmp.i3874.not4582 = icmp eq i8 %167, 0
  br i1 %cmp.i3874.not4582, label %for_exit1930, label %for_loop1928.lr.ph

for_loop1928.lr.ph:                               ; preds = %partial_inner_only1843
  %SquaredEpsilonSelf_load2150_broadcast = insertelement <8 x float> poison, float %SquaredEpsilonSelf_ptr_load_load, i64 0
  %SquaredEpsilonSelf_load2150_broadcast2151 = shufflevector <8 x float> %SquaredEpsilonSelf_load2150_broadcast, <8 x float> poison, <8 x i32> zeroinitializer
  br label %for_loop1928

not_all_continued_or_breaked1968:                 ; preds = %for_loop1928
  %new_mask1979 = xor <8 x i32> %"oldMask&test1953", %"oldMask&test19374586"
  %168 = icmp slt <8 x i32> %new_mask1979, zeroinitializer
  %169 = bitcast <8 x i1> %168 to i8
  %cmp.i3891.not4575 = icmp eq i8 %169, 0
  br i1 %cmp.i3891.not4575, label %for_step1929, label %for_loop1983

for_loop1983:                                     ; preds = %not_all_continued_or_breaked1968, %for_step1984
  %170 = phi i8 [ %174, %for_step1984 ], [ %169, %not_all_continued_or_breaked1968 ]
  %"oldMask&test19924579" = phi <8 x i32> [ %"oldMask&test1992", %for_step1984 ], [ %new_mask1979, %not_all_continued_or_breaked1968 ]
  %dy1989.04578 = phi <8 x i32> [ %dy_load2222_plus1, %for_step1984 ], [ splat (i32 -1), %not_all_continued_or_breaked1968 ]
  %bestIdx1926.24577 = phi <8 x i32> [ %bestIdx1926.3, %for_step1984 ], [ %bestIdx1926.04584, %not_all_continued_or_breaked1968 ]
  %bestDistSq1925.24576 = phi <8 x float> [ %bestDistSq1925.3, %for_step1984 ], [ %bestDistSq1925.04583, %not_all_continued_or_breaked1968 ]
  %add_cell18852000_y_dy_load2002 = add nsw <8 x i32> %dy1989.04578, %blend.i20.i4446.v
  %greaterequal_ny_load2003_GridDimensions11842004_y_broadcast2006.not = icmp ult <8 x i32> %add_cell18852000_y_dy_load2002, %get_element1916_broadcast1917
  %"oldMask&test2008" = select <8 x i1> %greaterequal_ny_load2003_GridDimensions11842004_y_broadcast2006.not, <8 x i32> zeroinitializer, <8 x i32> %"oldMask&test19924579"
  %171 = icmp slt <8 x i32> %"oldMask&test2008", zeroinitializer
  %172 = bitcast <8 x i1> %171 to i8
  %"equal_finished&func2021_internal_mask&function_mask1998" = icmp eq i8 %170, %172
  br i1 %"equal_finished&func2021_internal_mask&function_mask1998", label %for_step1984, label %not_all_continued_or_breaked2023

for_step1984:                                     ; preds = %not_all_continued_or_breaked2089, %for_step2105, %not_all_continued_or_breaked2023, %for_loop1983
  %bestDistSq1925.3 = phi <8 x float> [ %bestDistSq1925.24576, %for_loop1983 ], [ %bestDistSq1925.24576, %not_all_continued_or_breaked2023 ], [ %bestDistSq1925.24576, %not_all_continued_or_breaked2089 ], [ %bestDistSq1925.5, %for_step2105 ]
  %bestIdx1926.3 = phi <8 x i32> [ %bestIdx1926.24577, %for_loop1983 ], [ %bestIdx1926.24577, %not_all_continued_or_breaked2023 ], [ %bestIdx1926.24577, %not_all_continued_or_breaked2089 ], [ %bestIdx1926.5, %for_step2105 ]
  %continue_lanes_memory1987.1 = phi <8 x i32> [ %"oldMask&test2008", %for_loop1983 ], [ %"mask|continueMask2078", %not_all_continued_or_breaked2023 ], [ %"mask|continueMask2078", %not_all_continued_or_breaked2089 ], [ %"mask|continueMask2078", %for_step2105 ]
  %internal_mask_memory.29 = phi <8 x i32> [ zeroinitializer, %for_loop1983 ], [ zeroinitializer, %not_all_continued_or_breaked2023 ], [ %new_mask2100, %not_all_continued_or_breaked2089 ], [ %new_mask2100, %for_step2105 ]
  %"mask|continue_mask2219" = or <8 x i32> %internal_mask_memory.29, %continue_lanes_memory1987.1
  %dy_load2222_plus1 = add nsw <8 x i32> %dy1989.04578, splat (i32 1)
  %lessequal_dy_load1990_.inv = icmp sgt <8 x i32> %dy1989.04578, zeroinitializer
  %"oldMask&test1992" = select <8 x i1> %lessequal_dy_load1990_.inv, <8 x i32> zeroinitializer, <8 x i32> %"mask|continue_mask2219"
  %173 = icmp slt <8 x i32> %"oldMask&test1992", zeroinitializer
  %174 = bitcast <8 x i1> %173 to i8
  %cmp.i3891.not = icmp eq i8 %174, 0
  br i1 %cmp.i3891.not, label %for_step1929, label %for_loop1983, !llvm.loop !51

not_all_continued_or_breaked2023:                 ; preds = %for_loop1983
  %new_mask2034 = xor <8 x i32> %"oldMask&test2008", %"oldMask&test19924579"
  %mul_ny_load2038_GridDimensions11842039_x_broadcast2041 = mul nsw <8 x i32> %add_cell18852000_y_dy_load2002, %get_element1913_broadcast1914
  %add_mul_ny_load2038_GridDimensions11842039_x_broadcast2041_nx_load2042 = add nsw <8 x i32> %mul_ny_load2038_GridDimensions11842039_x_broadcast2041, %add_cell18851945_x_dx_load1947
  %CellStartEnd_load20452046__data = load ptr, ptr %CellStartEnd, align 8
  %mul__cellHash_load2044 = shl nsw <8 x i32> %add_mul_ny_load2038_GridDimensions11842039_x_broadcast2041_nx_load2042, splat (i32 3)
  %v_1.i4332 = tail call <8 x i32> @llvm.x86.avx2.gather.d.d.256(<8 x i32> undef, ptr readonly %CellStartEnd_load20452046__data, <8 x i32> %mul__cellHash_load2044, <8 x i32> %new_mask2034, i8 1)
  %175 = or disjoint <8 x i32> %mul__cellHash_load2044, splat (i32 4)
  %v_1.i4334 = tail call <8 x i32> @llvm.x86.avx2.gather.d.d.256(<8 x i32> undef, ptr readonly %CellStartEnd_load20452046__data, <8 x i32> %175, <8 x i32> %new_mask2034, i8 1)
  %isneg = icmp slt <8 x i32> %v_1.i4332, zeroinitializer
  %"oldMask&test2073" = select <8 x i1> %isneg, <8 x i32> %new_mask2034, <8 x i32> zeroinitializer
  %"mask|continueMask2078" = or <8 x i32> %"oldMask&test2073", %"oldMask&test2008"
  %176 = icmp slt <8 x i32> %"mask|continueMask2078", zeroinitializer
  %177 = bitcast <8 x i1> %176 to i8
  %"equal_finished&func2086_internal_mask&function_mask1998" = icmp eq i8 %170, %177
  br i1 %"equal_finished&func2086_internal_mask&function_mask1998", label %for_step1984, label %not_all_continued_or_breaked2089

not_all_continued_or_breaked2089:                 ; preds = %not_all_continued_or_breaked2023
  %new_mask2100 = xor <8 x i32> %"mask|continueMask2078", %"oldMask&test19924579"
  %less_i_load2112_end_load21134566 = icmp slt <8 x i32> %v_1.i4332, %v_1.i4334
  %"oldMask&test21154567" = select <8 x i1> %less_i_load2112_end_load21134566, <8 x i32> %new_mask2100, <8 x i32> zeroinitializer
  %178 = icmp slt <8 x i32> %"oldMask&test21154567", zeroinitializer
  %179 = bitcast <8 x i1> %178 to i8
  %cmp.i3895.not4568 = icmp eq i8 %179, 0
  br i1 %cmp.i3895.not4568, label %for_step1984, label %for_loop2104

for_loop2104:                                     ; preds = %not_all_continued_or_breaked2089, %for_step2105
  %180 = phi i8 [ %185, %for_step2105 ], [ %179, %not_all_continued_or_breaked2089 ]
  %"oldMask&test21154572" = phi <8 x i32> [ %"oldMask&test2115", %for_step2105 ], [ %"oldMask&test21154567", %not_all_continued_or_breaked2089 ]
  %i2110.04571 = phi <8 x i32> [ %i_load2211_plus1, %for_step2105 ], [ %v_1.i4332, %not_all_continued_or_breaked2089 ]
  %bestIdx1926.44570 = phi <8 x i32> [ %bestIdx1926.5, %for_step2105 ], [ %bestIdx1926.24577, %not_all_continued_or_breaked2089 ]
  %bestDistSq1925.44569 = phi <8 x float> [ %bestDistSq1925.5, %for_step2105 ], [ %bestDistSq1925.24576, %not_all_continued_or_breaked2089 ]
  %mul__i_load2123 = shl nsw <8 x i32> %i2110.04571, splat (i32 3)
  %mask.i4336 = bitcast <8 x i32> %"oldMask&test21154572" to <8 x float>
  %v_1.i4337 = tail call <8 x float> @llvm.x86.avx2.gather.d.ps.256(<8 x float> undef, ptr readonly %SortedPositions_ptr, <8 x i32> %mul__i_load2123, <8 x float> %mask.i4336, i8 1)
  %181 = or disjoint <8 x i32> %mul__i_load2123, splat (i32 4)
  %v_1.i4340 = tail call <8 x float> @llvm.x86.avx2.gather.d.ps.256(<8 x float> undef, ptr readonly %SortedPositions_ptr, <8 x i32> %181, <8 x float> %mask.i4336, i8 1)
  %sub_a14_x_b26_x.i.i4144 = fsub <8 x float> %v_1.i4337, %v_1.i4327
  %sub_a19_y_b211_y.i.i4145 = fsub <8 x float> %v_1.i4340, %v_1.i4330
  %mul_a13_x_b25_x.i.i.i4153 = fmul <8 x float> %sub_a14_x_b26_x.i.i4144, %sub_a14_x_b26_x.i.i4144
  %mul_a17_y_b29_y.i.i.i4154 = fmul <8 x float> %sub_a19_y_b211_y.i.i4145, %sub_a19_y_b211_y.i.i4145
  %add_mul_a13_x_b25_x_mul_a17_y_b29_y.i.i.i4155 = fadd <8 x float> %mul_a13_x_b25_x.i.i.i4153, %mul_a17_y_b29_y.i.i.i4154
  %less_distSq_load2149_SquaredEpsilonSelf_load2150_broadcast2151 = fcmp olt <8 x float> %add_mul_a13_x_b25_x_mul_a17_y_b29_y.i.i.i4155, %SquaredEpsilonSelf_load2150_broadcast2151
  %"oldMask&test2154" = select <8 x i1> %less_distSq_load2149_SquaredEpsilonSelf_load2150_broadcast2151, <8 x i32> %"oldMask&test21154572", <8 x i32> zeroinitializer
  %182 = icmp slt <8 x i32> %"oldMask&test2154", zeroinitializer
  %183 = bitcast <8 x i1> %182 to i8
  %"equal_finished&func2167_internal_mask&function_mask2121" = icmp eq i8 %180, %183
  br i1 %"equal_finished&func2167_internal_mask&function_mask2121", label %for_step2105, label %not_all_continued_or_breaked2169

for_step2105:                                     ; preds = %for_loop2104, %not_all_continued_or_breaked2169
  %bestDistSq1925.5 = phi <8 x float> [ %bestDistSq1925.44569, %for_loop2104 ], [ %blend.i.i4344, %not_all_continued_or_breaked2169 ]
  %bestIdx1926.5 = phi <8 x i32> [ %bestIdx1926.44570, %for_loop2104 ], [ %188, %not_all_continued_or_breaked2169 ]
  %internal_mask_memory.31 = phi <8 x i32> [ zeroinitializer, %for_loop2104 ], [ %new_mask2180, %not_all_continued_or_breaked2169 ]
  %"mask|continue_mask2208" = or <8 x i32> %internal_mask_memory.31, %"oldMask&test2154"
  %i_load2211_plus1 = add nsw <8 x i32> %i2110.04571, splat (i32 1)
  %less_i_load2112_end_load2113 = icmp slt <8 x i32> %i_load2211_plus1, %v_1.i4334
  %"oldMask&test2115" = select <8 x i1> %less_i_load2112_end_load2113, <8 x i32> %"mask|continue_mask2208", <8 x i32> zeroinitializer
  %184 = icmp slt <8 x i32> %"oldMask&test2115", zeroinitializer
  %185 = bitcast <8 x i1> %184 to i8
  %cmp.i3895.not = icmp eq i8 %185, 0
  br i1 %cmp.i3895.not, label %for_step1984, label %for_loop2104, !llvm.loop !52

not_all_continued_or_breaked2169:                 ; preds = %for_loop2104
  %new_mask2180 = xor <8 x i32> %"oldMask&test2154", %"oldMask&test21154572"
  %less_distSq_load2183_bestDistSq_load2184 = fcmp olt <8 x float> %add_mul_a13_x_b25_x_mul_a17_y_b29_y.i.i.i4155, %bestDistSq1925.44569
  %186 = bitcast <8 x i32> %new_mask2180 to <8 x float>
  %mask_as_float.i.i4342 = select <8 x i1> %less_distSq_load2183_bestDistSq_load2184, <8 x float> %186, <8 x float> zeroinitializer
  %blend.i.i4344 = call <8 x float> @llvm.x86.avx.blendv.ps.256(<8 x float> %bestDistSq1925.44569, <8 x float> %add_mul_a13_x_b25_x_mul_a17_y_b29_y.i.i.i4155, <8 x float> %mask_as_float.i.i4342)
  %187 = bitcast <8 x i32> %bestIdx1926.44570 to <8 x float>
  %newAsFloat.i4347 = bitcast <8 x i32> %i2110.04571 to <8 x float>
  %blend.i4348 = call <8 x float> @llvm.x86.avx.blendv.ps.256(<8 x float> %187, <8 x float> %newAsFloat.i4347, <8 x float> %mask_as_float.i.i4342)
  %188 = bitcast <8 x float> %blend.i4348 to <8 x i32>
  br label %for_step2105

safe_if_after_true2242:                           ; preds = %safe_if_run_true2243, %for_exit1930
  %"oldMask&~test2267" = select <8 x i1> %notequal_bestIdx_load2239_, <8 x i32> zeroinitializer, <8 x i32> %cmp1852_to_boolvec
  %not.notequal_bestIdx_load2239_ = xor <8 x i1> %notequal_bestIdx_load2239_, splat (i1 true)
  %189 = select <8 x i1> %not.notequal_bestIdx_load2239_, <8 x i1> %cmp1852, <8 x i1> zeroinitializer
  %190 = bitcast <8 x i1> %189 to i8
  %cmp.i3898.not = icmp eq i8 %190, 0
  br i1 %cmp.i3898.not, label %common.ret, label %for_test2272.preheader

for_test2272.preheader:                           ; preds = %safe_if_after_true2242
  %SortedLength_load2281_broadcast = insertelement <8 x i32> poison, i32 %SortedLength_ptr_load_load, i64 0
  %SortedLength_load2281_broadcast2282 = shufflevector <8 x i32> %SortedLength_load2281_broadcast, <8 x i32> poison, <8 x i32> zeroinitializer
  %less_i_load2280_SortedLength_load2281_broadcast22824589 = icmp sgt <8 x i32> %SortedLength_load2281_broadcast2282, zeroinitializer
  %"oldMask&test22844590" = select <8 x i1> %less_i_load2280_SortedLength_load2281_broadcast22824589, <8 x i32> %"oldMask&~test2267", <8 x i32> zeroinitializer
  %191 = icmp slt <8 x i32> %"oldMask&test22844590", zeroinitializer
  %192 = bitcast <8 x i1> %191 to i8
  %cmp.i3899.not4591 = icmp eq i8 %192, 0
  br i1 %cmp.i3899.not4591, label %for_exit2275, label %for_loop2273.lr.ph

for_loop2273.lr.ph:                               ; preds = %for_test2272.preheader
  %invariant.gep4599 = getelementptr i8, ptr %SortedPositions_ptr, i64 4
  %SquaredEpsilonSelf_load2319_broadcast = insertelement <8 x float> poison, float %SquaredEpsilonSelf_ptr_load_load, i64 0
  %SquaredEpsilonSelf_load2319_broadcast2320 = shufflevector <8 x float> %SquaredEpsilonSelf_load2319_broadcast, <8 x float> poison, <8 x i32> zeroinitializer
  br label %for_loop2273

safe_if_run_true2243:                             ; preds = %for_exit1930
  %"oldMask&test2244" = select <8 x i1> %notequal_bestIdx_load2239_, <8 x i32> %cmp1852_to_boolvec, <8 x i32> zeroinitializer
  %mul__bestIdx_load2252 = shl nsw <8 x i32> %bestIdx1926.0.lcssa, splat (i32 3)
  %193 = or disjoint <8 x i32> %mul__bestIdx_load2252, splat (i32 4)
  %new_add3674 = sext <8 x i32> %193 to <8 x i64>
  %vecmask_1.i4349 = shufflevector <8 x i32> %"oldMask&test2244", <8 x i32> poison, <4 x i32> <i32 0, i32 1, i32 2, i32 3>
  %vecmask_2.i4350 = shufflevector <8 x i32> %"oldMask&test2244", <8 x i32> poison, <4 x i32> <i32 4, i32 5, i32 6, i32 7>
  %offsets_1.i4351 = shufflevector <8 x i64> %new_add3674, <8 x i64> poison, <4 x i32> <i32 0, i32 1, i32 2, i32 3>
  %offsets_2.i4352 = shufflevector <8 x i64> %new_add3674, <8 x i64> poison, <4 x i32> <i32 4, i32 5, i32 6, i32 7>
  %v1_1.i4353 = tail call <4 x i32> @llvm.x86.avx2.gather.q.d.256(<4 x i32> undef, ptr %HashIndex_ptr, <4 x i64> %offsets_1.i4351, <4 x i32> %vecmask_1.i4349, i8 1)
  %v2_1.i4354 = tail call <4 x i32> @llvm.x86.avx2.gather.q.d.256(<4 x i32> undef, ptr %HashIndex_ptr, <4 x i64> %offsets_2.i4352, <4 x i32> %vecmask_2.i4350, i8 1)
  %v.i4355 = shufflevector <4 x i32> %v1_1.i4353, <4 x i32> %v2_1.i4354, <8 x i32> <i32 0, i32 1, i32 2, i32 3, i32 4, i32 5, i32 6, i32 7>
  %val.i4356 = bitcast <8 x i32> %v.i4355 to <8 x float>
  call void @llvm.x86.avx.maskstore.ps.256(ptr %ptr3576, <8 x i32> %"oldMask&test2244", <8 x float> %val.i4356)
  br label %safe_if_after_true2242

for_loop2273:                                     ; preds = %for_loop2273.lr.ph, %for_step2274
  %indvars.iv4677 = phi i64 [ 0, %for_loop2273.lr.ph ], [ %indvars.iv.next4678, %for_step2274 ]
  %194 = phi i8 [ %192, %for_loop2273.lr.ph ], [ %199, %for_step2274 ]
  %"oldMask&test22844597" = phi <8 x i32> [ %"oldMask&test22844590", %for_loop2273.lr.ph ], [ %"oldMask&test2284", %for_step2274 ]
  %i2279.04596 = phi <8 x i32> [ zeroinitializer, %for_loop2273.lr.ph ], [ %i_load2380_plus1, %for_step2274 ]
  %bestIdx1926.64593 = phi <8 x i32> [ %bestIdx1926.0.lcssa, %for_loop2273.lr.ph ], [ %bestIdx1926.7, %for_step2274 ]
  %bestDistSq1925.64592 = phi <8 x float> [ %bestDistSq1925.0.lcssa, %for_loop2273.lr.ph ], [ %bestDistSq1925.7, %for_step2274 ]
  %195 = shl nsw i64 %indvars.iv4677, 3
  %ptr3691 = getelementptr i8, ptr %SortedPositions_ptr, i64 %195
  %SortedPositions_ptr_load2293_offset_load36903692 = load float, ptr %ptr3691, align 4
  %SortedPositions_ptr_load2293_offset_load36903693 = insertelement <8 x float> poison, float %SortedPositions_ptr_load2293_offset_load36903692, i64 0
  %SortedPositions_ptr_load2293_offset_load36903694 = shufflevector <8 x float> %SortedPositions_ptr_load2293_offset_load36903693, <8 x float> poison, <8 x i32> zeroinitializer
  %gep4600 = getelementptr i8, ptr %invariant.gep4599, i64 %195
  %SortedPositions_ptr_load2293_offset_load230837003705 = load float, ptr %gep4600, align 4
  %SortedPositions_ptr_load2293_offset_load230837003706 = insertelement <8 x float> poison, float %SortedPositions_ptr_load2293_offset_load230837003705, i64 0
  %SortedPositions_ptr_load2293_offset_load230837003707 = shufflevector <8 x float> %SortedPositions_ptr_load2293_offset_load230837003706, <8 x float> poison, <8 x i32> zeroinitializer
  %sub_a14_x_b26_x.i.i4156 = fsub <8 x float> %SortedPositions_ptr_load2293_offset_load36903694, %v_1.i4327
  %sub_a19_y_b211_y.i.i4157 = fsub <8 x float> %SortedPositions_ptr_load2293_offset_load230837003707, %v_1.i4330
  %mul_a13_x_b25_x.i.i.i4165 = fmul <8 x float> %sub_a14_x_b26_x.i.i4156, %sub_a14_x_b26_x.i.i4156
  %mul_a17_y_b29_y.i.i.i4166 = fmul <8 x float> %sub_a19_y_b211_y.i.i4157, %sub_a19_y_b211_y.i.i4157
  %add_mul_a13_x_b25_x_mul_a17_y_b29_y.i.i.i4167 = fadd <8 x float> %mul_a13_x_b25_x.i.i.i4165, %mul_a17_y_b29_y.i.i.i4166
  %less_distSq_load2318_SquaredEpsilonSelf_load2319_broadcast2320 = fcmp olt <8 x float> %add_mul_a13_x_b25_x_mul_a17_y_b29_y.i.i.i4167, %SquaredEpsilonSelf_load2319_broadcast2320
  %"oldMask&test2323" = select <8 x i1> %less_distSq_load2318_SquaredEpsilonSelf_load2319_broadcast2320, <8 x i32> %"oldMask&test22844597", <8 x i32> zeroinitializer
  %196 = icmp slt <8 x i32> %"oldMask&test2323", zeroinitializer
  %197 = bitcast <8 x i1> %196 to i8
  %"equal_finished&func2336_internal_mask&function_mask2290" = icmp eq i8 %194, %197
  br i1 %"equal_finished&func2336_internal_mask&function_mask2290", label %for_step2274, label %not_all_continued_or_breaked2338

for_step2274:                                     ; preds = %for_loop2273, %not_all_continued_or_breaked2338
  %bestDistSq1925.7 = phi <8 x float> [ %bestDistSq1925.64592, %for_loop2273 ], [ %blend.i.i4359, %not_all_continued_or_breaked2338 ]
  %bestIdx1926.7 = phi <8 x i32> [ %bestIdx1926.64593, %for_loop2273 ], [ %204, %not_all_continued_or_breaked2338 ]
  %internal_mask_memory.33 = phi <8 x i32> [ zeroinitializer, %for_loop2273 ], [ %new_mask2349, %not_all_continued_or_breaked2338 ]
  %"mask|continue_mask2377" = or <8 x i32> %internal_mask_memory.33, %"oldMask&test2323"
  %indvars.iv.next4678 = add nuw nsw i64 %indvars.iv4677, 1
  %i_load2380_plus1 = add nuw nsw <8 x i32> %i2279.04596, splat (i32 1)
  %less_i_load2280_SortedLength_load2281_broadcast2282 = icmp slt <8 x i32> %i_load2380_plus1, %SortedLength_load2281_broadcast2282
  %"oldMask&test2284" = select <8 x i1> %less_i_load2280_SortedLength_load2281_broadcast2282, <8 x i32> %"mask|continue_mask2377", <8 x i32> zeroinitializer
  %198 = icmp slt <8 x i32> %"oldMask&test2284", zeroinitializer
  %199 = bitcast <8 x i1> %198 to i8
  %cmp.i3899.not = icmp eq i8 %199, 0
  br i1 %cmp.i3899.not, label %for_exit2275, label %for_loop2273, !llvm.loop !53

for_exit2275:                                     ; preds = %for_step2274, %for_test2272.preheader
  %bestIdx1926.6.lcssa = phi <8 x i32> [ %bestIdx1926.0.lcssa, %for_test2272.preheader ], [ %bestIdx1926.7, %for_step2274 ]
  %notequal_bestIdx_load2386_ = icmp eq <8 x i32> %bestIdx1926.6.lcssa, splat (i32 -1)
  %"oldMask&test2391" = select <8 x i1> %notequal_bestIdx_load2386_, <8 x i32> zeroinitializer, <8 x i32> %"oldMask&~test2267"
  %200 = icmp slt <8 x i32> %"oldMask&test2391", zeroinitializer
  %201 = bitcast <8 x i1> %200 to i8
  %cmp.i3902.not = icmp eq i8 %201, 0
  br i1 %cmp.i3902.not, label %common.ret, label %safe_if_run_true2390

not_all_continued_or_breaked2338:                 ; preds = %for_loop2273
  %new_mask2349 = xor <8 x i32> %"oldMask&test2323", %"oldMask&test22844597"
  %less_distSq_load2352_bestDistSq_load2353 = fcmp olt <8 x float> %add_mul_a13_x_b25_x_mul_a17_y_b29_y.i.i.i4167, %bestDistSq1925.64592
  %202 = bitcast <8 x i32> %new_mask2349 to <8 x float>
  %mask_as_float.i.i4357 = select <8 x i1> %less_distSq_load2352_bestDistSq_load2353, <8 x float> %202, <8 x float> zeroinitializer
  %blend.i.i4359 = call <8 x float> @llvm.x86.avx.blendv.ps.256(<8 x float> %bestDistSq1925.64592, <8 x float> %add_mul_a13_x_b25_x_mul_a17_y_b29_y.i.i.i4167, <8 x float> %mask_as_float.i.i4357)
  %203 = bitcast <8 x i32> %bestIdx1926.64593 to <8 x float>
  %newAsFloat.i4362 = bitcast <8 x i32> %i2279.04596 to <8 x float>
  %blend.i4363 = call <8 x float> @llvm.x86.avx.blendv.ps.256(<8 x float> %203, <8 x float> %newAsFloat.i4362, <8 x float> %mask_as_float.i.i4357)
  %204 = bitcast <8 x float> %blend.i4363 to <8 x i32>
  br label %for_step2274

safe_if_run_true2390:                             ; preds = %for_exit2275
  %mul__bestIdx_load2399 = shl nsw <8 x i32> %bestIdx1926.6.lcssa, splat (i32 3)
  %205 = or disjoint <8 x i32> %mul__bestIdx_load2399, splat (i32 4)
  %new_add3712 = sext <8 x i32> %205 to <8 x i64>
  %vecmask_1.i4364 = shufflevector <8 x i32> %"oldMask&test2391", <8 x i32> poison, <4 x i32> <i32 0, i32 1, i32 2, i32 3>
  %vecmask_2.i4365 = shufflevector <8 x i32> %"oldMask&test2391", <8 x i32> poison, <4 x i32> <i32 4, i32 5, i32 6, i32 7>
  %offsets_1.i4366 = shufflevector <8 x i64> %new_add3712, <8 x i64> poison, <4 x i32> <i32 0, i32 1, i32 2, i32 3>
  %offsets_2.i4367 = shufflevector <8 x i64> %new_add3712, <8 x i64> poison, <4 x i32> <i32 4, i32 5, i32 6, i32 7>
  %v1_1.i4368 = tail call <4 x i32> @llvm.x86.avx2.gather.q.d.256(<4 x i32> undef, ptr %HashIndex_ptr, <4 x i64> %offsets_1.i4366, <4 x i32> %vecmask_1.i4364, i8 1)
  %v2_1.i4369 = tail call <4 x i32> @llvm.x86.avx2.gather.q.d.256(<4 x i32> undef, ptr %HashIndex_ptr, <4 x i64> %offsets_2.i4367, <4 x i32> %vecmask_2.i4365, i8 1)
  %v.i4370 = shufflevector <4 x i32> %v1_1.i4368, <4 x i32> %v2_1.i4369, <8 x i32> <i32 0, i32 1, i32 2, i32 3, i32 4, i32 5, i32 6, i32 7>
  %val.i4371 = bitcast <8 x i32> %v.i4370 to <8 x float>
  call void @llvm.x86.avx.maskstore.ps.256(ptr %ptr3576, <8 x i32> %"oldMask&test2391", <8 x float> %val.i4371)
  br label %common.ret
}

; Function Attrs: nounwind uwtable
define void @SharpNative_Job__global_namespace__GridSearch2D_ClosestPointJobPointer_Execute_Batch_false_impl(i32 %__startIndex, i32 %__count, ptr noalias readonly captures(none) %GridOrigin_ptr, ptr noalias readonly captures(none) %GridResolutionInv_ptr, ptr noalias readonly captures(none) %GridDimensions_ptr, ptr noalias %QueryPositions_ptr, i32 %QueryPositions_length, ptr noalias %SortedPositions_ptr, i32 %SortedPositions_length, ptr noalias %HashIndex_ptr, i32 %HashIndex_length, ptr noalias readonly captures(none) %CellStartEnd, ptr noalias readonly captures(none) %SortedLength_ptr, ptr noalias readnone captures(none) %IgnoreSelf_ptr, ptr noalias readnone captures(none) %SquaredEpsilonSelf_ptr, ptr noalias captures(none) %Results_ptr, i32 %Results_length) local_unnamed_addr #0 {
allocas:
  %GridOrigin_ptr_load_load.unpack = load float, ptr %GridOrigin_ptr, align 4
  %GridOrigin_ptr_load_load.elt2559 = getelementptr inbounds nuw i8, ptr %GridOrigin_ptr, i64 4
  %GridOrigin_ptr_load_load.unpack2560 = load float, ptr %GridOrigin_ptr_load_load.elt2559, align 4
  %GridResolutionInv_ptr_load_load = load float, ptr %GridResolutionInv_ptr, align 4
  %GridDimensions_ptr_load_load.unpack = load i32, ptr %GridDimensions_ptr, align 4
  %GridDimensions_ptr_load_load.elt2562 = getelementptr inbounds nuw i8, ptr %GridDimensions_ptr, i64 4
  %GridDimensions_ptr_load_load.unpack2563 = load i32, ptr %GridDimensions_ptr_load_load.elt2562, align 4
  %SortedLength_ptr_load_load = load i32, ptr %SortedLength_ptr, align 4
  %add___startIndex_load24___count_load = add nsw i32 %__count, %__startIndex
  %ret.i.i = tail call i32 @llvm.smin.i32(i32 %QueryPositions_length, i32 %add___startIndex_load24___count_load)
  %nitems = sub nsw i32 %ret.i.i, %__startIndex
  %nextras = srem i32 %nitems, 8
  %aligned_end = sub nsw i32 %ret.i.i, %nextras
  %before_aligned_end303505 = icmp slt i32 %__startIndex, %aligned_end
  br i1 %before_aligned_end303505, label %foreach_full_body.lr.ph, label %partial_inner_all_outer, !llvm.loop !54

foreach_full_body.lr.ph:                          ; preds = %allocas
  %get_element_broadcast = insertelement <8 x float> poison, float %GridOrigin_ptr_load_load.unpack, i64 0
  %get_element_broadcast49 = shufflevector <8 x float> %get_element_broadcast, <8 x float> poison, <8 x i32> zeroinitializer
  %get_element50_broadcast = insertelement <8 x float> poison, float %GridOrigin_ptr_load_load.unpack2560, i64 0
  %get_element50_broadcast51 = shufflevector <8 x float> %get_element50_broadcast, <8 x float> poison, <8 x i32> zeroinitializer
  %GridResolutionInv_load_broadcast = insertelement <8 x float> poison, float %GridResolutionInv_ptr_load_load, i64 0
  %GridResolutionInv_load_broadcast55 = shufflevector <8 x float> %GridResolutionInv_load_broadcast, <8 x float> poison, <8 x i32> zeroinitializer
  %get_element64_broadcast = insertelement <8 x i32> poison, i32 %GridDimensions_ptr_load_load.unpack, i64 0
  %get_element64_broadcast65 = shufflevector <8 x i32> %get_element64_broadcast, <8 x i32> poison, <8 x i32> zeroinitializer
  %get_element67_broadcast = insertelement <8 x i32> poison, i32 %GridDimensions_ptr_load_load.unpack2563, i64 0
  %get_element67_broadcast68 = shufflevector <8 x i32> %get_element67_broadcast, <8 x i32> poison, <8 x i32> zeroinitializer
  %sub_a14_x_b_load.i = add nsw <8 x i32> %get_element64_broadcast65, splat (i32 -1)
  %sub_a17_y_b_load9.i = add nsw <8 x i32> %get_element67_broadcast68, splat (i32 -1)
  %SortedLength_load_broadcast = insertelement <8 x i32> poison, i32 %SortedLength_ptr_load_load, i64 0
  %SortedLength_load_broadcast289 = shufflevector <8 x i32> %SortedLength_load_broadcast, <8 x i32> poison, <8 x i32> zeroinitializer
  %less_i_load288_SortedLength_load_broadcast2893495 = icmp sgt <8 x i32> %SortedLength_load_broadcast289, zeroinitializer
  %invariant.gep = getelementptr i8, ptr %SortedPositions_ptr, i64 4
  %0 = sext i32 %__startIndex to i64
  %1 = sext i32 %aligned_end to i64
  br label %foreach_full_body, !llvm.loop !54

foreach_full_body:                                ; preds = %foreach_full_body.lr.ph, %if_done
  %indvars.iv3548 = phi i64 [ %0, %foreach_full_body.lr.ph ], [ %indvars.iv.next3549, %if_done ]
  %2 = trunc nsw i64 %indvars.iv3548 to i32
  %smear_counter_init32 = insertelement <8 x i32> poison, i32 %2, i64 0
  %smear_counter33 = shufflevector <8 x i32> %smear_counter_init32, <8 x i32> poison, <8 x i32> zeroinitializer
  %iter_val34 = add nsw <8 x i32> %smear_counter33, <i32 0, i32 1, i32 2, i32 3, i32 4, i32 5, i32 6, i32 7>
  %3 = shl nsw i64 %indvars.iv3548, 2
  %ptr = getelementptr i8, ptr %Results_ptr, i64 %3
  store <8 x i32> splat (i32 -1), ptr %ptr, align 4, !filename !11, !first_line !12, !first_column !13, !last_line !12, !last_column !14
  %mul__index_load39 = shl nsw <8 x i32> %iter_val34, splat (i32 3)
  %v_1.i = tail call <8 x float> @llvm.x86.avx2.gather.d.ps.256(<8 x float> undef, ptr readonly %QueryPositions_ptr, <8 x i32> %mul__index_load39, <8 x float> splat (float 0xFFFFFFFFE0000000), i8 1)
  %4 = or disjoint <8 x i32> %mul__index_load39, splat (i32 4)
  %v_1.i3336 = tail call <8 x float> @llvm.x86.avx2.gather.d.ps.256(<8 x float> undef, ptr readonly %QueryPositions_ptr, <8 x i32> %4, <8 x float> splat (float 0xFFFFFFFFE0000000), i8 1)
  %sub_a14_x_b26_x.i = fsub <8 x float> %v_1.i, %get_element_broadcast49
  %sub_a19_y_b211_y.i = fsub <8 x float> %v_1.i3336, %get_element50_broadcast51
  %mul_v14_x_s_load.i = fmul <8 x float> %GridResolutionInv_load_broadcast55, %sub_a14_x_b26_x.i
  %mul_v17_y_s_load9.i = fmul <8 x float> %GridResolutionInv_load_broadcast55, %sub_a19_y_b211_y.i
  %call.i.i.i = tail call <8 x float> @llvm.x86.avx.round.ps.256(<8 x float> %mul_v14_x_s_load.i, i32 9)
  %call.i.i3.i = tail call <8 x float> @llvm.x86.avx.round.ps.256(<8 x float> %mul_v17_y_s_load9.i, i32 9)
  %v12_x_to_int32.i = fptosi <8 x float> %call.i.i.i to <8 x i32>
  %v14_y_to_int32.i = fptosi <8 x float> %call.i.i3.i to <8 x i32>
  %5 = call <8 x i32> @llvm.smax.v8i32(<8 x i32> %v12_x_to_int32.i, <8 x i32> zeroinitializer)
  %6 = call <8 x i32> @llvm.smax.v8i32(<8 x i32> %v14_y_to_int32.i, <8 x i32> zeroinitializer)
  %blend.i3344.v = call <8 x i32> @llvm.smin.v8i32(<8 x i32> %5, <8 x i32> %sub_a14_x_b_load.i)
  %blend.i3348.v = call <8 x i32> @llvm.smin.v8i32(<8 x i32> %6, <8 x i32> %sub_a17_y_b_load9.i)
  br label %for_loop

for_loop:                                         ; preds = %foreach_full_body, %for_step
  %7 = phi i8 [ -1, %foreach_full_body ], [ %11, %for_step ]
  %"oldMask&test3494" = phi <8 x i32> [ splat (i32 -1), %foreach_full_body ], [ %"oldMask&test", %for_step ]
  %dx.03493 = phi <8 x i32> [ splat (i32 -1), %foreach_full_body ], [ %dx_load261_plus1, %for_step ]
  %bestIdx.03492 = phi <8 x i32> [ splat (i32 -1), %foreach_full_body ], [ %bestIdx.1, %for_step ]
  %bestDistSq.03491 = phi <8 x float> [ splat (float 0x47EFFFFFE0000000), %foreach_full_body ], [ %bestDistSq.1, %for_step ]
  %add_cell78_x_dx_load80 = add nsw <8 x i32> %dx.03493, %blend.i3344.v
  %greaterequal_nx_load_GridDimensions81_x_broadcast83.not = icmp ult <8 x i32> %add_cell78_x_dx_load80, %get_element64_broadcast65
  %"oldMask&test85" = select <8 x i1> %greaterequal_nx_load_GridDimensions81_x_broadcast83.not, <8 x i32> zeroinitializer, <8 x i32> %"oldMask&test3494"
  %8 = icmp slt <8 x i32> %"oldMask&test85", zeroinitializer
  %9 = bitcast <8 x i1> %8 to i8
  %"equal_finished&func_load_mask77" = icmp eq i8 %7, %9
  br i1 %"equal_finished&func_load_mask77", label %for_step, label %not_all_continued_or_breaked

for_step:                                         ; preds = %not_all_continued_or_breaked, %for_step94, %for_loop
  %bestDistSq.1 = phi <8 x float> [ %bestDistSq.03491, %for_loop ], [ %bestDistSq.03491, %not_all_continued_or_breaked ], [ %bestDistSq.3, %for_step94 ]
  %bestIdx.1 = phi <8 x i32> [ %bestIdx.03492, %for_loop ], [ %bestIdx.03492, %not_all_continued_or_breaked ], [ %bestIdx.3, %for_step94 ]
  %internal_mask_memory.2 = phi <8 x i32> [ zeroinitializer, %for_loop ], [ %new_mask91, %not_all_continued_or_breaked ], [ %new_mask91, %for_step94 ]
  %"mask|continue_mask260" = or <8 x i32> %internal_mask_memory.2, %"oldMask&test85"
  %dx_load261_plus1 = add nsw <8 x i32> %dx.03493, splat (i32 1)
  %lessequal_dx_load_.inv = icmp sgt <8 x i32> %dx.03493, zeroinitializer
  %"oldMask&test" = select <8 x i1> %lessequal_dx_load_.inv, <8 x i32> zeroinitializer, <8 x i32> %"mask|continue_mask260"
  %10 = icmp slt <8 x i32> %"oldMask&test", zeroinitializer
  %11 = bitcast <8 x i1> %10 to i8
  %cmp.i.not = icmp eq i8 %11, 0
  br i1 %cmp.i.not, label %for_exit, label %for_loop, !llvm.loop !55

for_exit:                                         ; preds = %for_step
  %notequal_bestIdx_load_ = icmp ne <8 x i32> %bestIdx.1, splat (i32 -1)
  %notequal_bestIdx_load__to_boolvec = sext <8 x i1> %notequal_bestIdx_load_ to <8 x i32>
  %12 = bitcast <8 x i1> %notequal_bestIdx_load_ to i8
  %cmp.i3012.not = icmp eq i8 %12, 0
  br i1 %cmp.i3012.not, label %safe_if_after_true, label %safe_if_run_true

for_loop472:                                      ; preds = %partial_inner_only, %for_step473
  %13 = phi i8 [ %17, %for_step473 ], [ %57, %partial_inner_only ]
  %"oldMask&test4813528" = phi <8 x i32> [ %"oldMask&test481", %for_step473 ], [ %cmp414_to_boolvec, %partial_inner_only ]
  %dx478.03527 = phi <8 x i32> [ %dx_load707_plus1, %for_step473 ], [ splat (i32 -1), %partial_inner_only ]
  %bestIdx470.03526 = phi <8 x i32> [ %bestIdx470.1, %for_step473 ], [ splat (i32 -1), %partial_inner_only ]
  %bestDistSq469.03525 = phi <8 x float> [ %bestDistSq469.1, %for_step473 ], [ splat (float 0x47EFFFFFE0000000), %partial_inner_only ]
  %add_cell436485_x_dx_load487 = add nsw <8 x i32> %dx478.03527, %blend.i16.i.v
  %greaterequal_nx_load488_GridDimensions489_x_broadcast491.not = icmp ult <8 x i32> %add_cell436485_x_dx_load487, %get_element459_broadcast460
  %"oldMask&test493" = select <8 x i1> %greaterequal_nx_load488_GridDimensions489_x_broadcast491.not, <8 x i32> zeroinitializer, <8 x i32> %"oldMask&test4813528"
  %14 = icmp slt <8 x i32> %"oldMask&test493", zeroinitializer
  %15 = bitcast <8 x i1> %14 to i8
  %"equal_finished&func502_load_mask483" = icmp eq i8 %13, %15
  br i1 %"equal_finished&func502_load_mask483", label %for_step473, label %not_all_continued_or_breaked504

for_step473:                                      ; preds = %not_all_continued_or_breaked504, %for_step516, %for_loop472
  %bestDistSq469.1 = phi <8 x float> [ %bestDistSq469.03525, %for_loop472 ], [ %bestDistSq469.03525, %not_all_continued_or_breaked504 ], [ %bestDistSq469.3, %for_step516 ]
  %bestIdx470.1 = phi <8 x i32> [ %bestIdx470.03526, %for_loop472 ], [ %bestIdx470.03526, %not_all_continued_or_breaked504 ], [ %bestIdx470.3, %for_step516 ]
  %internal_mask_memory.10 = phi <8 x i32> [ zeroinitializer, %for_loop472 ], [ %new_mask513, %not_all_continued_or_breaked504 ], [ %new_mask513, %for_step516 ]
  %"mask|continue_mask706" = or <8 x i32> %internal_mask_memory.10, %"oldMask&test493"
  %dx_load707_plus1 = add nsw <8 x i32> %dx478.03527, splat (i32 1)
  %lessequal_dx_load479_.inv = icmp sgt <8 x i32> %dx478.03527, zeroinitializer
  %"oldMask&test481" = select <8 x i1> %lessequal_dx_load479_.inv, <8 x i32> zeroinitializer, <8 x i32> %"mask|continue_mask706"
  %16 = icmp slt <8 x i32> %"oldMask&test481", zeroinitializer
  %17 = bitcast <8 x i1> %16 to i8
  %cmp.i3013.not = icmp eq i8 %17, 0
  br i1 %cmp.i3013.not, label %for_exit474, label %for_loop472, !llvm.loop !56

for_exit474:                                      ; preds = %for_step473, %partial_inner_only
  %bestDistSq469.0.lcssa = phi <8 x float> [ splat (float 0x47EFFFFFE0000000), %partial_inner_only ], [ %bestDistSq469.1, %for_step473 ]
  %bestIdx470.0.lcssa = phi <8 x i32> [ splat (i32 -1), %partial_inner_only ], [ %bestIdx470.1, %for_step473 ]
  %notequal_bestIdx_load711_ = icmp ne <8 x i32> %bestIdx470.0.lcssa, splat (i32 -1)
  %18 = select <8 x i1> %notequal_bestIdx_load711_, <8 x i1> %cmp414, <8 x i1> zeroinitializer
  %19 = bitcast <8 x i1> %18 to i8
  %cmp.i3016.not = icmp eq i8 %19, 0
  br i1 %cmp.i3016.not, label %safe_if_after_true714, label %safe_if_run_true715

common.ret:                                       ; preds = %for_exit737, %safe_if_run_true825, %safe_if_after_true714, %partial_inner_all_outer
  ret void

outer_not_in_extras.partial_inner_all_outer_crit_edge: ; preds = %if_done
  %20 = trunc nsw i64 %indvars.iv.next3549 to i32
  br label %partial_inner_all_outer, !llvm.loop !54

partial_inner_all_outer:                          ; preds = %outer_not_in_extras.partial_inner_all_outer_crit_edge, %allocas
  %counter.1.lcssa = phi i32 [ %20, %outer_not_in_extras.partial_inner_all_outer_crit_edge ], [ %__startIndex, %allocas ]
  %before_full_end = icmp slt i32 %counter.1.lcssa, %ret.i.i
  br i1 %before_full_end, label %partial_inner_only, label %common.ret

not_all_continued_or_breaked:                     ; preds = %for_loop
  %new_mask91 = xor <8 x i32> %"oldMask&test85", %"oldMask&test3494"
  %21 = icmp slt <8 x i32> %new_mask91, zeroinitializer
  %22 = bitcast <8 x i1> %21 to i8
  %cmp.i3017.not3484 = icmp eq i8 %22, 0
  br i1 %cmp.i3017.not3484, label %for_step, label %for_loop93

for_loop93:                                       ; preds = %not_all_continued_or_breaked, %for_step94
  %23 = phi i8 [ %28, %for_step94 ], [ %22, %not_all_continued_or_breaked ]
  %"oldMask&test1003488" = phi <8 x i32> [ %"oldMask&test100", %for_step94 ], [ %new_mask91, %not_all_continued_or_breaked ]
  %dy.03487 = phi <8 x i32> [ %dy_load254_plus1, %for_step94 ], [ splat (i32 -1), %not_all_continued_or_breaked ]
  %bestIdx.23486 = phi <8 x i32> [ %bestIdx.3, %for_step94 ], [ %bestIdx.03492, %not_all_continued_or_breaked ]
  %bestDistSq.23485 = phi <8 x float> [ %bestDistSq.3, %for_step94 ], [ %bestDistSq.03491, %not_all_continued_or_breaked ]
  %add_cell103_y_dy_load105 = add nsw <8 x i32> %dy.03487, %blend.i3348.v
  %greaterequal_ny_load_GridDimensions106_y_broadcast108.not = icmp ult <8 x i32> %add_cell103_y_dy_load105, %get_element67_broadcast68
  %"oldMask&test110" = select <8 x i1> %greaterequal_ny_load_GridDimensions106_y_broadcast108.not, <8 x i32> zeroinitializer, <8 x i32> %"oldMask&test1003488"
  %24 = icmp slt <8 x i32> %"oldMask&test110", zeroinitializer
  %25 = bitcast <8 x i1> %24 to i8
  %"equal_finished&func119_load_mask102" = icmp eq i8 %23, %25
  br i1 %"equal_finished&func119_load_mask102", label %for_step94, label %not_all_continued_or_breaked121

for_test177.for_step94.loopexit_crit_edge:        ; preds = %for_loop178
  %26 = bitcast <8 x float> %blend.i3357 to <8 x i32>
  br label %for_step94

for_step94:                                       ; preds = %not_all_continued_or_breaked167, %for_test177.for_step94.loopexit_crit_edge, %not_all_continued_or_breaked121, %for_loop93
  %bestDistSq.3 = phi <8 x float> [ %bestDistSq.23485, %for_loop93 ], [ %bestDistSq.23485, %not_all_continued_or_breaked121 ], [ %blend.i.i, %for_test177.for_step94.loopexit_crit_edge ], [ %bestDistSq.23485, %not_all_continued_or_breaked167 ]
  %bestIdx.3 = phi <8 x i32> [ %bestIdx.23486, %for_loop93 ], [ %bestIdx.23486, %not_all_continued_or_breaked121 ], [ %26, %for_test177.for_step94.loopexit_crit_edge ], [ %bestIdx.23486, %not_all_continued_or_breaked167 ]
  %continue_lanes_memory97.1 = phi <8 x i32> [ %"oldMask&test110", %for_loop93 ], [ %"mask|continueMask158", %not_all_continued_or_breaked121 ], [ %"mask|continueMask158", %for_test177.for_step94.loopexit_crit_edge ], [ %"mask|continueMask158", %not_all_continued_or_breaked167 ]
  %internal_mask_memory.4 = phi <8 x i32> [ zeroinitializer, %for_loop93 ], [ zeroinitializer, %not_all_continued_or_breaked121 ], [ %new_mask176, %for_test177.for_step94.loopexit_crit_edge ], [ %new_mask176, %not_all_continued_or_breaked167 ]
  %"mask|continue_mask253" = or <8 x i32> %internal_mask_memory.4, %continue_lanes_memory97.1
  %dy_load254_plus1 = add nsw <8 x i32> %dy.03487, splat (i32 1)
  %lessequal_dy_load_.inv = icmp sgt <8 x i32> %dy.03487, zeroinitializer
  %"oldMask&test100" = select <8 x i1> %lessequal_dy_load_.inv, <8 x i32> zeroinitializer, <8 x i32> %"mask|continue_mask253"
  %27 = icmp slt <8 x i32> %"oldMask&test100", zeroinitializer
  %28 = bitcast <8 x i1> %27 to i8
  %cmp.i3017.not = icmp eq i8 %28, 0
  br i1 %cmp.i3017.not, label %for_step, label %for_loop93, !llvm.loop !57

not_all_continued_or_breaked121:                  ; preds = %for_loop93
  %new_mask130 = xor <8 x i32> %"oldMask&test110", %"oldMask&test1003488"
  %mul_ny_load131_GridDimensions132_x_broadcast134 = mul nsw <8 x i32> %add_cell103_y_dy_load105, %get_element64_broadcast65
  %add_mul_ny_load131_GridDimensions132_x_broadcast134_nx_load135 = add nsw <8 x i32> %mul_ny_load131_GridDimensions132_x_broadcast134, %add_cell78_x_dx_load80
  %CellStartEnd_load136__data = load ptr, ptr %CellStartEnd, align 8
  %mul__cellHash_load = shl nsw <8 x i32> %add_mul_ny_load131_GridDimensions132_x_broadcast134_nx_load135, splat (i32 3)
  %v_1.i3349 = tail call <8 x i32> @llvm.x86.avx2.gather.d.d.256(<8 x i32> undef, ptr readonly %CellStartEnd_load136__data, <8 x i32> %mul__cellHash_load, <8 x i32> %new_mask130, i8 1)
  %29 = or disjoint <8 x i32> %mul__cellHash_load, splat (i32 4)
  %v_1.i3350 = tail call <8 x i32> @llvm.x86.avx2.gather.d.d.256(<8 x i32> undef, ptr readonly %CellStartEnd_load136__data, <8 x i32> %29, <8 x i32> %new_mask130, i8 1)
  %isneg2567 = icmp slt <8 x i32> %v_1.i3349, zeroinitializer
  %"oldMask&test155" = select <8 x i1> %isneg2567, <8 x i32> %new_mask130, <8 x i32> zeroinitializer
  %"mask|continueMask158" = or <8 x i32> %"oldMask&test155", %"oldMask&test110"
  %30 = icmp slt <8 x i32> %"mask|continueMask158", zeroinitializer
  %31 = bitcast <8 x i1> %30 to i8
  %"equal_finished&func164_load_mask102" = icmp eq i8 %23, %31
  br i1 %"equal_finished&func164_load_mask102", label %for_step94, label %not_all_continued_or_breaked167

not_all_continued_or_breaked167:                  ; preds = %not_all_continued_or_breaked121
  %new_mask176 = xor <8 x i32> %"mask|continueMask158", %"oldMask&test1003488"
  %less_i_load_end_load3476 = icmp slt <8 x i32> %v_1.i3349, %v_1.i3350
  %"oldMask&test1863477" = select <8 x i1> %less_i_load_end_load3476, <8 x i32> %new_mask176, <8 x i32> zeroinitializer
  %32 = icmp slt <8 x i32> %"oldMask&test1863477", zeroinitializer
  %33 = bitcast <8 x i1> %32 to i8
  %cmp.i3021.not3478 = icmp eq i8 %33, 0
  br i1 %cmp.i3021.not3478, label %for_step94, label %for_loop178.lr.ph

for_loop178.lr.ph:                                ; preds = %not_all_continued_or_breaked167
  %34 = bitcast <8 x i32> %bestIdx.23486 to <8 x float>
  br label %for_loop178

for_loop178:                                      ; preds = %for_loop178.lr.ph, %for_loop178
  %"oldMask&test1863482" = phi <8 x i32> [ %"oldMask&test1863477", %for_loop178.lr.ph ], [ %"oldMask&test186", %for_loop178 ]
  %i.03481 = phi <8 x i32> [ %v_1.i3349, %for_loop178.lr.ph ], [ %i_load247_plus1, %for_loop178 ]
  %bestIdx.43480 = phi <8 x float> [ %34, %for_loop178.lr.ph ], [ %blend.i3357, %for_loop178 ]
  %bestDistSq.43479 = phi <8 x float> [ %bestDistSq.23485, %for_loop178.lr.ph ], [ %blend.i.i, %for_loop178 ]
  %mul__i_load189 = shl nsw <8 x i32> %i.03481, splat (i32 3)
  %mask.i = bitcast <8 x i32> %"oldMask&test1863482" to <8 x float>
  %v_1.i3351 = tail call <8 x float> @llvm.x86.avx2.gather.d.ps.256(<8 x float> undef, ptr readonly %SortedPositions_ptr, <8 x i32> %mul__i_load189, <8 x float> %mask.i, i8 1)
  %35 = or disjoint <8 x i32> %mul__i_load189, splat (i32 4)
  %v_1.i3353 = tail call <8 x float> @llvm.x86.avx2.gather.d.ps.256(<8 x float> undef, ptr readonly %SortedPositions_ptr, <8 x i32> %35, <8 x float> %mask.i, i8 1)
  %sub_a14_x_b26_x.i.i = fsub <8 x float> %v_1.i3351, %v_1.i
  %mul_a13_x_b25_x.i.i.i = fmul <8 x float> %sub_a14_x_b26_x.i.i, %sub_a14_x_b26_x.i.i
  %sub_a19_y_b211_y.i.i = fsub <8 x float> %v_1.i3353, %v_1.i3336
  %mul_a17_y_b29_y.i.i.i = fmul <8 x float> %sub_a19_y_b211_y.i.i, %sub_a19_y_b211_y.i.i
  %add_mul_a13_x_b25_x_mul_a17_y_b29_y.i.i.i = fadd <8 x float> %mul_a13_x_b25_x.i.i.i, %mul_a17_y_b29_y.i.i.i
  %less_distSq_load229_bestDistSq_load = fcmp olt <8 x float> %add_mul_a13_x_b25_x_mul_a17_y_b29_y.i.i.i, %bestDistSq.43479
  %36 = bitcast <8 x i32> %"oldMask&test1863482" to <8 x float>
  %mask_as_float.i.i = select <8 x i1> %less_distSq_load229_bestDistSq_load, <8 x float> %36, <8 x float> zeroinitializer
  %blend.i.i = call <8 x float> @llvm.x86.avx.blendv.ps.256(<8 x float> %bestDistSq.43479, <8 x float> %add_mul_a13_x_b25_x_mul_a17_y_b29_y.i.i.i, <8 x float> %mask_as_float.i.i)
  %newAsFloat.i3356 = bitcast <8 x i32> %i.03481 to <8 x float>
  %blend.i3357 = call <8 x float> @llvm.x86.avx.blendv.ps.256(<8 x float> %bestIdx.43480, <8 x float> %newAsFloat.i3356, <8 x float> %mask_as_float.i.i)
  %i_load247_plus1 = add nsw <8 x i32> %i.03481, splat (i32 1)
  %less_i_load_end_load = icmp slt <8 x i32> %i_load247_plus1, %v_1.i3350
  %"oldMask&test186" = select <8 x i1> %less_i_load_end_load, <8 x i32> %"oldMask&test1863482", <8 x i32> zeroinitializer
  %37 = icmp slt <8 x i32> %"oldMask&test186", zeroinitializer
  %38 = bitcast <8 x i1> %37 to i8
  %cmp.i3021.not = icmp eq i8 %38, 0
  br i1 %cmp.i3021.not, label %for_test177.for_step94.loopexit_crit_edge, label %for_loop178, !llvm.loop !58

if_done:                                          ; preds = %for_exit283, %safe_if_run_true370, %safe_if_after_true
  %indvars.iv.next3549 = add nsw i64 %indvars.iv3548, 8
  %before_aligned_end30 = icmp slt i64 %indvars.iv.next3549, %1
  br i1 %before_aligned_end30, label %foreach_full_body, label %outer_not_in_extras.partial_inner_all_outer_crit_edge, !llvm.loop !54

safe_if_after_true:                               ; preds = %safe_if_run_true, %for_exit
  %"~test" = xor <8 x i32> %notequal_bestIdx_load__to_boolvec, splat (i32 -1)
  %39 = xor <8 x i1> %notequal_bestIdx_load_, splat (i1 true)
  %40 = bitcast <8 x i1> %39 to i8
  %cmp.i3023.not = icmp eq i8 %40, 0
  br i1 %cmp.i3023.not, label %if_done, label %for_test280.preheader

for_test280.preheader:                            ; preds = %safe_if_after_true
  %"oldMask&test2913496" = select <8 x i1> %less_i_load288_SortedLength_load_broadcast2893495, <8 x i32> %"~test", <8 x i32> zeroinitializer
  %41 = icmp slt <8 x i32> %"oldMask&test2913496", zeroinitializer
  %42 = bitcast <8 x i1> %41 to i8
  %cmp.i3024.not3497 = icmp eq i8 %42, 0
  br i1 %cmp.i3024.not3497, label %for_exit283, label %not_all_continued_or_breaked332.lr.ph

not_all_continued_or_breaked332.lr.ph:            ; preds = %for_test280.preheader
  %43 = bitcast <8 x i32> %bestIdx.1 to <8 x float>
  br label %not_all_continued_or_breaked332

safe_if_run_true:                                 ; preds = %for_exit
  %mul__bestIdx_load271 = shl nsw <8 x i32> %bestIdx.1, splat (i32 3)
  %44 = or disjoint <8 x i32> %mul__bestIdx_load271, splat (i32 4)
  %new_add2611 = sext <8 x i32> %44 to <8 x i64>
  %vecmask_1.i = shufflevector <8 x i32> %notequal_bestIdx_load__to_boolvec, <8 x i32> poison, <4 x i32> <i32 0, i32 1, i32 2, i32 3>
  %vecmask_2.i = shufflevector <8 x i32> %notequal_bestIdx_load__to_boolvec, <8 x i32> poison, <4 x i32> <i32 4, i32 5, i32 6, i32 7>
  %offsets_1.i = shufflevector <8 x i64> %new_add2611, <8 x i64> poison, <4 x i32> <i32 0, i32 1, i32 2, i32 3>
  %offsets_2.i = shufflevector <8 x i64> %new_add2611, <8 x i64> poison, <4 x i32> <i32 4, i32 5, i32 6, i32 7>
  %v1_1.i = tail call <4 x i32> @llvm.x86.avx2.gather.q.d.256(<4 x i32> undef, ptr %HashIndex_ptr, <4 x i64> %offsets_1.i, <4 x i32> %vecmask_1.i, i8 1)
  %v2_1.i = tail call <4 x i32> @llvm.x86.avx2.gather.q.d.256(<4 x i32> undef, ptr %HashIndex_ptr, <4 x i64> %offsets_2.i, <4 x i32> %vecmask_2.i, i8 1)
  %v.i = shufflevector <4 x i32> %v1_1.i, <4 x i32> %v2_1.i, <8 x i32> <i32 0, i32 1, i32 2, i32 3, i32 4, i32 5, i32 6, i32 7>
  %val.i = bitcast <8 x i32> %v.i to <8 x float>
  call void @llvm.masked.store.v8f32.p0(<8 x float> %val.i, ptr %ptr, i32 1, <8 x i1> %notequal_bestIdx_load_)
  br label %safe_if_after_true

for_test280.for_exit283_crit_edge:                ; preds = %not_all_continued_or_breaked332
  %45 = bitcast <8 x float> %blend.i3364 to <8 x i32>
  br label %for_exit283

for_exit283:                                      ; preds = %for_test280.for_exit283_crit_edge, %for_test280.preheader
  %bestIdx.5.lcssa = phi <8 x i32> [ %45, %for_test280.for_exit283_crit_edge ], [ %bestIdx.1, %for_test280.preheader ]
  %notequal_bestIdx_load366_ = icmp eq <8 x i32> %bestIdx.5.lcssa, splat (i32 -1)
  %"oldMask&test371" = select <8 x i1> %notequal_bestIdx_load366_, <8 x i32> zeroinitializer, <8 x i32> %"~test"
  %46 = icmp slt <8 x i32> %"oldMask&test371", zeroinitializer
  %47 = bitcast <8 x i1> %46 to i8
  %cmp.i3026.not = icmp eq i8 %47, 0
  br i1 %cmp.i3026.not, label %if_done, label %safe_if_run_true370

not_all_continued_or_breaked332:                  ; preds = %not_all_continued_or_breaked332.lr.ph, %not_all_continued_or_breaked332
  %indvars.iv = phi i64 [ 0, %not_all_continued_or_breaked332.lr.ph ], [ %indvars.iv.next, %not_all_continued_or_breaked332 ]
  %"oldMask&test2913503" = phi <8 x i32> [ %"oldMask&test2913496", %not_all_continued_or_breaked332.lr.ph ], [ %"oldMask&test291", %not_all_continued_or_breaked332 ]
  %i287.03502 = phi <8 x i32> [ zeroinitializer, %not_all_continued_or_breaked332.lr.ph ], [ %i_load362_plus1, %not_all_continued_or_breaked332 ]
  %bestIdx.53499 = phi <8 x float> [ %43, %not_all_continued_or_breaked332.lr.ph ], [ %blend.i3364, %not_all_continued_or_breaked332 ]
  %bestDistSq.53498 = phi <8 x float> [ %bestDistSq.1, %not_all_continued_or_breaked332.lr.ph ], [ %blend.i.i3360, %not_all_continued_or_breaked332 ]
  %48 = shl nsw i64 %indvars.iv, 3
  %ptr2624 = getelementptr i8, ptr %SortedPositions_ptr, i64 %48, !filename !11, !first_line !19, !first_column !20, !last_line !19, !last_column !21
  %SortedPositions_ptr_load296_offset_load26232625 = load float, ptr %ptr2624, align 4
  %SortedPositions_ptr_load296_offset_load26232626 = insertelement <8 x float> poison, float %SortedPositions_ptr_load296_offset_load26232625, i64 0
  %SortedPositions_ptr_load296_offset_load26232627 = shufflevector <8 x float> %SortedPositions_ptr_load296_offset_load26232626, <8 x float> poison, <8 x i32> zeroinitializer, !filename !11, !first_line !19, !first_column !20, !last_line !19, !last_column !21
  %sub_a14_x_b26_x.i.i3136 = fsub <8 x float> %SortedPositions_ptr_load296_offset_load26232627, %v_1.i
  %mul_a13_x_b25_x.i.i.i3145 = fmul <8 x float> %sub_a14_x_b26_x.i.i3136, %sub_a14_x_b26_x.i.i3136
  %gep = getelementptr i8, ptr %invariant.gep, i64 %48
  %SortedPositions_ptr_load296_offset_load30726332638 = load float, ptr %gep, align 4
  %SortedPositions_ptr_load296_offset_load30726332639 = insertelement <8 x float> poison, float %SortedPositions_ptr_load296_offset_load30726332638, i64 0
  %SortedPositions_ptr_load296_offset_load30726332640 = shufflevector <8 x float> %SortedPositions_ptr_load296_offset_load30726332639, <8 x float> poison, <8 x i32> zeroinitializer, !filename !11, !first_line !19, !first_column !20, !last_line !19, !last_column !21
  %sub_a19_y_b211_y.i.i3137 = fsub <8 x float> %SortedPositions_ptr_load296_offset_load30726332640, %v_1.i3336
  %mul_a17_y_b29_y.i.i.i3146 = fmul <8 x float> %sub_a19_y_b211_y.i.i3137, %sub_a19_y_b211_y.i.i3137
  %add_mul_a13_x_b25_x_mul_a17_y_b29_y.i.i.i3147 = fadd <8 x float> %mul_a13_x_b25_x.i.i.i3145, %mul_a17_y_b29_y.i.i.i3146
  %less_distSq_load342_bestDistSq_load343 = fcmp olt <8 x float> %add_mul_a13_x_b25_x_mul_a17_y_b29_y.i.i.i3147, %bestDistSq.53498
  %49 = bitcast <8 x i32> %"oldMask&test2913503" to <8 x float>
  %mask_as_float.i.i3358 = select <8 x i1> %less_distSq_load342_bestDistSq_load343, <8 x float> %49, <8 x float> zeroinitializer
  %blend.i.i3360 = call <8 x float> @llvm.x86.avx.blendv.ps.256(<8 x float> %bestDistSq.53498, <8 x float> %add_mul_a13_x_b25_x_mul_a17_y_b29_y.i.i.i3147, <8 x float> %mask_as_float.i.i3358)
  %newAsFloat.i3363 = bitcast <8 x i32> %i287.03502 to <8 x float>
  %blend.i3364 = call <8 x float> @llvm.x86.avx.blendv.ps.256(<8 x float> %bestIdx.53499, <8 x float> %newAsFloat.i3363, <8 x float> %mask_as_float.i.i3358)
  %indvars.iv.next = add nuw nsw i64 %indvars.iv, 1
  %i_load362_plus1 = add nuw nsw <8 x i32> %i287.03502, splat (i32 1)
  %less_i_load288_SortedLength_load_broadcast289 = icmp slt <8 x i32> %i_load362_plus1, %SortedLength_load_broadcast289
  %"oldMask&test291" = select <8 x i1> %less_i_load288_SortedLength_load_broadcast289, <8 x i32> %"oldMask&test2913503", <8 x i32> zeroinitializer
  %50 = icmp slt <8 x i32> %"oldMask&test291", zeroinitializer
  %51 = bitcast <8 x i1> %50 to i8
  %cmp.i3024.not = icmp eq i8 %51, 0
  br i1 %cmp.i3024.not, label %for_test280.for_exit283_crit_edge, label %not_all_continued_or_breaked332, !llvm.loop !59

safe_if_run_true370:                              ; preds = %for_exit283
  %mul__bestIdx_load376 = shl nsw <8 x i32> %bestIdx.5.lcssa, splat (i32 3)
  %52 = or disjoint <8 x i32> %mul__bestIdx_load376, splat (i32 4)
  %new_add2645 = sext <8 x i32> %52 to <8 x i64>
  %vecmask_1.i3365 = shufflevector <8 x i32> %"oldMask&test371", <8 x i32> poison, <4 x i32> <i32 0, i32 1, i32 2, i32 3>
  %vecmask_2.i3366 = shufflevector <8 x i32> %"oldMask&test371", <8 x i32> poison, <4 x i32> <i32 4, i32 5, i32 6, i32 7>
  %offsets_1.i3367 = shufflevector <8 x i64> %new_add2645, <8 x i64> poison, <4 x i32> <i32 0, i32 1, i32 2, i32 3>
  %offsets_2.i3368 = shufflevector <8 x i64> %new_add2645, <8 x i64> poison, <4 x i32> <i32 4, i32 5, i32 6, i32 7>
  %v1_1.i3369 = tail call <4 x i32> @llvm.x86.avx2.gather.q.d.256(<4 x i32> undef, ptr %HashIndex_ptr, <4 x i64> %offsets_1.i3367, <4 x i32> %vecmask_1.i3365, i8 1)
  %v2_1.i3370 = tail call <4 x i32> @llvm.x86.avx2.gather.q.d.256(<4 x i32> undef, ptr %HashIndex_ptr, <4 x i64> %offsets_2.i3368, <4 x i32> %vecmask_2.i3366, i8 1)
  %v.i3371 = shufflevector <4 x i32> %v1_1.i3369, <4 x i32> %v2_1.i3370, <8 x i32> <i32 0, i32 1, i32 2, i32 3, i32 4, i32 5, i32 6, i32 7>
  %val.i3372 = bitcast <8 x i32> %v.i3371 to <8 x float>
  call void @llvm.x86.avx.maskstore.ps.256(ptr nonnull %ptr, <8 x i32> %"oldMask&test371", <8 x float> %val.i3372)
  br label %if_done

partial_inner_only:                               ; preds = %partial_inner_all_outer
  %smear_counter_init409 = insertelement <8 x i32> poison, i32 %counter.1.lcssa, i64 0
  %smear_counter410 = shufflevector <8 x i32> %smear_counter_init409, <8 x i32> poison, <8 x i32> zeroinitializer
  %iter_val411 = add nsw <8 x i32> %smear_counter410, <i32 0, i32 1, i32 2, i32 3, i32 4, i32 5, i32 6, i32 7>
  %smear_end_init412 = insertelement <8 x i32> poison, i32 %ret.i.i, i64 0
  %smear_end413 = shufflevector <8 x i32> %smear_end_init412, <8 x i32> poison, <8 x i32> zeroinitializer
  %cmp414 = icmp slt <8 x i32> %iter_val411, %smear_end413
  %cmp414_to_boolvec = sext <8 x i1> %cmp414 to <8 x i32>
  %mul__index_load416.elt0 = shl nsw i32 %counter.1.lcssa, 2
  %53 = sext i32 %mul__index_load416.elt0 to i64
  %ptr2580 = getelementptr i8, ptr %Results_ptr, i64 %53
  call void @llvm.masked.store.v8f32.p0(<8 x float> splat (float 0xFFFFFFFFE0000000), ptr %ptr2580, i32 1, <8 x i1> %cmp414)
  %mul__index_load422 = shl nsw <8 x i32> %iter_val411, splat (i32 3)
  %mask.i3373 = bitcast <8 x i32> %cmp414_to_boolvec to <8 x float>
  %v_1.i3374 = tail call <8 x float> @llvm.x86.avx2.gather.d.ps.256(<8 x float> undef, ptr readonly %QueryPositions_ptr, <8 x i32> %mul__index_load422, <8 x float> %mask.i3373, i8 1)
  %54 = or disjoint <8 x i32> %mul__index_load422, splat (i32 4)
  %v_1.i3377 = tail call <8 x float> @llvm.x86.avx2.gather.d.ps.256(<8 x float> undef, ptr readonly %QueryPositions_ptr, <8 x i32> %54, <8 x float> %mask.i3373, i8 1)
  %get_element439_broadcast = insertelement <8 x float> poison, float %GridOrigin_ptr_load_load.unpack, i64 0
  %get_element439_broadcast440 = shufflevector <8 x float> %get_element439_broadcast, <8 x float> poison, <8 x i32> zeroinitializer
  %get_element442_broadcast = insertelement <8 x float> poison, float %GridOrigin_ptr_load_load.unpack2560, i64 0
  %get_element442_broadcast443 = shufflevector <8 x float> %get_element442_broadcast, <8 x float> poison, <8 x i32> zeroinitializer
  %sub_a14_x_b26_x.i3091 = fsub <8 x float> %v_1.i3374, %get_element439_broadcast440
  %sub_a19_y_b211_y.i3092 = fsub <8 x float> %v_1.i3377, %get_element442_broadcast443
  %GridResolutionInv_load447_broadcast = insertelement <8 x float> poison, float %GridResolutionInv_ptr_load_load, i64 0
  %GridResolutionInv_load447_broadcast448 = shufflevector <8 x float> %GridResolutionInv_load447_broadcast, <8 x float> poison, <8 x i32> zeroinitializer
  %mul_v14_x_s_load.i3098 = fmul <8 x float> %GridResolutionInv_load447_broadcast448, %sub_a14_x_b26_x.i3091
  %mul_v17_y_s_load9.i3099 = fmul <8 x float> %GridResolutionInv_load447_broadcast448, %sub_a19_y_b211_y.i3092
  %call.i.i.i3105 = tail call <8 x float> @llvm.x86.avx.round.ps.256(<8 x float> %mul_v14_x_s_load.i3098, i32 9)
  %call.i.i3.i3106 = tail call <8 x float> @llvm.x86.avx.round.ps.256(<8 x float> %mul_v17_y_s_load9.i3099, i32 9)
  %v12_x_to_int32.i3112 = fptosi <8 x float> %call.i.i.i3105 to <8 x i32>
  %v14_y_to_int32.i3113 = fptosi <8 x float> %call.i.i3.i3106 to <8 x i32>
  %get_element459_broadcast = insertelement <8 x i32> poison, i32 %GridDimensions_ptr_load_load.unpack, i64 0
  %get_element459_broadcast460 = shufflevector <8 x i32> %get_element459_broadcast, <8 x i32> poison, <8 x i32> zeroinitializer
  %get_element462_broadcast = insertelement <8 x i32> poison, i32 %GridDimensions_ptr_load_load.unpack2563, i64 0
  %get_element462_broadcast463 = shufflevector <8 x i32> %get_element462_broadcast, <8 x i32> poison, <8 x i32> zeroinitializer
  %sub_a14_x_b_load.i3123 = add nsw <8 x i32> %get_element459_broadcast460, splat (i32 -1)
  %sub_a17_y_b_load9.i3124 = add nsw <8 x i32> %get_element462_broadcast463, splat (i32 -1)
  %55 = call <8 x i32> @llvm.smax.v8i32(<8 x i32> %v12_x_to_int32.i3112, <8 x i32> zeroinitializer)
  %56 = call <8 x i32> @llvm.smax.v8i32(<8 x i32> %v14_y_to_int32.i3113, <8 x i32> zeroinitializer)
  %blend.i16.i.v = call <8 x i32> @llvm.smin.v8i32(<8 x i32> %55, <8 x i32> %sub_a14_x_b_load.i3123)
  %blend.i20.i.v = call <8 x i32> @llvm.smin.v8i32(<8 x i32> %56, <8 x i32> %sub_a17_y_b_load9.i3124)
  %57 = bitcast <8 x i1> %cmp414 to i8
  %cmp.i3013.not3524 = icmp eq i8 %57, 0
  br i1 %cmp.i3013.not3524, label %for_exit474, label %for_loop472

not_all_continued_or_breaked504:                  ; preds = %for_loop472
  %new_mask513 = xor <8 x i32> %"oldMask&test493", %"oldMask&test4813528"
  %58 = icmp slt <8 x i32> %new_mask513, zeroinitializer
  %59 = bitcast <8 x i1> %58 to i8
  %cmp.i3028.not3517 = icmp eq i8 %59, 0
  br i1 %cmp.i3028.not3517, label %for_step473, label %for_loop515

for_loop515:                                      ; preds = %not_all_continued_or_breaked504, %for_step516
  %60 = phi i8 [ %65, %for_step516 ], [ %59, %not_all_continued_or_breaked504 ]
  %"oldMask&test5243521" = phi <8 x i32> [ %"oldMask&test524", %for_step516 ], [ %new_mask513, %not_all_continued_or_breaked504 ]
  %dy521.03520 = phi <8 x i32> [ %dy_load700_plus1, %for_step516 ], [ splat (i32 -1), %not_all_continued_or_breaked504 ]
  %bestIdx470.23519 = phi <8 x i32> [ %bestIdx470.3, %for_step516 ], [ %bestIdx470.03526, %not_all_continued_or_breaked504 ]
  %bestDistSq469.23518 = phi <8 x float> [ %bestDistSq469.3, %for_step516 ], [ %bestDistSq469.03525, %not_all_continued_or_breaked504 ]
  %add_cell436528_y_dy_load530 = add nsw <8 x i32> %dy521.03520, %blend.i20.i.v
  %greaterequal_ny_load531_GridDimensions532_y_broadcast534.not = icmp ult <8 x i32> %add_cell436528_y_dy_load530, %get_element462_broadcast463
  %"oldMask&test536" = select <8 x i1> %greaterequal_ny_load531_GridDimensions532_y_broadcast534.not, <8 x i32> zeroinitializer, <8 x i32> %"oldMask&test5243521"
  %61 = icmp slt <8 x i32> %"oldMask&test536", zeroinitializer
  %62 = bitcast <8 x i1> %61 to i8
  %"equal_finished&func545_load_mask526" = icmp eq i8 %60, %62
  br i1 %"equal_finished&func545_load_mask526", label %for_step516, label %not_all_continued_or_breaked547

for_test610.for_step516.loopexit_crit_edge:       ; preds = %for_loop611
  %63 = bitcast <8 x float> %blend.i3395 to <8 x i32>
  br label %for_step516

for_step516:                                      ; preds = %not_all_continued_or_breaked600, %for_test610.for_step516.loopexit_crit_edge, %not_all_continued_or_breaked547, %for_loop515
  %bestDistSq469.3 = phi <8 x float> [ %bestDistSq469.23518, %for_loop515 ], [ %bestDistSq469.23518, %not_all_continued_or_breaked547 ], [ %blend.i.i3391, %for_test610.for_step516.loopexit_crit_edge ], [ %bestDistSq469.23518, %not_all_continued_or_breaked600 ]
  %bestIdx470.3 = phi <8 x i32> [ %bestIdx470.23519, %for_loop515 ], [ %bestIdx470.23519, %not_all_continued_or_breaked547 ], [ %63, %for_test610.for_step516.loopexit_crit_edge ], [ %bestIdx470.23519, %not_all_continued_or_breaked600 ]
  %continue_lanes_memory519.1 = phi <8 x i32> [ %"oldMask&test536", %for_loop515 ], [ %"mask|continueMask591", %not_all_continued_or_breaked547 ], [ %"mask|continueMask591", %for_test610.for_step516.loopexit_crit_edge ], [ %"mask|continueMask591", %not_all_continued_or_breaked600 ]
  %internal_mask_memory.12 = phi <8 x i32> [ zeroinitializer, %for_loop515 ], [ zeroinitializer, %not_all_continued_or_breaked547 ], [ %new_mask609, %for_test610.for_step516.loopexit_crit_edge ], [ %new_mask609, %not_all_continued_or_breaked600 ]
  %"mask|continue_mask699" = or <8 x i32> %internal_mask_memory.12, %continue_lanes_memory519.1
  %dy_load700_plus1 = add nsw <8 x i32> %dy521.03520, splat (i32 1)
  %lessequal_dy_load522_.inv = icmp sgt <8 x i32> %dy521.03520, zeroinitializer
  %"oldMask&test524" = select <8 x i1> %lessequal_dy_load522_.inv, <8 x i32> zeroinitializer, <8 x i32> %"mask|continue_mask699"
  %64 = icmp slt <8 x i32> %"oldMask&test524", zeroinitializer
  %65 = bitcast <8 x i1> %64 to i8
  %cmp.i3028.not = icmp eq i8 %65, 0
  br i1 %cmp.i3028.not, label %for_step473, label %for_loop515, !llvm.loop !60

not_all_continued_or_breaked547:                  ; preds = %for_loop515
  %new_mask556 = xor <8 x i32> %"oldMask&test536", %"oldMask&test5243521"
  %mul_ny_load558_GridDimensions559_x_broadcast561 = mul nsw <8 x i32> %add_cell436528_y_dy_load530, %get_element459_broadcast460
  %add_mul_ny_load558_GridDimensions559_x_broadcast561_nx_load562 = add nsw <8 x i32> %mul_ny_load558_GridDimensions559_x_broadcast561, %add_cell436485_x_dx_load487
  %CellStartEnd_load565566__data = load ptr, ptr %CellStartEnd, align 8
  %mul__cellHash_load564 = shl nsw <8 x i32> %add_mul_ny_load558_GridDimensions559_x_broadcast561_nx_load562, splat (i32 3)
  %v_1.i3379 = tail call <8 x i32> @llvm.x86.avx2.gather.d.d.256(<8 x i32> undef, ptr readonly %CellStartEnd_load565566__data, <8 x i32> %mul__cellHash_load564, <8 x i32> %new_mask556, i8 1)
  %66 = or disjoint <8 x i32> %mul__cellHash_load564, splat (i32 4)
  %v_1.i3381 = tail call <8 x i32> @llvm.x86.avx2.gather.d.d.256(<8 x i32> undef, ptr readonly %CellStartEnd_load565566__data, <8 x i32> %66, <8 x i32> %new_mask556, i8 1)
  %isneg2566 = icmp slt <8 x i32> %v_1.i3379, zeroinitializer
  %"oldMask&test588" = select <8 x i1> %isneg2566, <8 x i32> %new_mask556, <8 x i32> zeroinitializer
  %"mask|continueMask591" = or <8 x i32> %"oldMask&test588", %"oldMask&test536"
  %67 = icmp slt <8 x i32> %"mask|continueMask591", zeroinitializer
  %68 = bitcast <8 x i1> %67 to i8
  %"equal_finished&func597_load_mask526" = icmp eq i8 %60, %68
  br i1 %"equal_finished&func597_load_mask526", label %for_step516, label %not_all_continued_or_breaked600

not_all_continued_or_breaked600:                  ; preds = %not_all_continued_or_breaked547
  %new_mask609 = xor <8 x i32> %"mask|continueMask591", %"oldMask&test5243521"
  %less_i_load619_end_load6203508 = icmp slt <8 x i32> %v_1.i3379, %v_1.i3381
  %"oldMask&test6223509" = select <8 x i1> %less_i_load619_end_load6203508, <8 x i32> %new_mask609, <8 x i32> zeroinitializer
  %69 = icmp slt <8 x i32> %"oldMask&test6223509", zeroinitializer
  %70 = bitcast <8 x i1> %69 to i8
  %cmp.i3032.not3510 = icmp eq i8 %70, 0
  br i1 %cmp.i3032.not3510, label %for_step516, label %for_loop611.lr.ph

for_loop611.lr.ph:                                ; preds = %not_all_continued_or_breaked600
  %71 = bitcast <8 x i32> %bestIdx470.23519 to <8 x float>
  br label %for_loop611

for_loop611:                                      ; preds = %for_loop611.lr.ph, %for_loop611
  %"oldMask&test6223514" = phi <8 x i32> [ %"oldMask&test6223509", %for_loop611.lr.ph ], [ %"oldMask&test622", %for_loop611 ]
  %i617.03513 = phi <8 x i32> [ %v_1.i3379, %for_loop611.lr.ph ], [ %i_load693_plus1, %for_loop611 ]
  %bestIdx470.43512 = phi <8 x float> [ %71, %for_loop611.lr.ph ], [ %blend.i3395, %for_loop611 ]
  %bestDistSq469.43511 = phi <8 x float> [ %bestDistSq469.23518, %for_loop611.lr.ph ], [ %blend.i.i3391, %for_loop611 ]
  %mul__i_load626 = shl nsw <8 x i32> %i617.03513, splat (i32 3)
  %mask.i3383 = bitcast <8 x i32> %"oldMask&test6223514" to <8 x float>
  %v_1.i3384 = tail call <8 x float> @llvm.x86.avx2.gather.d.ps.256(<8 x float> undef, ptr readonly %SortedPositions_ptr, <8 x i32> %mul__i_load626, <8 x float> %mask.i3383, i8 1)
  %72 = or disjoint <8 x i32> %mul__i_load626, splat (i32 4)
  %v_1.i3387 = tail call <8 x float> @llvm.x86.avx2.gather.d.ps.256(<8 x float> undef, ptr readonly %SortedPositions_ptr, <8 x i32> %72, <8 x float> %mask.i3383, i8 1)
  %sub_a14_x_b26_x.i.i3148 = fsub <8 x float> %v_1.i3384, %v_1.i3374
  %mul_a13_x_b25_x.i.i.i3157 = fmul <8 x float> %sub_a14_x_b26_x.i.i3148, %sub_a14_x_b26_x.i.i3148
  %sub_a19_y_b211_y.i.i3149 = fsub <8 x float> %v_1.i3387, %v_1.i3377
  %mul_a17_y_b29_y.i.i.i3158 = fmul <8 x float> %sub_a19_y_b211_y.i.i3149, %sub_a19_y_b211_y.i.i3149
  %add_mul_a13_x_b25_x_mul_a17_y_b29_y.i.i.i3159 = fadd <8 x float> %mul_a13_x_b25_x.i.i.i3157, %mul_a17_y_b29_y.i.i.i3158
  %less_distSq_load673_bestDistSq_load674 = fcmp olt <8 x float> %add_mul_a13_x_b25_x_mul_a17_y_b29_y.i.i.i3159, %bestDistSq469.43511
  %73 = bitcast <8 x i32> %"oldMask&test6223514" to <8 x float>
  %mask_as_float.i.i3389 = select <8 x i1> %less_distSq_load673_bestDistSq_load674, <8 x float> %73, <8 x float> zeroinitializer
  %blend.i.i3391 = call <8 x float> @llvm.x86.avx.blendv.ps.256(<8 x float> %bestDistSq469.43511, <8 x float> %add_mul_a13_x_b25_x_mul_a17_y_b29_y.i.i.i3159, <8 x float> %mask_as_float.i.i3389)
  %newAsFloat.i3394 = bitcast <8 x i32> %i617.03513 to <8 x float>
  %blend.i3395 = call <8 x float> @llvm.x86.avx.blendv.ps.256(<8 x float> %bestIdx470.43512, <8 x float> %newAsFloat.i3394, <8 x float> %mask_as_float.i.i3389)
  %i_load693_plus1 = add nsw <8 x i32> %i617.03513, splat (i32 1)
  %less_i_load619_end_load620 = icmp slt <8 x i32> %i_load693_plus1, %v_1.i3381
  %"oldMask&test622" = select <8 x i1> %less_i_load619_end_load620, <8 x i32> %"oldMask&test6223514", <8 x i32> zeroinitializer
  %74 = icmp slt <8 x i32> %"oldMask&test622", zeroinitializer
  %75 = bitcast <8 x i1> %74 to i8
  %cmp.i3032.not = icmp eq i8 %75, 0
  br i1 %cmp.i3032.not, label %for_test610.for_step516.loopexit_crit_edge, label %for_loop611, !llvm.loop !61

safe_if_after_true714:                            ; preds = %safe_if_run_true715, %for_exit474
  %"oldMask&~test732" = select <8 x i1> %notequal_bestIdx_load711_, <8 x i32> zeroinitializer, <8 x i32> %cmp414_to_boolvec
  %not.notequal_bestIdx_load711_ = xor <8 x i1> %notequal_bestIdx_load711_, splat (i1 true)
  %76 = select <8 x i1> %not.notequal_bestIdx_load711_, <8 x i1> %cmp414, <8 x i1> zeroinitializer
  %77 = bitcast <8 x i1> %76 to i8
  %cmp.i3034.not = icmp eq i8 %77, 0
  br i1 %cmp.i3034.not, label %common.ret, label %for_test734.preheader

for_test734.preheader:                            ; preds = %safe_if_after_true714
  %SortedLength_load743_broadcast = insertelement <8 x i32> poison, i32 %SortedLength_ptr_load_load, i64 0
  %SortedLength_load743_broadcast744 = shufflevector <8 x i32> %SortedLength_load743_broadcast, <8 x i32> poison, <8 x i32> zeroinitializer
  %less_i_load742_SortedLength_load743_broadcast7443531 = icmp sgt <8 x i32> %SortedLength_load743_broadcast744, zeroinitializer
  %"oldMask&test7463532" = select <8 x i1> %less_i_load742_SortedLength_load743_broadcast7443531, <8 x i32> %"oldMask&~test732", <8 x i32> zeroinitializer
  %78 = icmp slt <8 x i32> %"oldMask&test7463532", zeroinitializer
  %79 = bitcast <8 x i1> %78 to i8
  %cmp.i3035.not3533 = icmp eq i8 %79, 0
  br i1 %cmp.i3035.not3533, label %for_exit737, label %not_all_continued_or_breaked787.lr.ph

not_all_continued_or_breaked787.lr.ph:            ; preds = %for_test734.preheader
  %invariant.gep3541 = getelementptr i8, ptr %SortedPositions_ptr, i64 4
  %80 = bitcast <8 x i32> %bestIdx470.0.lcssa to <8 x float>
  br label %not_all_continued_or_breaked787

safe_if_run_true715:                              ; preds = %for_exit474
  %"oldMask&test716" = select <8 x i1> %notequal_bestIdx_load711_, <8 x i32> %cmp414_to_boolvec, <8 x i32> zeroinitializer
  %mul__bestIdx_load721 = shl nsw <8 x i32> %bestIdx470.0.lcssa, splat (i32 3)
  %81 = or disjoint <8 x i32> %mul__bestIdx_load721, splat (i32 4)
  %new_add2677 = sext <8 x i32> %81 to <8 x i64>
  %vecmask_1.i3396 = shufflevector <8 x i32> %"oldMask&test716", <8 x i32> poison, <4 x i32> <i32 0, i32 1, i32 2, i32 3>
  %vecmask_2.i3397 = shufflevector <8 x i32> %"oldMask&test716", <8 x i32> poison, <4 x i32> <i32 4, i32 5, i32 6, i32 7>
  %offsets_1.i3398 = shufflevector <8 x i64> %new_add2677, <8 x i64> poison, <4 x i32> <i32 0, i32 1, i32 2, i32 3>
  %offsets_2.i3399 = shufflevector <8 x i64> %new_add2677, <8 x i64> poison, <4 x i32> <i32 4, i32 5, i32 6, i32 7>
  %v1_1.i3400 = tail call <4 x i32> @llvm.x86.avx2.gather.q.d.256(<4 x i32> undef, ptr %HashIndex_ptr, <4 x i64> %offsets_1.i3398, <4 x i32> %vecmask_1.i3396, i8 1)
  %v2_1.i3401 = tail call <4 x i32> @llvm.x86.avx2.gather.q.d.256(<4 x i32> undef, ptr %HashIndex_ptr, <4 x i64> %offsets_2.i3399, <4 x i32> %vecmask_2.i3397, i8 1)
  %v.i3402 = shufflevector <4 x i32> %v1_1.i3400, <4 x i32> %v2_1.i3401, <8 x i32> <i32 0, i32 1, i32 2, i32 3, i32 4, i32 5, i32 6, i32 7>
  %val.i3403 = bitcast <8 x i32> %v.i3402 to <8 x float>
  call void @llvm.x86.avx.maskstore.ps.256(ptr %ptr2580, <8 x i32> %"oldMask&test716", <8 x float> %val.i3403)
  br label %safe_if_after_true714

for_test734.for_exit737_crit_edge:                ; preds = %not_all_continued_or_breaked787
  %82 = bitcast <8 x float> %blend.i3410 to <8 x i32>
  br label %for_exit737

for_exit737:                                      ; preds = %for_test734.for_exit737_crit_edge, %for_test734.preheader
  %bestIdx470.5.lcssa = phi <8 x i32> [ %82, %for_test734.for_exit737_crit_edge ], [ %bestIdx470.0.lcssa, %for_test734.preheader ]
  %notequal_bestIdx_load821_ = icmp eq <8 x i32> %bestIdx470.5.lcssa, splat (i32 -1)
  %"oldMask&test826" = select <8 x i1> %notequal_bestIdx_load821_, <8 x i32> zeroinitializer, <8 x i32> %"oldMask&~test732"
  %83 = icmp slt <8 x i32> %"oldMask&test826", zeroinitializer
  %84 = bitcast <8 x i1> %83 to i8
  %cmp.i3037.not = icmp eq i8 %84, 0
  br i1 %cmp.i3037.not, label %common.ret, label %safe_if_run_true825

not_all_continued_or_breaked787:                  ; preds = %not_all_continued_or_breaked787.lr.ph, %not_all_continued_or_breaked787
  %indvars.iv3552 = phi i64 [ 0, %not_all_continued_or_breaked787.lr.ph ], [ %indvars.iv.next3553, %not_all_continued_or_breaked787 ]
  %"oldMask&test7463539" = phi <8 x i32> [ %"oldMask&test7463532", %not_all_continued_or_breaked787.lr.ph ], [ %"oldMask&test746", %not_all_continued_or_breaked787 ]
  %i741.03538 = phi <8 x i32> [ zeroinitializer, %not_all_continued_or_breaked787.lr.ph ], [ %i_load817_plus1, %not_all_continued_or_breaked787 ]
  %bestIdx470.53535 = phi <8 x float> [ %80, %not_all_continued_or_breaked787.lr.ph ], [ %blend.i3410, %not_all_continued_or_breaked787 ]
  %bestDistSq469.53534 = phi <8 x float> [ %bestDistSq469.0.lcssa, %not_all_continued_or_breaked787.lr.ph ], [ %blend.i.i3406, %not_all_continued_or_breaked787 ]
  %85 = shl nsw i64 %indvars.iv3552, 3
  %ptr2694 = getelementptr i8, ptr %SortedPositions_ptr, i64 %85
  %SortedPositions_ptr_load751_offset_load26932695 = load float, ptr %ptr2694, align 4
  %SortedPositions_ptr_load751_offset_load26932696 = insertelement <8 x float> poison, float %SortedPositions_ptr_load751_offset_load26932695, i64 0
  %SortedPositions_ptr_load751_offset_load26932697 = shufflevector <8 x float> %SortedPositions_ptr_load751_offset_load26932696, <8 x float> poison, <8 x i32> zeroinitializer
  %sub_a14_x_b26_x.i.i3160 = fsub <8 x float> %SortedPositions_ptr_load751_offset_load26932697, %v_1.i3374
  %mul_a13_x_b25_x.i.i.i3169 = fmul <8 x float> %sub_a14_x_b26_x.i.i3160, %sub_a14_x_b26_x.i.i3160
  %gep3542 = getelementptr i8, ptr %invariant.gep3541, i64 %85
  %SortedPositions_ptr_load751_offset_load76227032708 = load float, ptr %gep3542, align 4
  %SortedPositions_ptr_load751_offset_load76227032709 = insertelement <8 x float> poison, float %SortedPositions_ptr_load751_offset_load76227032708, i64 0
  %SortedPositions_ptr_load751_offset_load76227032710 = shufflevector <8 x float> %SortedPositions_ptr_load751_offset_load76227032709, <8 x float> poison, <8 x i32> zeroinitializer
  %sub_a19_y_b211_y.i.i3161 = fsub <8 x float> %SortedPositions_ptr_load751_offset_load76227032710, %v_1.i3377
  %mul_a17_y_b29_y.i.i.i3170 = fmul <8 x float> %sub_a19_y_b211_y.i.i3161, %sub_a19_y_b211_y.i.i3161
  %add_mul_a13_x_b25_x_mul_a17_y_b29_y.i.i.i3171 = fadd <8 x float> %mul_a13_x_b25_x.i.i.i3169, %mul_a17_y_b29_y.i.i.i3170
  %less_distSq_load797_bestDistSq_load798 = fcmp olt <8 x float> %add_mul_a13_x_b25_x_mul_a17_y_b29_y.i.i.i3171, %bestDistSq469.53534
  %86 = bitcast <8 x i32> %"oldMask&test7463539" to <8 x float>
  %mask_as_float.i.i3404 = select <8 x i1> %less_distSq_load797_bestDistSq_load798, <8 x float> %86, <8 x float> zeroinitializer
  %blend.i.i3406 = call <8 x float> @llvm.x86.avx.blendv.ps.256(<8 x float> %bestDistSq469.53534, <8 x float> %add_mul_a13_x_b25_x_mul_a17_y_b29_y.i.i.i3171, <8 x float> %mask_as_float.i.i3404)
  %newAsFloat.i3409 = bitcast <8 x i32> %i741.03538 to <8 x float>
  %blend.i3410 = call <8 x float> @llvm.x86.avx.blendv.ps.256(<8 x float> %bestIdx470.53535, <8 x float> %newAsFloat.i3409, <8 x float> %mask_as_float.i.i3404)
  %indvars.iv.next3553 = add nuw nsw i64 %indvars.iv3552, 1
  %i_load817_plus1 = add nuw nsw <8 x i32> %i741.03538, splat (i32 1)
  %less_i_load742_SortedLength_load743_broadcast744 = icmp slt <8 x i32> %i_load817_plus1, %SortedLength_load743_broadcast744
  %"oldMask&test746" = select <8 x i1> %less_i_load742_SortedLength_load743_broadcast744, <8 x i32> %"oldMask&test7463539", <8 x i32> zeroinitializer
  %87 = icmp slt <8 x i32> %"oldMask&test746", zeroinitializer
  %88 = bitcast <8 x i1> %87 to i8
  %cmp.i3035.not = icmp eq i8 %88, 0
  br i1 %cmp.i3035.not, label %for_test734.for_exit737_crit_edge, label %not_all_continued_or_breaked787, !llvm.loop !62

safe_if_run_true825:                              ; preds = %for_exit737
  %mul__bestIdx_load831 = shl nsw <8 x i32> %bestIdx470.5.lcssa, splat (i32 3)
  %89 = or disjoint <8 x i32> %mul__bestIdx_load831, splat (i32 4)
  %new_add2715 = sext <8 x i32> %89 to <8 x i64>
  %vecmask_1.i3411 = shufflevector <8 x i32> %"oldMask&test826", <8 x i32> poison, <4 x i32> <i32 0, i32 1, i32 2, i32 3>
  %vecmask_2.i3412 = shufflevector <8 x i32> %"oldMask&test826", <8 x i32> poison, <4 x i32> <i32 4, i32 5, i32 6, i32 7>
  %offsets_1.i3413 = shufflevector <8 x i64> %new_add2715, <8 x i64> poison, <4 x i32> <i32 0, i32 1, i32 2, i32 3>
  %offsets_2.i3414 = shufflevector <8 x i64> %new_add2715, <8 x i64> poison, <4 x i32> <i32 4, i32 5, i32 6, i32 7>
  %v1_1.i3415 = tail call <4 x i32> @llvm.x86.avx2.gather.q.d.256(<4 x i32> undef, ptr %HashIndex_ptr, <4 x i64> %offsets_1.i3413, <4 x i32> %vecmask_1.i3411, i8 1)
  %v2_1.i3416 = tail call <4 x i32> @llvm.x86.avx2.gather.q.d.256(<4 x i32> undef, ptr %HashIndex_ptr, <4 x i64> %offsets_2.i3414, <4 x i32> %vecmask_2.i3412, i8 1)
  %v.i3417 = shufflevector <4 x i32> %v1_1.i3415, <4 x i32> %v2_1.i3416, <8 x i32> <i32 0, i32 1, i32 2, i32 3, i32 4, i32 5, i32 6, i32 7>
  %val.i3418 = bitcast <8 x i32> %v.i3417 to <8 x float>
  call void @llvm.x86.avx.maskstore.ps.256(ptr %ptr2580, <8 x i32> %"oldMask&test826", <8 x float> %val.i3418)
  br label %common.ret
}

; Function Attrs: nounwind uwtable
define void @SharpNative_Job__global_namespace__GridSearch2D_ClosestPointJobPointer_Execute_Batch_true_impl(i32 %__startIndex, i32 %__count, ptr noalias readonly captures(none) %GridOrigin_ptr, ptr noalias readonly captures(none) %GridResolutionInv_ptr, ptr noalias readonly captures(none) %GridDimensions_ptr, ptr noalias %QueryPositions_ptr, i32 %QueryPositions_length, ptr noalias %SortedPositions_ptr, i32 %SortedPositions_length, ptr noalias %HashIndex_ptr, i32 %HashIndex_length, ptr noalias readonly captures(none) %CellStartEnd, ptr noalias readonly captures(none) %SortedLength_ptr, ptr noalias readnone captures(none) %IgnoreSelf_ptr, ptr noalias readonly captures(none) %SquaredEpsilonSelf_ptr, ptr noalias captures(none) %Results_ptr, i32 %Results_length) local_unnamed_addr #0 {
allocas:
  %GridOrigin_ptr_load_load.unpack = load float, ptr %GridOrigin_ptr, align 4
  %GridOrigin_ptr_load_load.elt2559 = getelementptr inbounds nuw i8, ptr %GridOrigin_ptr, i64 4
  %GridOrigin_ptr_load_load.unpack2560 = load float, ptr %GridOrigin_ptr_load_load.elt2559, align 4
  %GridResolutionInv_ptr_load_load = load float, ptr %GridResolutionInv_ptr, align 4
  %GridDimensions_ptr_load_load.unpack = load i32, ptr %GridDimensions_ptr, align 4
  %GridDimensions_ptr_load_load.elt2562 = getelementptr inbounds nuw i8, ptr %GridDimensions_ptr, i64 4
  %GridDimensions_ptr_load_load.unpack2563 = load i32, ptr %GridDimensions_ptr_load_load.elt2562, align 4
  %SortedLength_ptr_load_load = load i32, ptr %SortedLength_ptr, align 4
  %SquaredEpsilonSelf_ptr_load_load = load float, ptr %SquaredEpsilonSelf_ptr, align 4
  %add___startIndex_load24___count_load = add nsw i32 %__count, %__startIndex
  %ret.i.i = tail call i32 @llvm.smin.i32(i32 %QueryPositions_length, i32 %add___startIndex_load24___count_load)
  %nitems = sub nsw i32 %ret.i.i, %__startIndex
  %nextras = srem i32 %nitems, 8
  %aligned_end = sub nsw i32 %ret.i.i, %nextras
  %before_aligned_end303517 = icmp slt i32 %__startIndex, %aligned_end
  br i1 %before_aligned_end303517, label %foreach_full_body.lr.ph, label %partial_inner_all_outer, !llvm.loop !63

foreach_full_body.lr.ph:                          ; preds = %allocas
  %get_element_broadcast = insertelement <8 x float> poison, float %GridOrigin_ptr_load_load.unpack, i64 0
  %get_element_broadcast49 = shufflevector <8 x float> %get_element_broadcast, <8 x float> poison, <8 x i32> zeroinitializer
  %get_element50_broadcast = insertelement <8 x float> poison, float %GridOrigin_ptr_load_load.unpack2560, i64 0
  %get_element50_broadcast51 = shufflevector <8 x float> %get_element50_broadcast, <8 x float> poison, <8 x i32> zeroinitializer
  %GridResolutionInv_load_broadcast = insertelement <8 x float> poison, float %GridResolutionInv_ptr_load_load, i64 0
  %GridResolutionInv_load_broadcast55 = shufflevector <8 x float> %GridResolutionInv_load_broadcast, <8 x float> poison, <8 x i32> zeroinitializer
  %get_element64_broadcast = insertelement <8 x i32> poison, i32 %GridDimensions_ptr_load_load.unpack, i64 0
  %get_element64_broadcast65 = shufflevector <8 x i32> %get_element64_broadcast, <8 x i32> poison, <8 x i32> zeroinitializer
  %get_element67_broadcast = insertelement <8 x i32> poison, i32 %GridDimensions_ptr_load_load.unpack2563, i64 0
  %get_element67_broadcast68 = shufflevector <8 x i32> %get_element67_broadcast, <8 x i32> poison, <8 x i32> zeroinitializer
  %sub_a14_x_b_load.i = add nsw <8 x i32> %get_element64_broadcast65, splat (i32 -1)
  %sub_a17_y_b_load9.i = add nsw <8 x i32> %get_element67_broadcast68, splat (i32 -1)
  %SquaredEpsilonSelf_load_broadcast = insertelement <8 x float> poison, float %SquaredEpsilonSelf_ptr_load_load, i64 0
  %SquaredEpsilonSelf_load_broadcast206 = shufflevector <8 x float> %SquaredEpsilonSelf_load_broadcast, <8 x float> poison, <8 x i32> zeroinitializer
  %SortedLength_load_broadcast = insertelement <8 x i32> poison, i32 %SortedLength_ptr_load_load, i64 0
  %SortedLength_load_broadcast289 = shufflevector <8 x i32> %SortedLength_load_broadcast, <8 x i32> poison, <8 x i32> zeroinitializer
  %less_i_load288_SortedLength_load_broadcast2893507 = icmp sgt <8 x i32> %SortedLength_load_broadcast289, zeroinitializer
  %invariant.gep = getelementptr i8, ptr %SortedPositions_ptr, i64 4
  %0 = sext i32 %__startIndex to i64
  %1 = sext i32 %aligned_end to i64
  br label %foreach_full_body, !llvm.loop !63

foreach_full_body:                                ; preds = %foreach_full_body.lr.ph, %if_done
  %indvars.iv3557 = phi i64 [ %0, %foreach_full_body.lr.ph ], [ %indvars.iv.next3558, %if_done ]
  %2 = trunc nsw i64 %indvars.iv3557 to i32
  %smear_counter_init32 = insertelement <8 x i32> poison, i32 %2, i64 0
  %smear_counter33 = shufflevector <8 x i32> %smear_counter_init32, <8 x i32> poison, <8 x i32> zeroinitializer
  %iter_val34 = add nsw <8 x i32> %smear_counter33, <i32 0, i32 1, i32 2, i32 3, i32 4, i32 5, i32 6, i32 7>
  %3 = shl nsw i64 %indvars.iv3557, 2
  %ptr = getelementptr i8, ptr %Results_ptr, i64 %3
  store <8 x i32> splat (i32 -1), ptr %ptr, align 4, !filename !11, !first_line !36, !first_column !13, !last_line !36, !last_column !14
  %mul__index_load39 = shl nsw <8 x i32> %iter_val34, splat (i32 3)
  %v_1.i = tail call <8 x float> @llvm.x86.avx2.gather.d.ps.256(<8 x float> undef, ptr readonly %QueryPositions_ptr, <8 x i32> %mul__index_load39, <8 x float> splat (float 0xFFFFFFFFE0000000), i8 1)
  %4 = or disjoint <8 x i32> %mul__index_load39, splat (i32 4)
  %v_1.i3348 = tail call <8 x float> @llvm.x86.avx2.gather.d.ps.256(<8 x float> undef, ptr readonly %QueryPositions_ptr, <8 x i32> %4, <8 x float> splat (float 0xFFFFFFFFE0000000), i8 1)
  %sub_a14_x_b26_x.i = fsub <8 x float> %v_1.i, %get_element_broadcast49
  %sub_a19_y_b211_y.i = fsub <8 x float> %v_1.i3348, %get_element50_broadcast51
  %mul_v14_x_s_load.i = fmul <8 x float> %GridResolutionInv_load_broadcast55, %sub_a14_x_b26_x.i
  %mul_v17_y_s_load9.i = fmul <8 x float> %GridResolutionInv_load_broadcast55, %sub_a19_y_b211_y.i
  %call.i.i.i = tail call <8 x float> @llvm.x86.avx.round.ps.256(<8 x float> %mul_v14_x_s_load.i, i32 9)
  %call.i.i3.i = tail call <8 x float> @llvm.x86.avx.round.ps.256(<8 x float> %mul_v17_y_s_load9.i, i32 9)
  %v12_x_to_int32.i = fptosi <8 x float> %call.i.i.i to <8 x i32>
  %v14_y_to_int32.i = fptosi <8 x float> %call.i.i3.i to <8 x i32>
  %5 = call <8 x i32> @llvm.smax.v8i32(<8 x i32> %v12_x_to_int32.i, <8 x i32> zeroinitializer)
  %6 = call <8 x i32> @llvm.smax.v8i32(<8 x i32> %v14_y_to_int32.i, <8 x i32> zeroinitializer)
  %blend.i3356.v = call <8 x i32> @llvm.smin.v8i32(<8 x i32> %5, <8 x i32> %sub_a14_x_b_load.i)
  %blend.i3360.v = call <8 x i32> @llvm.smin.v8i32(<8 x i32> %6, <8 x i32> %sub_a17_y_b_load9.i)
  br label %for_loop

for_loop:                                         ; preds = %foreach_full_body, %for_step
  %7 = phi i8 [ -1, %foreach_full_body ], [ %11, %for_step ]
  %"oldMask&test3506" = phi <8 x i32> [ splat (i32 -1), %foreach_full_body ], [ %"oldMask&test", %for_step ]
  %dx.03505 = phi <8 x i32> [ splat (i32 -1), %foreach_full_body ], [ %dx_load261_plus1, %for_step ]
  %bestIdx.03504 = phi <8 x i32> [ splat (i32 -1), %foreach_full_body ], [ %bestIdx.1, %for_step ]
  %bestDistSq.03503 = phi <8 x float> [ splat (float 0x47EFFFFFE0000000), %foreach_full_body ], [ %bestDistSq.1, %for_step ]
  %add_cell78_x_dx_load80 = add nsw <8 x i32> %dx.03505, %blend.i3356.v
  %greaterequal_nx_load_GridDimensions81_x_broadcast83.not = icmp ult <8 x i32> %add_cell78_x_dx_load80, %get_element64_broadcast65
  %"oldMask&test85" = select <8 x i1> %greaterequal_nx_load_GridDimensions81_x_broadcast83.not, <8 x i32> zeroinitializer, <8 x i32> %"oldMask&test3506"
  %8 = icmp slt <8 x i32> %"oldMask&test85", zeroinitializer
  %9 = bitcast <8 x i1> %8 to i8
  %"equal_finished&func_load_mask77" = icmp eq i8 %7, %9
  br i1 %"equal_finished&func_load_mask77", label %for_step, label %not_all_continued_or_breaked

for_step:                                         ; preds = %not_all_continued_or_breaked, %for_step94, %for_loop
  %bestDistSq.1 = phi <8 x float> [ %bestDistSq.03503, %for_loop ], [ %bestDistSq.03503, %not_all_continued_or_breaked ], [ %bestDistSq.3, %for_step94 ]
  %bestIdx.1 = phi <8 x i32> [ %bestIdx.03504, %for_loop ], [ %bestIdx.03504, %not_all_continued_or_breaked ], [ %bestIdx.3, %for_step94 ]
  %internal_mask_memory.2 = phi <8 x i32> [ zeroinitializer, %for_loop ], [ %new_mask91, %not_all_continued_or_breaked ], [ %new_mask91, %for_step94 ]
  %"mask|continue_mask260" = or <8 x i32> %internal_mask_memory.2, %"oldMask&test85"
  %dx_load261_plus1 = add nsw <8 x i32> %dx.03505, splat (i32 1)
  %lessequal_dx_load_.inv = icmp sgt <8 x i32> %dx.03505, zeroinitializer
  %"oldMask&test" = select <8 x i1> %lessequal_dx_load_.inv, <8 x i32> zeroinitializer, <8 x i32> %"mask|continue_mask260"
  %10 = icmp slt <8 x i32> %"oldMask&test", zeroinitializer
  %11 = bitcast <8 x i1> %10 to i8
  %cmp.i.not = icmp eq i8 %11, 0
  br i1 %cmp.i.not, label %for_exit, label %for_loop, !llvm.loop !64

for_exit:                                         ; preds = %for_step
  %notequal_bestIdx_load_ = icmp ne <8 x i32> %bestIdx.1, splat (i32 -1)
  %notequal_bestIdx_load__to_boolvec = sext <8 x i1> %notequal_bestIdx_load_ to <8 x i32>
  %12 = bitcast <8 x i1> %notequal_bestIdx_load_ to i8
  %cmp.i3012.not = icmp eq i8 %12, 0
  br i1 %cmp.i3012.not, label %safe_if_after_true, label %safe_if_run_true

for_loop472:                                      ; preds = %for_loop472.lr.ph, %for_step473
  %13 = phi i8 [ %63, %for_loop472.lr.ph ], [ %17, %for_step473 ]
  %"oldMask&test4813540" = phi <8 x i32> [ %cmp414_to_boolvec, %for_loop472.lr.ph ], [ %"oldMask&test481", %for_step473 ]
  %dx478.03539 = phi <8 x i32> [ splat (i32 -1), %for_loop472.lr.ph ], [ %dx_load707_plus1, %for_step473 ]
  %bestIdx470.03538 = phi <8 x i32> [ splat (i32 -1), %for_loop472.lr.ph ], [ %bestIdx470.1, %for_step473 ]
  %bestDistSq469.03537 = phi <8 x float> [ splat (float 0x47EFFFFFE0000000), %for_loop472.lr.ph ], [ %bestDistSq469.1, %for_step473 ]
  %add_cell436485_x_dx_load487 = add nsw <8 x i32> %dx478.03539, %blend.i16.i.v
  %greaterequal_nx_load488_GridDimensions489_x_broadcast491.not = icmp ult <8 x i32> %add_cell436485_x_dx_load487, %get_element459_broadcast460
  %"oldMask&test493" = select <8 x i1> %greaterequal_nx_load488_GridDimensions489_x_broadcast491.not, <8 x i32> zeroinitializer, <8 x i32> %"oldMask&test4813540"
  %14 = icmp slt <8 x i32> %"oldMask&test493", zeroinitializer
  %15 = bitcast <8 x i1> %14 to i8
  %"equal_finished&func502_load_mask483" = icmp eq i8 %13, %15
  br i1 %"equal_finished&func502_load_mask483", label %for_step473, label %not_all_continued_or_breaked504

for_step473:                                      ; preds = %not_all_continued_or_breaked504, %for_step516, %for_loop472
  %bestDistSq469.1 = phi <8 x float> [ %bestDistSq469.03537, %for_loop472 ], [ %bestDistSq469.03537, %not_all_continued_or_breaked504 ], [ %bestDistSq469.3, %for_step516 ]
  %bestIdx470.1 = phi <8 x i32> [ %bestIdx470.03538, %for_loop472 ], [ %bestIdx470.03538, %not_all_continued_or_breaked504 ], [ %bestIdx470.3, %for_step516 ]
  %internal_mask_memory.10 = phi <8 x i32> [ zeroinitializer, %for_loop472 ], [ %new_mask513, %not_all_continued_or_breaked504 ], [ %new_mask513, %for_step516 ]
  %"mask|continue_mask706" = or <8 x i32> %internal_mask_memory.10, %"oldMask&test493"
  %dx_load707_plus1 = add nsw <8 x i32> %dx478.03539, splat (i32 1)
  %lessequal_dx_load479_.inv = icmp sgt <8 x i32> %dx478.03539, zeroinitializer
  %"oldMask&test481" = select <8 x i1> %lessequal_dx_load479_.inv, <8 x i32> zeroinitializer, <8 x i32> %"mask|continue_mask706"
  %16 = icmp slt <8 x i32> %"oldMask&test481", zeroinitializer
  %17 = bitcast <8 x i1> %16 to i8
  %cmp.i3013.not = icmp eq i8 %17, 0
  br i1 %cmp.i3013.not, label %for_exit474, label %for_loop472, !llvm.loop !65

for_exit474:                                      ; preds = %for_step473, %partial_inner_only
  %bestDistSq469.0.lcssa = phi <8 x float> [ splat (float 0x47EFFFFFE0000000), %partial_inner_only ], [ %bestDistSq469.1, %for_step473 ]
  %bestIdx470.0.lcssa = phi <8 x i32> [ splat (i32 -1), %partial_inner_only ], [ %bestIdx470.1, %for_step473 ]
  %notequal_bestIdx_load711_ = icmp ne <8 x i32> %bestIdx470.0.lcssa, splat (i32 -1)
  %18 = select <8 x i1> %notequal_bestIdx_load711_, <8 x i1> %cmp414, <8 x i1> zeroinitializer
  %19 = bitcast <8 x i1> %18 to i8
  %cmp.i3016.not = icmp eq i8 %19, 0
  br i1 %cmp.i3016.not, label %safe_if_after_true714, label %safe_if_run_true715

common.ret:                                       ; preds = %for_exit737, %safe_if_run_true825, %safe_if_after_true714, %partial_inner_all_outer
  ret void

outer_not_in_extras.partial_inner_all_outer_crit_edge: ; preds = %if_done
  %20 = trunc nsw i64 %indvars.iv.next3558 to i32
  br label %partial_inner_all_outer, !llvm.loop !63

partial_inner_all_outer:                          ; preds = %outer_not_in_extras.partial_inner_all_outer_crit_edge, %allocas
  %counter.1.lcssa = phi i32 [ %20, %outer_not_in_extras.partial_inner_all_outer_crit_edge ], [ %__startIndex, %allocas ]
  %before_full_end = icmp slt i32 %counter.1.lcssa, %ret.i.i
  br i1 %before_full_end, label %partial_inner_only, label %common.ret

not_all_continued_or_breaked:                     ; preds = %for_loop
  %new_mask91 = xor <8 x i32> %"oldMask&test85", %"oldMask&test3506"
  %21 = icmp slt <8 x i32> %new_mask91, zeroinitializer
  %22 = bitcast <8 x i1> %21 to i8
  %cmp.i3017.not3496 = icmp eq i8 %22, 0
  br i1 %cmp.i3017.not3496, label %for_step, label %for_loop93

for_loop93:                                       ; preds = %not_all_continued_or_breaked, %for_step94
  %23 = phi i8 [ %27, %for_step94 ], [ %22, %not_all_continued_or_breaked ]
  %"oldMask&test1003500" = phi <8 x i32> [ %"oldMask&test100", %for_step94 ], [ %new_mask91, %not_all_continued_or_breaked ]
  %dy.03499 = phi <8 x i32> [ %dy_load254_plus1, %for_step94 ], [ splat (i32 -1), %not_all_continued_or_breaked ]
  %bestIdx.23498 = phi <8 x i32> [ %bestIdx.3, %for_step94 ], [ %bestIdx.03504, %not_all_continued_or_breaked ]
  %bestDistSq.23497 = phi <8 x float> [ %bestDistSq.3, %for_step94 ], [ %bestDistSq.03503, %not_all_continued_or_breaked ]
  %add_cell103_y_dy_load105 = add nsw <8 x i32> %dy.03499, %blend.i3360.v
  %greaterequal_ny_load_GridDimensions106_y_broadcast108.not = icmp ult <8 x i32> %add_cell103_y_dy_load105, %get_element67_broadcast68
  %"oldMask&test110" = select <8 x i1> %greaterequal_ny_load_GridDimensions106_y_broadcast108.not, <8 x i32> zeroinitializer, <8 x i32> %"oldMask&test1003500"
  %24 = icmp slt <8 x i32> %"oldMask&test110", zeroinitializer
  %25 = bitcast <8 x i1> %24 to i8
  %"equal_finished&func119_load_mask102" = icmp eq i8 %23, %25
  br i1 %"equal_finished&func119_load_mask102", label %for_step94, label %not_all_continued_or_breaked121

for_step94:                                       ; preds = %not_all_continued_or_breaked167, %for_step179, %not_all_continued_or_breaked121, %for_loop93
  %bestDistSq.3 = phi <8 x float> [ %bestDistSq.23497, %for_loop93 ], [ %bestDistSq.23497, %not_all_continued_or_breaked121 ], [ %bestDistSq.23497, %not_all_continued_or_breaked167 ], [ %bestDistSq.5, %for_step179 ]
  %bestIdx.3 = phi <8 x i32> [ %bestIdx.23498, %for_loop93 ], [ %bestIdx.23498, %not_all_continued_or_breaked121 ], [ %bestIdx.23498, %not_all_continued_or_breaked167 ], [ %bestIdx.5, %for_step179 ]
  %continue_lanes_memory97.1 = phi <8 x i32> [ %"oldMask&test110", %for_loop93 ], [ %"mask|continueMask158", %not_all_continued_or_breaked121 ], [ %"mask|continueMask158", %not_all_continued_or_breaked167 ], [ %"mask|continueMask158", %for_step179 ]
  %internal_mask_memory.4 = phi <8 x i32> [ zeroinitializer, %for_loop93 ], [ zeroinitializer, %not_all_continued_or_breaked121 ], [ %new_mask176, %not_all_continued_or_breaked167 ], [ %new_mask176, %for_step179 ]
  %"mask|continue_mask253" = or <8 x i32> %internal_mask_memory.4, %continue_lanes_memory97.1
  %dy_load254_plus1 = add nsw <8 x i32> %dy.03499, splat (i32 1)
  %lessequal_dy_load_.inv = icmp sgt <8 x i32> %dy.03499, zeroinitializer
  %"oldMask&test100" = select <8 x i1> %lessequal_dy_load_.inv, <8 x i32> zeroinitializer, <8 x i32> %"mask|continue_mask253"
  %26 = icmp slt <8 x i32> %"oldMask&test100", zeroinitializer
  %27 = bitcast <8 x i1> %26 to i8
  %cmp.i3017.not = icmp eq i8 %27, 0
  br i1 %cmp.i3017.not, label %for_step, label %for_loop93, !llvm.loop !66

not_all_continued_or_breaked121:                  ; preds = %for_loop93
  %new_mask130 = xor <8 x i32> %"oldMask&test110", %"oldMask&test1003500"
  %mul_ny_load131_GridDimensions132_x_broadcast134 = mul nsw <8 x i32> %add_cell103_y_dy_load105, %get_element64_broadcast65
  %add_mul_ny_load131_GridDimensions132_x_broadcast134_nx_load135 = add nsw <8 x i32> %mul_ny_load131_GridDimensions132_x_broadcast134, %add_cell78_x_dx_load80
  %CellStartEnd_load136__data = load ptr, ptr %CellStartEnd, align 8
  %mul__cellHash_load = shl nsw <8 x i32> %add_mul_ny_load131_GridDimensions132_x_broadcast134_nx_load135, splat (i32 3)
  %v_1.i3361 = tail call <8 x i32> @llvm.x86.avx2.gather.d.d.256(<8 x i32> undef, ptr readonly %CellStartEnd_load136__data, <8 x i32> %mul__cellHash_load, <8 x i32> %new_mask130, i8 1)
  %28 = or disjoint <8 x i32> %mul__cellHash_load, splat (i32 4)
  %v_1.i3362 = tail call <8 x i32> @llvm.x86.avx2.gather.d.d.256(<8 x i32> undef, ptr readonly %CellStartEnd_load136__data, <8 x i32> %28, <8 x i32> %new_mask130, i8 1)
  %isneg2567 = icmp slt <8 x i32> %v_1.i3361, zeroinitializer
  %"oldMask&test155" = select <8 x i1> %isneg2567, <8 x i32> %new_mask130, <8 x i32> zeroinitializer
  %"mask|continueMask158" = or <8 x i32> %"oldMask&test155", %"oldMask&test110"
  %29 = icmp slt <8 x i32> %"mask|continueMask158", zeroinitializer
  %30 = bitcast <8 x i1> %29 to i8
  %"equal_finished&func164_load_mask102" = icmp eq i8 %23, %30
  br i1 %"equal_finished&func164_load_mask102", label %for_step94, label %not_all_continued_or_breaked167

not_all_continued_or_breaked167:                  ; preds = %not_all_continued_or_breaked121
  %new_mask176 = xor <8 x i32> %"mask|continueMask158", %"oldMask&test1003500"
  %less_i_load_end_load3488 = icmp slt <8 x i32> %v_1.i3361, %v_1.i3362
  %"oldMask&test1863489" = select <8 x i1> %less_i_load_end_load3488, <8 x i32> %new_mask176, <8 x i32> zeroinitializer
  %31 = icmp slt <8 x i32> %"oldMask&test1863489", zeroinitializer
  %32 = bitcast <8 x i1> %31 to i8
  %cmp.i3021.not3490 = icmp eq i8 %32, 0
  br i1 %cmp.i3021.not3490, label %for_step94, label %for_loop178

for_loop178:                                      ; preds = %not_all_continued_or_breaked167, %for_step179
  %33 = phi i8 [ %38, %for_step179 ], [ %32, %not_all_continued_or_breaked167 ]
  %"oldMask&test1863494" = phi <8 x i32> [ %"oldMask&test186", %for_step179 ], [ %"oldMask&test1863489", %not_all_continued_or_breaked167 ]
  %i.03493 = phi <8 x i32> [ %i_load247_plus1, %for_step179 ], [ %v_1.i3361, %not_all_continued_or_breaked167 ]
  %bestIdx.43492 = phi <8 x i32> [ %bestIdx.5, %for_step179 ], [ %bestIdx.23498, %not_all_continued_or_breaked167 ]
  %bestDistSq.43491 = phi <8 x float> [ %bestDistSq.5, %for_step179 ], [ %bestDistSq.23497, %not_all_continued_or_breaked167 ]
  %mul__i_load189 = shl nsw <8 x i32> %i.03493, splat (i32 3)
  %mask.i = bitcast <8 x i32> %"oldMask&test1863494" to <8 x float>
  %v_1.i3363 = tail call <8 x float> @llvm.x86.avx2.gather.d.ps.256(<8 x float> undef, ptr readonly %SortedPositions_ptr, <8 x i32> %mul__i_load189, <8 x float> %mask.i, i8 1)
  %34 = or disjoint <8 x i32> %mul__i_load189, splat (i32 4)
  %v_1.i3365 = tail call <8 x float> @llvm.x86.avx2.gather.d.ps.256(<8 x float> undef, ptr readonly %SortedPositions_ptr, <8 x i32> %34, <8 x float> %mask.i, i8 1)
  %sub_a14_x_b26_x.i.i = fsub <8 x float> %v_1.i3363, %v_1.i
  %sub_a19_y_b211_y.i.i = fsub <8 x float> %v_1.i3365, %v_1.i3348
  %mul_a13_x_b25_x.i.i.i = fmul <8 x float> %sub_a14_x_b26_x.i.i, %sub_a14_x_b26_x.i.i
  %mul_a17_y_b29_y.i.i.i = fmul <8 x float> %sub_a19_y_b211_y.i.i, %sub_a19_y_b211_y.i.i
  %add_mul_a13_x_b25_x_mul_a17_y_b29_y.i.i.i = fadd <8 x float> %mul_a13_x_b25_x.i.i.i, %mul_a17_y_b29_y.i.i.i
  %less_distSq_load_SquaredEpsilonSelf_load_broadcast206 = fcmp olt <8 x float> %add_mul_a13_x_b25_x_mul_a17_y_b29_y.i.i.i, %SquaredEpsilonSelf_load_broadcast206
  %"oldMask&test208" = select <8 x i1> %less_distSq_load_SquaredEpsilonSelf_load_broadcast206, <8 x i32> %"oldMask&test1863494", <8 x i32> zeroinitializer
  %35 = icmp slt <8 x i32> %"oldMask&test208", zeroinitializer
  %36 = bitcast <8 x i1> %35 to i8
  %"equal_finished&func217_load_mask188" = icmp eq i8 %33, %36
  br i1 %"equal_finished&func217_load_mask188", label %for_step179, label %not_all_continued_or_breaked219

for_step179:                                      ; preds = %for_loop178, %not_all_continued_or_breaked219
  %bestDistSq.5 = phi <8 x float> [ %bestDistSq.43491, %for_loop178 ], [ %blend.i.i, %not_all_continued_or_breaked219 ]
  %bestIdx.5 = phi <8 x i32> [ %bestIdx.43492, %for_loop178 ], [ %41, %not_all_continued_or_breaked219 ]
  %internal_mask_memory.6 = phi <8 x i32> [ zeroinitializer, %for_loop178 ], [ %new_mask228, %not_all_continued_or_breaked219 ]
  %"mask|continue_mask" = or <8 x i32> %internal_mask_memory.6, %"oldMask&test208"
  %i_load247_plus1 = add nsw <8 x i32> %i.03493, splat (i32 1)
  %less_i_load_end_load = icmp slt <8 x i32> %i_load247_plus1, %v_1.i3362
  %"oldMask&test186" = select <8 x i1> %less_i_load_end_load, <8 x i32> %"mask|continue_mask", <8 x i32> zeroinitializer
  %37 = icmp slt <8 x i32> %"oldMask&test186", zeroinitializer
  %38 = bitcast <8 x i1> %37 to i8
  %cmp.i3021.not = icmp eq i8 %38, 0
  br i1 %cmp.i3021.not, label %for_step94, label %for_loop178, !llvm.loop !67

not_all_continued_or_breaked219:                  ; preds = %for_loop178
  %new_mask228 = xor <8 x i32> %"oldMask&test208", %"oldMask&test1863494"
  %less_distSq_load229_bestDistSq_load = fcmp olt <8 x float> %add_mul_a13_x_b25_x_mul_a17_y_b29_y.i.i.i, %bestDistSq.43491
  %39 = bitcast <8 x i32> %new_mask228 to <8 x float>
  %mask_as_float.i.i = select <8 x i1> %less_distSq_load229_bestDistSq_load, <8 x float> %39, <8 x float> zeroinitializer
  %blend.i.i = call <8 x float> @llvm.x86.avx.blendv.ps.256(<8 x float> %bestDistSq.43491, <8 x float> %add_mul_a13_x_b25_x_mul_a17_y_b29_y.i.i.i, <8 x float> %mask_as_float.i.i)
  %40 = bitcast <8 x i32> %bestIdx.43492 to <8 x float>
  %newAsFloat.i3368 = bitcast <8 x i32> %i.03493 to <8 x float>
  %blend.i3369 = call <8 x float> @llvm.x86.avx.blendv.ps.256(<8 x float> %40, <8 x float> %newAsFloat.i3368, <8 x float> %mask_as_float.i.i)
  %41 = bitcast <8 x float> %blend.i3369 to <8 x i32>
  br label %for_step179

if_done:                                          ; preds = %for_exit283, %safe_if_run_true370, %safe_if_after_true
  %indvars.iv.next3558 = add nsw i64 %indvars.iv3557, 8
  %before_aligned_end30 = icmp slt i64 %indvars.iv.next3558, %1
  br i1 %before_aligned_end30, label %foreach_full_body, label %outer_not_in_extras.partial_inner_all_outer_crit_edge, !llvm.loop !63

safe_if_after_true:                               ; preds = %safe_if_run_true, %for_exit
  %"~test" = xor <8 x i32> %notequal_bestIdx_load__to_boolvec, splat (i32 -1)
  %42 = xor <8 x i1> %notequal_bestIdx_load_, splat (i1 true)
  %43 = bitcast <8 x i1> %42 to i8
  %cmp.i3024.not = icmp eq i8 %43, 0
  br i1 %cmp.i3024.not, label %if_done, label %for_test280.preheader

for_test280.preheader:                            ; preds = %safe_if_after_true
  %"oldMask&test2913508" = select <8 x i1> %less_i_load288_SortedLength_load_broadcast2893507, <8 x i32> %"~test", <8 x i32> zeroinitializer
  %44 = icmp slt <8 x i32> %"oldMask&test2913508", zeroinitializer
  %45 = bitcast <8 x i1> %44 to i8
  %cmp.i3025.not3509 = icmp eq i8 %45, 0
  br i1 %cmp.i3025.not3509, label %for_exit283, label %for_loop281

safe_if_run_true:                                 ; preds = %for_exit
  %mul__bestIdx_load271 = shl nsw <8 x i32> %bestIdx.1, splat (i32 3)
  %46 = or disjoint <8 x i32> %mul__bestIdx_load271, splat (i32 4)
  %new_add2611 = sext <8 x i32> %46 to <8 x i64>
  %vecmask_1.i = shufflevector <8 x i32> %notequal_bestIdx_load__to_boolvec, <8 x i32> poison, <4 x i32> <i32 0, i32 1, i32 2, i32 3>
  %vecmask_2.i = shufflevector <8 x i32> %notequal_bestIdx_load__to_boolvec, <8 x i32> poison, <4 x i32> <i32 4, i32 5, i32 6, i32 7>
  %offsets_1.i = shufflevector <8 x i64> %new_add2611, <8 x i64> poison, <4 x i32> <i32 0, i32 1, i32 2, i32 3>
  %offsets_2.i = shufflevector <8 x i64> %new_add2611, <8 x i64> poison, <4 x i32> <i32 4, i32 5, i32 6, i32 7>
  %v1_1.i = tail call <4 x i32> @llvm.x86.avx2.gather.q.d.256(<4 x i32> undef, ptr %HashIndex_ptr, <4 x i64> %offsets_1.i, <4 x i32> %vecmask_1.i, i8 1)
  %v2_1.i = tail call <4 x i32> @llvm.x86.avx2.gather.q.d.256(<4 x i32> undef, ptr %HashIndex_ptr, <4 x i64> %offsets_2.i, <4 x i32> %vecmask_2.i, i8 1)
  %v.i = shufflevector <4 x i32> %v1_1.i, <4 x i32> %v2_1.i, <8 x i32> <i32 0, i32 1, i32 2, i32 3, i32 4, i32 5, i32 6, i32 7>
  %val.i = bitcast <8 x i32> %v.i to <8 x float>
  call void @llvm.masked.store.v8f32.p0(<8 x float> %val.i, ptr %ptr, i32 1, <8 x i1> %notequal_bestIdx_load_)
  br label %safe_if_after_true

for_loop281:                                      ; preds = %for_test280.preheader, %for_step282
  %indvars.iv = phi i64 [ %indvars.iv.next, %for_step282 ], [ 0, %for_test280.preheader ]
  %47 = phi i8 [ %52, %for_step282 ], [ %45, %for_test280.preheader ]
  %"oldMask&test2913515" = phi <8 x i32> [ %"oldMask&test291", %for_step282 ], [ %"oldMask&test2913508", %for_test280.preheader ]
  %i287.03514 = phi <8 x i32> [ %i_load362_plus1, %for_step282 ], [ zeroinitializer, %for_test280.preheader ]
  %bestIdx.63511 = phi <8 x i32> [ %bestIdx.7, %for_step282 ], [ %bestIdx.1, %for_test280.preheader ]
  %bestDistSq.63510 = phi <8 x float> [ %bestDistSq.7, %for_step282 ], [ %bestDistSq.1, %for_test280.preheader ]
  %48 = shl nsw i64 %indvars.iv, 3
  %ptr2624 = getelementptr i8, ptr %SortedPositions_ptr, i64 %48, !filename !11, !first_line !41, !first_column !20, !last_line !41, !last_column !21
  %SortedPositions_ptr_load296_offset_load26232625 = load float, ptr %ptr2624, align 4
  %SortedPositions_ptr_load296_offset_load26232626 = insertelement <8 x float> poison, float %SortedPositions_ptr_load296_offset_load26232625, i64 0
  %SortedPositions_ptr_load296_offset_load26232627 = shufflevector <8 x float> %SortedPositions_ptr_load296_offset_load26232626, <8 x float> poison, <8 x i32> zeroinitializer, !filename !11, !first_line !41, !first_column !20, !last_line !41, !last_column !21
  %gep = getelementptr i8, ptr %invariant.gep, i64 %48
  %SortedPositions_ptr_load296_offset_load30726332638 = load float, ptr %gep, align 4
  %SortedPositions_ptr_load296_offset_load30726332639 = insertelement <8 x float> poison, float %SortedPositions_ptr_load296_offset_load30726332638, i64 0
  %SortedPositions_ptr_load296_offset_load30726332640 = shufflevector <8 x float> %SortedPositions_ptr_load296_offset_load30726332639, <8 x float> poison, <8 x i32> zeroinitializer, !filename !11, !first_line !41, !first_column !20, !last_line !41, !last_column !21
  %sub_a14_x_b26_x.i.i3144 = fsub <8 x float> %SortedPositions_ptr_load296_offset_load26232627, %v_1.i
  %sub_a19_y_b211_y.i.i3145 = fsub <8 x float> %SortedPositions_ptr_load296_offset_load30726332640, %v_1.i3348
  %mul_a13_x_b25_x.i.i.i3153 = fmul <8 x float> %sub_a14_x_b26_x.i.i3144, %sub_a14_x_b26_x.i.i3144
  %mul_a17_y_b29_y.i.i.i3154 = fmul <8 x float> %sub_a19_y_b211_y.i.i3145, %sub_a19_y_b211_y.i.i3145
  %add_mul_a13_x_b25_x_mul_a17_y_b29_y.i.i.i3155 = fadd <8 x float> %mul_a13_x_b25_x.i.i.i3153, %mul_a17_y_b29_y.i.i.i3154
  %less_distSq_load316_SquaredEpsilonSelf_load317_broadcast318 = fcmp olt <8 x float> %add_mul_a13_x_b25_x_mul_a17_y_b29_y.i.i.i3155, %SquaredEpsilonSelf_load_broadcast206
  %"oldMask&test321" = select <8 x i1> %less_distSq_load316_SquaredEpsilonSelf_load317_broadcast318, <8 x i32> %"oldMask&test2913515", <8 x i32> zeroinitializer
  %49 = icmp slt <8 x i32> %"oldMask&test321", zeroinitializer
  %50 = bitcast <8 x i1> %49 to i8
  %"equal_finished&func330_load_mask293" = icmp eq i8 %47, %50
  br i1 %"equal_finished&func330_load_mask293", label %for_step282, label %not_all_continued_or_breaked332

for_step282:                                      ; preds = %for_loop281, %not_all_continued_or_breaked332
  %bestDistSq.7 = phi <8 x float> [ %bestDistSq.63510, %for_loop281 ], [ %blend.i.i3372, %not_all_continued_or_breaked332 ]
  %bestIdx.7 = phi <8 x i32> [ %bestIdx.63511, %for_loop281 ], [ %57, %not_all_continued_or_breaked332 ]
  %internal_mask_memory.8 = phi <8 x i32> [ zeroinitializer, %for_loop281 ], [ %new_mask341, %not_all_continued_or_breaked332 ]
  %"mask|continue_mask361" = or <8 x i32> %internal_mask_memory.8, %"oldMask&test321"
  %indvars.iv.next = add nuw nsw i64 %indvars.iv, 1
  %i_load362_plus1 = add nuw nsw <8 x i32> %i287.03514, splat (i32 1)
  %less_i_load288_SortedLength_load_broadcast289 = icmp slt <8 x i32> %i_load362_plus1, %SortedLength_load_broadcast289
  %"oldMask&test291" = select <8 x i1> %less_i_load288_SortedLength_load_broadcast289, <8 x i32> %"mask|continue_mask361", <8 x i32> zeroinitializer
  %51 = icmp slt <8 x i32> %"oldMask&test291", zeroinitializer
  %52 = bitcast <8 x i1> %51 to i8
  %cmp.i3025.not = icmp eq i8 %52, 0
  br i1 %cmp.i3025.not, label %for_exit283, label %for_loop281, !llvm.loop !68

for_exit283:                                      ; preds = %for_step282, %for_test280.preheader
  %bestIdx.6.lcssa = phi <8 x i32> [ %bestIdx.1, %for_test280.preheader ], [ %bestIdx.7, %for_step282 ]
  %notequal_bestIdx_load366_ = icmp eq <8 x i32> %bestIdx.6.lcssa, splat (i32 -1)
  %"oldMask&test371" = select <8 x i1> %notequal_bestIdx_load366_, <8 x i32> zeroinitializer, <8 x i32> %"~test"
  %53 = icmp slt <8 x i32> %"oldMask&test371", zeroinitializer
  %54 = bitcast <8 x i1> %53 to i8
  %cmp.i3028.not = icmp eq i8 %54, 0
  br i1 %cmp.i3028.not, label %if_done, label %safe_if_run_true370

not_all_continued_or_breaked332:                  ; preds = %for_loop281
  %new_mask341 = xor <8 x i32> %"oldMask&test321", %"oldMask&test2913515"
  %less_distSq_load342_bestDistSq_load343 = fcmp olt <8 x float> %add_mul_a13_x_b25_x_mul_a17_y_b29_y.i.i.i3155, %bestDistSq.63510
  %55 = bitcast <8 x i32> %new_mask341 to <8 x float>
  %mask_as_float.i.i3370 = select <8 x i1> %less_distSq_load342_bestDistSq_load343, <8 x float> %55, <8 x float> zeroinitializer
  %blend.i.i3372 = call <8 x float> @llvm.x86.avx.blendv.ps.256(<8 x float> %bestDistSq.63510, <8 x float> %add_mul_a13_x_b25_x_mul_a17_y_b29_y.i.i.i3155, <8 x float> %mask_as_float.i.i3370)
  %56 = bitcast <8 x i32> %bestIdx.63511 to <8 x float>
  %newAsFloat.i3375 = bitcast <8 x i32> %i287.03514 to <8 x float>
  %blend.i3376 = call <8 x float> @llvm.x86.avx.blendv.ps.256(<8 x float> %56, <8 x float> %newAsFloat.i3375, <8 x float> %mask_as_float.i.i3370)
  %57 = bitcast <8 x float> %blend.i3376 to <8 x i32>
  br label %for_step282

safe_if_run_true370:                              ; preds = %for_exit283
  %mul__bestIdx_load376 = shl nsw <8 x i32> %bestIdx.6.lcssa, splat (i32 3)
  %58 = or disjoint <8 x i32> %mul__bestIdx_load376, splat (i32 4)
  %new_add2645 = sext <8 x i32> %58 to <8 x i64>
  %vecmask_1.i3377 = shufflevector <8 x i32> %"oldMask&test371", <8 x i32> poison, <4 x i32> <i32 0, i32 1, i32 2, i32 3>
  %vecmask_2.i3378 = shufflevector <8 x i32> %"oldMask&test371", <8 x i32> poison, <4 x i32> <i32 4, i32 5, i32 6, i32 7>
  %offsets_1.i3379 = shufflevector <8 x i64> %new_add2645, <8 x i64> poison, <4 x i32> <i32 0, i32 1, i32 2, i32 3>
  %offsets_2.i3380 = shufflevector <8 x i64> %new_add2645, <8 x i64> poison, <4 x i32> <i32 4, i32 5, i32 6, i32 7>
  %v1_1.i3381 = tail call <4 x i32> @llvm.x86.avx2.gather.q.d.256(<4 x i32> undef, ptr %HashIndex_ptr, <4 x i64> %offsets_1.i3379, <4 x i32> %vecmask_1.i3377, i8 1)
  %v2_1.i3382 = tail call <4 x i32> @llvm.x86.avx2.gather.q.d.256(<4 x i32> undef, ptr %HashIndex_ptr, <4 x i64> %offsets_2.i3380, <4 x i32> %vecmask_2.i3378, i8 1)
  %v.i3383 = shufflevector <4 x i32> %v1_1.i3381, <4 x i32> %v2_1.i3382, <8 x i32> <i32 0, i32 1, i32 2, i32 3, i32 4, i32 5, i32 6, i32 7>
  %val.i3384 = bitcast <8 x i32> %v.i3383 to <8 x float>
  call void @llvm.x86.avx.maskstore.ps.256(ptr nonnull %ptr, <8 x i32> %"oldMask&test371", <8 x float> %val.i3384)
  br label %if_done

partial_inner_only:                               ; preds = %partial_inner_all_outer
  %smear_counter_init409 = insertelement <8 x i32> poison, i32 %counter.1.lcssa, i64 0
  %smear_counter410 = shufflevector <8 x i32> %smear_counter_init409, <8 x i32> poison, <8 x i32> zeroinitializer
  %iter_val411 = add nsw <8 x i32> %smear_counter410, <i32 0, i32 1, i32 2, i32 3, i32 4, i32 5, i32 6, i32 7>
  %smear_end_init412 = insertelement <8 x i32> poison, i32 %ret.i.i, i64 0
  %smear_end413 = shufflevector <8 x i32> %smear_end_init412, <8 x i32> poison, <8 x i32> zeroinitializer
  %cmp414 = icmp slt <8 x i32> %iter_val411, %smear_end413
  %cmp414_to_boolvec = sext <8 x i1> %cmp414 to <8 x i32>
  %mul__index_load416.elt0 = shl nsw i32 %counter.1.lcssa, 2
  %59 = sext i32 %mul__index_load416.elt0 to i64
  %ptr2580 = getelementptr i8, ptr %Results_ptr, i64 %59
  call void @llvm.masked.store.v8f32.p0(<8 x float> splat (float 0xFFFFFFFFE0000000), ptr %ptr2580, i32 1, <8 x i1> %cmp414)
  %mul__index_load422 = shl nsw <8 x i32> %iter_val411, splat (i32 3)
  %mask.i3385 = bitcast <8 x i32> %cmp414_to_boolvec to <8 x float>
  %v_1.i3386 = tail call <8 x float> @llvm.x86.avx2.gather.d.ps.256(<8 x float> undef, ptr readonly %QueryPositions_ptr, <8 x i32> %mul__index_load422, <8 x float> %mask.i3385, i8 1)
  %60 = or disjoint <8 x i32> %mul__index_load422, splat (i32 4)
  %v_1.i3389 = tail call <8 x float> @llvm.x86.avx2.gather.d.ps.256(<8 x float> undef, ptr readonly %QueryPositions_ptr, <8 x i32> %60, <8 x float> %mask.i3385, i8 1)
  %get_element439_broadcast = insertelement <8 x float> poison, float %GridOrigin_ptr_load_load.unpack, i64 0
  %get_element439_broadcast440 = shufflevector <8 x float> %get_element439_broadcast, <8 x float> poison, <8 x i32> zeroinitializer
  %get_element442_broadcast = insertelement <8 x float> poison, float %GridOrigin_ptr_load_load.unpack2560, i64 0
  %get_element442_broadcast443 = shufflevector <8 x float> %get_element442_broadcast, <8 x float> poison, <8 x i32> zeroinitializer
  %sub_a14_x_b26_x.i3099 = fsub <8 x float> %v_1.i3386, %get_element439_broadcast440
  %sub_a19_y_b211_y.i3100 = fsub <8 x float> %v_1.i3389, %get_element442_broadcast443
  %GridResolutionInv_load447_broadcast = insertelement <8 x float> poison, float %GridResolutionInv_ptr_load_load, i64 0
  %GridResolutionInv_load447_broadcast448 = shufflevector <8 x float> %GridResolutionInv_load447_broadcast, <8 x float> poison, <8 x i32> zeroinitializer
  %mul_v14_x_s_load.i3106 = fmul <8 x float> %GridResolutionInv_load447_broadcast448, %sub_a14_x_b26_x.i3099
  %mul_v17_y_s_load9.i3107 = fmul <8 x float> %GridResolutionInv_load447_broadcast448, %sub_a19_y_b211_y.i3100
  %call.i.i.i3113 = tail call <8 x float> @llvm.x86.avx.round.ps.256(<8 x float> %mul_v14_x_s_load.i3106, i32 9)
  %call.i.i3.i3114 = tail call <8 x float> @llvm.x86.avx.round.ps.256(<8 x float> %mul_v17_y_s_load9.i3107, i32 9)
  %v12_x_to_int32.i3120 = fptosi <8 x float> %call.i.i.i3113 to <8 x i32>
  %v14_y_to_int32.i3121 = fptosi <8 x float> %call.i.i3.i3114 to <8 x i32>
  %get_element459_broadcast = insertelement <8 x i32> poison, i32 %GridDimensions_ptr_load_load.unpack, i64 0
  %get_element459_broadcast460 = shufflevector <8 x i32> %get_element459_broadcast, <8 x i32> poison, <8 x i32> zeroinitializer
  %get_element462_broadcast = insertelement <8 x i32> poison, i32 %GridDimensions_ptr_load_load.unpack2563, i64 0
  %get_element462_broadcast463 = shufflevector <8 x i32> %get_element462_broadcast, <8 x i32> poison, <8 x i32> zeroinitializer
  %sub_a14_x_b_load.i3131 = add nsw <8 x i32> %get_element459_broadcast460, splat (i32 -1)
  %sub_a17_y_b_load9.i3132 = add nsw <8 x i32> %get_element462_broadcast463, splat (i32 -1)
  %61 = call <8 x i32> @llvm.smax.v8i32(<8 x i32> %v12_x_to_int32.i3120, <8 x i32> zeroinitializer)
  %62 = call <8 x i32> @llvm.smax.v8i32(<8 x i32> %v14_y_to_int32.i3121, <8 x i32> zeroinitializer)
  %blend.i16.i.v = call <8 x i32> @llvm.smin.v8i32(<8 x i32> %61, <8 x i32> %sub_a14_x_b_load.i3131)
  %blend.i20.i.v = call <8 x i32> @llvm.smin.v8i32(<8 x i32> %62, <8 x i32> %sub_a17_y_b_load9.i3132)
  %63 = bitcast <8 x i1> %cmp414 to i8
  %cmp.i3013.not3536 = icmp eq i8 %63, 0
  br i1 %cmp.i3013.not3536, label %for_exit474, label %for_loop472.lr.ph

for_loop472.lr.ph:                                ; preds = %partial_inner_only
  %SquaredEpsilonSelf_load648_broadcast = insertelement <8 x float> poison, float %SquaredEpsilonSelf_ptr_load_load, i64 0
  %SquaredEpsilonSelf_load648_broadcast649 = shufflevector <8 x float> %SquaredEpsilonSelf_load648_broadcast, <8 x float> poison, <8 x i32> zeroinitializer
  br label %for_loop472

not_all_continued_or_breaked504:                  ; preds = %for_loop472
  %new_mask513 = xor <8 x i32> %"oldMask&test493", %"oldMask&test4813540"
  %64 = icmp slt <8 x i32> %new_mask513, zeroinitializer
  %65 = bitcast <8 x i1> %64 to i8
  %cmp.i3030.not3529 = icmp eq i8 %65, 0
  br i1 %cmp.i3030.not3529, label %for_step473, label %for_loop515

for_loop515:                                      ; preds = %not_all_continued_or_breaked504, %for_step516
  %66 = phi i8 [ %70, %for_step516 ], [ %65, %not_all_continued_or_breaked504 ]
  %"oldMask&test5243533" = phi <8 x i32> [ %"oldMask&test524", %for_step516 ], [ %new_mask513, %not_all_continued_or_breaked504 ]
  %dy521.03532 = phi <8 x i32> [ %dy_load700_plus1, %for_step516 ], [ splat (i32 -1), %not_all_continued_or_breaked504 ]
  %bestIdx470.23531 = phi <8 x i32> [ %bestIdx470.3, %for_step516 ], [ %bestIdx470.03538, %not_all_continued_or_breaked504 ]
  %bestDistSq469.23530 = phi <8 x float> [ %bestDistSq469.3, %for_step516 ], [ %bestDistSq469.03537, %not_all_continued_or_breaked504 ]
  %add_cell436528_y_dy_load530 = add nsw <8 x i32> %dy521.03532, %blend.i20.i.v
  %greaterequal_ny_load531_GridDimensions532_y_broadcast534.not = icmp ult <8 x i32> %add_cell436528_y_dy_load530, %get_element462_broadcast463
  %"oldMask&test536" = select <8 x i1> %greaterequal_ny_load531_GridDimensions532_y_broadcast534.not, <8 x i32> zeroinitializer, <8 x i32> %"oldMask&test5243533"
  %67 = icmp slt <8 x i32> %"oldMask&test536", zeroinitializer
  %68 = bitcast <8 x i1> %67 to i8
  %"equal_finished&func545_load_mask526" = icmp eq i8 %66, %68
  br i1 %"equal_finished&func545_load_mask526", label %for_step516, label %not_all_continued_or_breaked547

for_step516:                                      ; preds = %not_all_continued_or_breaked600, %for_step612, %not_all_continued_or_breaked547, %for_loop515
  %bestDistSq469.3 = phi <8 x float> [ %bestDistSq469.23530, %for_loop515 ], [ %bestDistSq469.23530, %not_all_continued_or_breaked547 ], [ %bestDistSq469.23530, %not_all_continued_or_breaked600 ], [ %bestDistSq469.5, %for_step612 ]
  %bestIdx470.3 = phi <8 x i32> [ %bestIdx470.23531, %for_loop515 ], [ %bestIdx470.23531, %not_all_continued_or_breaked547 ], [ %bestIdx470.23531, %not_all_continued_or_breaked600 ], [ %bestIdx470.5, %for_step612 ]
  %continue_lanes_memory519.1 = phi <8 x i32> [ %"oldMask&test536", %for_loop515 ], [ %"mask|continueMask591", %not_all_continued_or_breaked547 ], [ %"mask|continueMask591", %not_all_continued_or_breaked600 ], [ %"mask|continueMask591", %for_step612 ]
  %internal_mask_memory.12 = phi <8 x i32> [ zeroinitializer, %for_loop515 ], [ zeroinitializer, %not_all_continued_or_breaked547 ], [ %new_mask609, %not_all_continued_or_breaked600 ], [ %new_mask609, %for_step612 ]
  %"mask|continue_mask699" = or <8 x i32> %internal_mask_memory.12, %continue_lanes_memory519.1
  %dy_load700_plus1 = add nsw <8 x i32> %dy521.03532, splat (i32 1)
  %lessequal_dy_load522_.inv = icmp sgt <8 x i32> %dy521.03532, zeroinitializer
  %"oldMask&test524" = select <8 x i1> %lessequal_dy_load522_.inv, <8 x i32> zeroinitializer, <8 x i32> %"mask|continue_mask699"
  %69 = icmp slt <8 x i32> %"oldMask&test524", zeroinitializer
  %70 = bitcast <8 x i1> %69 to i8
  %cmp.i3030.not = icmp eq i8 %70, 0
  br i1 %cmp.i3030.not, label %for_step473, label %for_loop515, !llvm.loop !69

not_all_continued_or_breaked547:                  ; preds = %for_loop515
  %new_mask556 = xor <8 x i32> %"oldMask&test536", %"oldMask&test5243533"
  %mul_ny_load558_GridDimensions559_x_broadcast561 = mul nsw <8 x i32> %add_cell436528_y_dy_load530, %get_element459_broadcast460
  %add_mul_ny_load558_GridDimensions559_x_broadcast561_nx_load562 = add nsw <8 x i32> %mul_ny_load558_GridDimensions559_x_broadcast561, %add_cell436485_x_dx_load487
  %CellStartEnd_load565566__data = load ptr, ptr %CellStartEnd, align 8
  %mul__cellHash_load564 = shl nsw <8 x i32> %add_mul_ny_load558_GridDimensions559_x_broadcast561_nx_load562, splat (i32 3)
  %v_1.i3391 = tail call <8 x i32> @llvm.x86.avx2.gather.d.d.256(<8 x i32> undef, ptr readonly %CellStartEnd_load565566__data, <8 x i32> %mul__cellHash_load564, <8 x i32> %new_mask556, i8 1)
  %71 = or disjoint <8 x i32> %mul__cellHash_load564, splat (i32 4)
  %v_1.i3393 = tail call <8 x i32> @llvm.x86.avx2.gather.d.d.256(<8 x i32> undef, ptr readonly %CellStartEnd_load565566__data, <8 x i32> %71, <8 x i32> %new_mask556, i8 1)
  %isneg2566 = icmp slt <8 x i32> %v_1.i3391, zeroinitializer
  %"oldMask&test588" = select <8 x i1> %isneg2566, <8 x i32> %new_mask556, <8 x i32> zeroinitializer
  %"mask|continueMask591" = or <8 x i32> %"oldMask&test588", %"oldMask&test536"
  %72 = icmp slt <8 x i32> %"mask|continueMask591", zeroinitializer
  %73 = bitcast <8 x i1> %72 to i8
  %"equal_finished&func597_load_mask526" = icmp eq i8 %66, %73
  br i1 %"equal_finished&func597_load_mask526", label %for_step516, label %not_all_continued_or_breaked600

not_all_continued_or_breaked600:                  ; preds = %not_all_continued_or_breaked547
  %new_mask609 = xor <8 x i32> %"mask|continueMask591", %"oldMask&test5243533"
  %less_i_load619_end_load6203520 = icmp slt <8 x i32> %v_1.i3391, %v_1.i3393
  %"oldMask&test6223521" = select <8 x i1> %less_i_load619_end_load6203520, <8 x i32> %new_mask609, <8 x i32> zeroinitializer
  %74 = icmp slt <8 x i32> %"oldMask&test6223521", zeroinitializer
  %75 = bitcast <8 x i1> %74 to i8
  %cmp.i3034.not3522 = icmp eq i8 %75, 0
  br i1 %cmp.i3034.not3522, label %for_step516, label %for_loop611

for_loop611:                                      ; preds = %not_all_continued_or_breaked600, %for_step612
  %76 = phi i8 [ %81, %for_step612 ], [ %75, %not_all_continued_or_breaked600 ]
  %"oldMask&test6223526" = phi <8 x i32> [ %"oldMask&test622", %for_step612 ], [ %"oldMask&test6223521", %not_all_continued_or_breaked600 ]
  %i617.03525 = phi <8 x i32> [ %i_load693_plus1, %for_step612 ], [ %v_1.i3391, %not_all_continued_or_breaked600 ]
  %bestIdx470.43524 = phi <8 x i32> [ %bestIdx470.5, %for_step612 ], [ %bestIdx470.23531, %not_all_continued_or_breaked600 ]
  %bestDistSq469.43523 = phi <8 x float> [ %bestDistSq469.5, %for_step612 ], [ %bestDistSq469.23530, %not_all_continued_or_breaked600 ]
  %mul__i_load626 = shl nsw <8 x i32> %i617.03525, splat (i32 3)
  %mask.i3395 = bitcast <8 x i32> %"oldMask&test6223526" to <8 x float>
  %v_1.i3396 = tail call <8 x float> @llvm.x86.avx2.gather.d.ps.256(<8 x float> undef, ptr readonly %SortedPositions_ptr, <8 x i32> %mul__i_load626, <8 x float> %mask.i3395, i8 1)
  %77 = or disjoint <8 x i32> %mul__i_load626, splat (i32 4)
  %v_1.i3399 = tail call <8 x float> @llvm.x86.avx2.gather.d.ps.256(<8 x float> undef, ptr readonly %SortedPositions_ptr, <8 x i32> %77, <8 x float> %mask.i3395, i8 1)
  %sub_a14_x_b26_x.i.i3156 = fsub <8 x float> %v_1.i3396, %v_1.i3386
  %sub_a19_y_b211_y.i.i3157 = fsub <8 x float> %v_1.i3399, %v_1.i3389
  %mul_a13_x_b25_x.i.i.i3165 = fmul <8 x float> %sub_a14_x_b26_x.i.i3156, %sub_a14_x_b26_x.i.i3156
  %mul_a17_y_b29_y.i.i.i3166 = fmul <8 x float> %sub_a19_y_b211_y.i.i3157, %sub_a19_y_b211_y.i.i3157
  %add_mul_a13_x_b25_x_mul_a17_y_b29_y.i.i.i3167 = fadd <8 x float> %mul_a13_x_b25_x.i.i.i3165, %mul_a17_y_b29_y.i.i.i3166
  %less_distSq_load647_SquaredEpsilonSelf_load648_broadcast649 = fcmp olt <8 x float> %add_mul_a13_x_b25_x_mul_a17_y_b29_y.i.i.i3167, %SquaredEpsilonSelf_load648_broadcast649
  %"oldMask&test652" = select <8 x i1> %less_distSq_load647_SquaredEpsilonSelf_load648_broadcast649, <8 x i32> %"oldMask&test6223526", <8 x i32> zeroinitializer
  %78 = icmp slt <8 x i32> %"oldMask&test652", zeroinitializer
  %79 = bitcast <8 x i1> %78 to i8
  %"equal_finished&func661_load_mask624" = icmp eq i8 %76, %79
  br i1 %"equal_finished&func661_load_mask624", label %for_step612, label %not_all_continued_or_breaked663

for_step612:                                      ; preds = %for_loop611, %not_all_continued_or_breaked663
  %bestDistSq469.5 = phi <8 x float> [ %bestDistSq469.43523, %for_loop611 ], [ %blend.i.i3403, %not_all_continued_or_breaked663 ]
  %bestIdx470.5 = phi <8 x i32> [ %bestIdx470.43524, %for_loop611 ], [ %84, %not_all_continued_or_breaked663 ]
  %internal_mask_memory.14 = phi <8 x i32> [ zeroinitializer, %for_loop611 ], [ %new_mask672, %not_all_continued_or_breaked663 ]
  %"mask|continue_mask692" = or <8 x i32> %internal_mask_memory.14, %"oldMask&test652"
  %i_load693_plus1 = add nsw <8 x i32> %i617.03525, splat (i32 1)
  %less_i_load619_end_load620 = icmp slt <8 x i32> %i_load693_plus1, %v_1.i3393
  %"oldMask&test622" = select <8 x i1> %less_i_load619_end_load620, <8 x i32> %"mask|continue_mask692", <8 x i32> zeroinitializer
  %80 = icmp slt <8 x i32> %"oldMask&test622", zeroinitializer
  %81 = bitcast <8 x i1> %80 to i8
  %cmp.i3034.not = icmp eq i8 %81, 0
  br i1 %cmp.i3034.not, label %for_step516, label %for_loop611, !llvm.loop !70

not_all_continued_or_breaked663:                  ; preds = %for_loop611
  %new_mask672 = xor <8 x i32> %"oldMask&test652", %"oldMask&test6223526"
  %less_distSq_load673_bestDistSq_load674 = fcmp olt <8 x float> %add_mul_a13_x_b25_x_mul_a17_y_b29_y.i.i.i3167, %bestDistSq469.43523
  %82 = bitcast <8 x i32> %new_mask672 to <8 x float>
  %mask_as_float.i.i3401 = select <8 x i1> %less_distSq_load673_bestDistSq_load674, <8 x float> %82, <8 x float> zeroinitializer
  %blend.i.i3403 = call <8 x float> @llvm.x86.avx.blendv.ps.256(<8 x float> %bestDistSq469.43523, <8 x float> %add_mul_a13_x_b25_x_mul_a17_y_b29_y.i.i.i3167, <8 x float> %mask_as_float.i.i3401)
  %83 = bitcast <8 x i32> %bestIdx470.43524 to <8 x float>
  %newAsFloat.i3406 = bitcast <8 x i32> %i617.03525 to <8 x float>
  %blend.i3407 = call <8 x float> @llvm.x86.avx.blendv.ps.256(<8 x float> %83, <8 x float> %newAsFloat.i3406, <8 x float> %mask_as_float.i.i3401)
  %84 = bitcast <8 x float> %blend.i3407 to <8 x i32>
  br label %for_step612

safe_if_after_true714:                            ; preds = %safe_if_run_true715, %for_exit474
  %"oldMask&~test732" = select <8 x i1> %notequal_bestIdx_load711_, <8 x i32> zeroinitializer, <8 x i32> %cmp414_to_boolvec
  %not.notequal_bestIdx_load711_ = xor <8 x i1> %notequal_bestIdx_load711_, splat (i1 true)
  %85 = select <8 x i1> %not.notequal_bestIdx_load711_, <8 x i1> %cmp414, <8 x i1> zeroinitializer
  %86 = bitcast <8 x i1> %85 to i8
  %cmp.i3037.not = icmp eq i8 %86, 0
  br i1 %cmp.i3037.not, label %common.ret, label %for_test734.preheader

for_test734.preheader:                            ; preds = %safe_if_after_true714
  %SortedLength_load743_broadcast = insertelement <8 x i32> poison, i32 %SortedLength_ptr_load_load, i64 0
  %SortedLength_load743_broadcast744 = shufflevector <8 x i32> %SortedLength_load743_broadcast, <8 x i32> poison, <8 x i32> zeroinitializer
  %less_i_load742_SortedLength_load743_broadcast7443543 = icmp sgt <8 x i32> %SortedLength_load743_broadcast744, zeroinitializer
  %"oldMask&test7463544" = select <8 x i1> %less_i_load742_SortedLength_load743_broadcast7443543, <8 x i32> %"oldMask&~test732", <8 x i32> zeroinitializer
  %87 = icmp slt <8 x i32> %"oldMask&test7463544", zeroinitializer
  %88 = bitcast <8 x i1> %87 to i8
  %cmp.i3038.not3545 = icmp eq i8 %88, 0
  br i1 %cmp.i3038.not3545, label %for_exit737, label %for_loop735.lr.ph

for_loop735.lr.ph:                                ; preds = %for_test734.preheader
  %invariant.gep3553 = getelementptr i8, ptr %SortedPositions_ptr, i64 4
  %SquaredEpsilonSelf_load772_broadcast = insertelement <8 x float> poison, float %SquaredEpsilonSelf_ptr_load_load, i64 0
  %SquaredEpsilonSelf_load772_broadcast773 = shufflevector <8 x float> %SquaredEpsilonSelf_load772_broadcast, <8 x float> poison, <8 x i32> zeroinitializer
  br label %for_loop735

safe_if_run_true715:                              ; preds = %for_exit474
  %"oldMask&test716" = select <8 x i1> %notequal_bestIdx_load711_, <8 x i32> %cmp414_to_boolvec, <8 x i32> zeroinitializer
  %mul__bestIdx_load721 = shl nsw <8 x i32> %bestIdx470.0.lcssa, splat (i32 3)
  %89 = or disjoint <8 x i32> %mul__bestIdx_load721, splat (i32 4)
  %new_add2677 = sext <8 x i32> %89 to <8 x i64>
  %vecmask_1.i3408 = shufflevector <8 x i32> %"oldMask&test716", <8 x i32> poison, <4 x i32> <i32 0, i32 1, i32 2, i32 3>
  %vecmask_2.i3409 = shufflevector <8 x i32> %"oldMask&test716", <8 x i32> poison, <4 x i32> <i32 4, i32 5, i32 6, i32 7>
  %offsets_1.i3410 = shufflevector <8 x i64> %new_add2677, <8 x i64> poison, <4 x i32> <i32 0, i32 1, i32 2, i32 3>
  %offsets_2.i3411 = shufflevector <8 x i64> %new_add2677, <8 x i64> poison, <4 x i32> <i32 4, i32 5, i32 6, i32 7>
  %v1_1.i3412 = tail call <4 x i32> @llvm.x86.avx2.gather.q.d.256(<4 x i32> undef, ptr %HashIndex_ptr, <4 x i64> %offsets_1.i3410, <4 x i32> %vecmask_1.i3408, i8 1)
  %v2_1.i3413 = tail call <4 x i32> @llvm.x86.avx2.gather.q.d.256(<4 x i32> undef, ptr %HashIndex_ptr, <4 x i64> %offsets_2.i3411, <4 x i32> %vecmask_2.i3409, i8 1)
  %v.i3414 = shufflevector <4 x i32> %v1_1.i3412, <4 x i32> %v2_1.i3413, <8 x i32> <i32 0, i32 1, i32 2, i32 3, i32 4, i32 5, i32 6, i32 7>
  %val.i3415 = bitcast <8 x i32> %v.i3414 to <8 x float>
  call void @llvm.x86.avx.maskstore.ps.256(ptr %ptr2580, <8 x i32> %"oldMask&test716", <8 x float> %val.i3415)
  br label %safe_if_after_true714

for_loop735:                                      ; preds = %for_loop735.lr.ph, %for_step736
  %indvars.iv3561 = phi i64 [ 0, %for_loop735.lr.ph ], [ %indvars.iv.next3562, %for_step736 ]
  %90 = phi i8 [ %88, %for_loop735.lr.ph ], [ %95, %for_step736 ]
  %"oldMask&test7463551" = phi <8 x i32> [ %"oldMask&test7463544", %for_loop735.lr.ph ], [ %"oldMask&test746", %for_step736 ]
  %i741.03550 = phi <8 x i32> [ zeroinitializer, %for_loop735.lr.ph ], [ %i_load817_plus1, %for_step736 ]
  %bestIdx470.63547 = phi <8 x i32> [ %bestIdx470.0.lcssa, %for_loop735.lr.ph ], [ %bestIdx470.7, %for_step736 ]
  %bestDistSq469.63546 = phi <8 x float> [ %bestDistSq469.0.lcssa, %for_loop735.lr.ph ], [ %bestDistSq469.7, %for_step736 ]
  %91 = shl nsw i64 %indvars.iv3561, 3
  %ptr2694 = getelementptr i8, ptr %SortedPositions_ptr, i64 %91
  %SortedPositions_ptr_load751_offset_load26932695 = load float, ptr %ptr2694, align 4
  %SortedPositions_ptr_load751_offset_load26932696 = insertelement <8 x float> poison, float %SortedPositions_ptr_load751_offset_load26932695, i64 0
  %SortedPositions_ptr_load751_offset_load26932697 = shufflevector <8 x float> %SortedPositions_ptr_load751_offset_load26932696, <8 x float> poison, <8 x i32> zeroinitializer
  %gep3554 = getelementptr i8, ptr %invariant.gep3553, i64 %91
  %SortedPositions_ptr_load751_offset_load76227032708 = load float, ptr %gep3554, align 4
  %SortedPositions_ptr_load751_offset_load76227032709 = insertelement <8 x float> poison, float %SortedPositions_ptr_load751_offset_load76227032708, i64 0
  %SortedPositions_ptr_load751_offset_load76227032710 = shufflevector <8 x float> %SortedPositions_ptr_load751_offset_load76227032709, <8 x float> poison, <8 x i32> zeroinitializer
  %sub_a14_x_b26_x.i.i3168 = fsub <8 x float> %SortedPositions_ptr_load751_offset_load26932697, %v_1.i3386
  %sub_a19_y_b211_y.i.i3169 = fsub <8 x float> %SortedPositions_ptr_load751_offset_load76227032710, %v_1.i3389
  %mul_a13_x_b25_x.i.i.i3177 = fmul <8 x float> %sub_a14_x_b26_x.i.i3168, %sub_a14_x_b26_x.i.i3168
  %mul_a17_y_b29_y.i.i.i3178 = fmul <8 x float> %sub_a19_y_b211_y.i.i3169, %sub_a19_y_b211_y.i.i3169
  %add_mul_a13_x_b25_x_mul_a17_y_b29_y.i.i.i3179 = fadd <8 x float> %mul_a13_x_b25_x.i.i.i3177, %mul_a17_y_b29_y.i.i.i3178
  %less_distSq_load771_SquaredEpsilonSelf_load772_broadcast773 = fcmp olt <8 x float> %add_mul_a13_x_b25_x_mul_a17_y_b29_y.i.i.i3179, %SquaredEpsilonSelf_load772_broadcast773
  %"oldMask&test776" = select <8 x i1> %less_distSq_load771_SquaredEpsilonSelf_load772_broadcast773, <8 x i32> %"oldMask&test7463551", <8 x i32> zeroinitializer
  %92 = icmp slt <8 x i32> %"oldMask&test776", zeroinitializer
  %93 = bitcast <8 x i1> %92 to i8
  %"equal_finished&func785_load_mask748" = icmp eq i8 %90, %93
  br i1 %"equal_finished&func785_load_mask748", label %for_step736, label %not_all_continued_or_breaked787

for_step736:                                      ; preds = %for_loop735, %not_all_continued_or_breaked787
  %bestDistSq469.7 = phi <8 x float> [ %bestDistSq469.63546, %for_loop735 ], [ %blend.i.i3418, %not_all_continued_or_breaked787 ]
  %bestIdx470.7 = phi <8 x i32> [ %bestIdx470.63547, %for_loop735 ], [ %100, %not_all_continued_or_breaked787 ]
  %internal_mask_memory.16 = phi <8 x i32> [ zeroinitializer, %for_loop735 ], [ %new_mask796, %not_all_continued_or_breaked787 ]
  %"mask|continue_mask816" = or <8 x i32> %internal_mask_memory.16, %"oldMask&test776"
  %indvars.iv.next3562 = add nuw nsw i64 %indvars.iv3561, 1
  %i_load817_plus1 = add nuw nsw <8 x i32> %i741.03550, splat (i32 1)
  %less_i_load742_SortedLength_load743_broadcast744 = icmp slt <8 x i32> %i_load817_plus1, %SortedLength_load743_broadcast744
  %"oldMask&test746" = select <8 x i1> %less_i_load742_SortedLength_load743_broadcast744, <8 x i32> %"mask|continue_mask816", <8 x i32> zeroinitializer
  %94 = icmp slt <8 x i32> %"oldMask&test746", zeroinitializer
  %95 = bitcast <8 x i1> %94 to i8
  %cmp.i3038.not = icmp eq i8 %95, 0
  br i1 %cmp.i3038.not, label %for_exit737, label %for_loop735, !llvm.loop !71

for_exit737:                                      ; preds = %for_step736, %for_test734.preheader
  %bestIdx470.6.lcssa = phi <8 x i32> [ %bestIdx470.0.lcssa, %for_test734.preheader ], [ %bestIdx470.7, %for_step736 ]
  %notequal_bestIdx_load821_ = icmp eq <8 x i32> %bestIdx470.6.lcssa, splat (i32 -1)
  %"oldMask&test826" = select <8 x i1> %notequal_bestIdx_load821_, <8 x i32> zeroinitializer, <8 x i32> %"oldMask&~test732"
  %96 = icmp slt <8 x i32> %"oldMask&test826", zeroinitializer
  %97 = bitcast <8 x i1> %96 to i8
  %cmp.i3041.not = icmp eq i8 %97, 0
  br i1 %cmp.i3041.not, label %common.ret, label %safe_if_run_true825

not_all_continued_or_breaked787:                  ; preds = %for_loop735
  %new_mask796 = xor <8 x i32> %"oldMask&test776", %"oldMask&test7463551"
  %less_distSq_load797_bestDistSq_load798 = fcmp olt <8 x float> %add_mul_a13_x_b25_x_mul_a17_y_b29_y.i.i.i3179, %bestDistSq469.63546
  %98 = bitcast <8 x i32> %new_mask796 to <8 x float>
  %mask_as_float.i.i3416 = select <8 x i1> %less_distSq_load797_bestDistSq_load798, <8 x float> %98, <8 x float> zeroinitializer
  %blend.i.i3418 = call <8 x float> @llvm.x86.avx.blendv.ps.256(<8 x float> %bestDistSq469.63546, <8 x float> %add_mul_a13_x_b25_x_mul_a17_y_b29_y.i.i.i3179, <8 x float> %mask_as_float.i.i3416)
  %99 = bitcast <8 x i32> %bestIdx470.63547 to <8 x float>
  %newAsFloat.i3421 = bitcast <8 x i32> %i741.03550 to <8 x float>
  %blend.i3422 = call <8 x float> @llvm.x86.avx.blendv.ps.256(<8 x float> %99, <8 x float> %newAsFloat.i3421, <8 x float> %mask_as_float.i.i3416)
  %100 = bitcast <8 x float> %blend.i3422 to <8 x i32>
  br label %for_step736

safe_if_run_true825:                              ; preds = %for_exit737
  %mul__bestIdx_load831 = shl nsw <8 x i32> %bestIdx470.6.lcssa, splat (i32 3)
  %101 = or disjoint <8 x i32> %mul__bestIdx_load831, splat (i32 4)
  %new_add2715 = sext <8 x i32> %101 to <8 x i64>
  %vecmask_1.i3423 = shufflevector <8 x i32> %"oldMask&test826", <8 x i32> poison, <4 x i32> <i32 0, i32 1, i32 2, i32 3>
  %vecmask_2.i3424 = shufflevector <8 x i32> %"oldMask&test826", <8 x i32> poison, <4 x i32> <i32 4, i32 5, i32 6, i32 7>
  %offsets_1.i3425 = shufflevector <8 x i64> %new_add2715, <8 x i64> poison, <4 x i32> <i32 0, i32 1, i32 2, i32 3>
  %offsets_2.i3426 = shufflevector <8 x i64> %new_add2715, <8 x i64> poison, <4 x i32> <i32 4, i32 5, i32 6, i32 7>
  %v1_1.i3427 = tail call <4 x i32> @llvm.x86.avx2.gather.q.d.256(<4 x i32> undef, ptr %HashIndex_ptr, <4 x i64> %offsets_1.i3425, <4 x i32> %vecmask_1.i3423, i8 1)
  %v2_1.i3428 = tail call <4 x i32> @llvm.x86.avx2.gather.q.d.256(<4 x i32> undef, ptr %HashIndex_ptr, <4 x i64> %offsets_2.i3426, <4 x i32> %vecmask_2.i3424, i8 1)
  %v.i3429 = shufflevector <4 x i32> %v1_1.i3427, <4 x i32> %v2_1.i3428, <8 x i32> <i32 0, i32 1, i32 2, i32 3, i32 4, i32 5, i32 6, i32 7>
  %val.i3430 = bitcast <8 x i32> %v.i3429 to <8 x float>
  call void @llvm.x86.avx.maskstore.ps.256(ptr %ptr2580, <8 x i32> %"oldMask&test826", <8 x float> %val.i3430)
  br label %common.ret
}

; Function Attrs: nocallback nofree nosync nounwind willreturn memory(read)
declare <8 x i32> @llvm.x86.avx2.gather.d.d.256(<8 x i32>, ptr, <8 x i32>, <8 x i32>, i8 immarg) #1

; Function Attrs: nocallback nofree nosync nounwind willreturn memory(read)
declare <4 x i32> @llvm.x86.avx2.gather.q.d.256(<4 x i32>, ptr, <4 x i64>, <4 x i32>, i8 immarg) #1

; Function Attrs: nocallback nofree nosync nounwind willreturn memory(read)
declare <8 x float> @llvm.x86.avx2.gather.d.ps.256(<8 x float>, ptr, <8 x i32>, <8 x float>, i8 immarg) #1

; Function Attrs: nocallback nofree nosync nounwind willreturn memory(none)
declare <8 x float> @llvm.x86.avx.blendv.ps.256(<8 x float>, <8 x float>, <8 x float>) #2

; Function Attrs: nocallback nofree nosync nounwind willreturn memory(none)
declare <8 x float> @llvm.x86.avx.round.ps.256(<8 x float>, i32 immarg) #2

; Function Attrs: nounwind memory(argmem: readwrite)
declare void @llvm.x86.avx.maskstore.ps.256(ptr, <8 x i32>, <8 x float>) #3

; Function Attrs: nocallback nofree nosync nounwind speculatable willreturn memory(none)
declare i32 @llvm.smin.i32(i32, i32) #4

; Function Attrs: nocallback nofree nosync nounwind willreturn memory(argmem: write)
declare void @llvm.masked.store.v8f32.p0(<8 x float>, ptr captures(none), i32 immarg, <8 x i1>) #5

; Function Attrs: nocallback nofree nosync nounwind speculatable willreturn memory(none)
declare <8 x i32> @llvm.smax.v8i32(<8 x i32>, <8 x i32>) #4

; Function Attrs: nocallback nofree nosync nounwind speculatable willreturn memory(none)
declare <8 x i32> @llvm.smin.v8i32(<8 x i32>, <8 x i32>) #4

attributes #0 = { nounwind uwtable }
attributes #1 = { nocallback nofree nosync nounwind willreturn memory(read) }
attributes #2 = { nocallback nofree nosync nounwind willreturn memory(none) }
attributes #3 = { nounwind memory(argmem: readwrite) }
attributes #4 = { nocallback nofree nosync nounwind speculatable willreturn memory(none) }
attributes #5 = { nocallback nofree nosync nounwind willreturn memory(argmem: write) }

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
!11 = !{!"src/EntJoySample/NativeTranspiler_Generated/SharpNative_Job__global_namespace__GridSearch2D_ClosestPointJobPointer_Execute_Batch.ispc"}
!12 = !{i32 21}
!13 = !{i32 1}
!14 = !{i32 19}
!15 = distinct !{!15, !9}
!16 = distinct !{!16, !9}
!17 = distinct !{!17, !9}
!18 = distinct !{!18, !9}
!19 = !{i32 65}
!20 = !{i32 22}
!21 = !{i32 44}
!22 = distinct !{!22, !9}
!23 = distinct !{!23, !9}
!24 = distinct !{!24, !9}
!25 = distinct !{!25, !9}
!26 = distinct !{!26, !9}
!27 = distinct !{!27, !9}
!28 = distinct !{!28, !9}
!29 = distinct !{!29, !9}
!30 = distinct !{!30, !9}
!31 = distinct !{!31, !9}
!32 = distinct !{!32, !9}
!33 = distinct !{!33, !9}
!34 = distinct !{!34, !9}
!35 = distinct !{!35, !9}
!36 = !{i32 90}
!37 = distinct !{!37, !9}
!38 = distinct !{!38, !9}
!39 = distinct !{!39, !9}
!40 = distinct !{!40, !9}
!41 = !{i32 134}
!42 = distinct !{!42, !9}
!43 = distinct !{!43, !9}
!44 = distinct !{!44, !9}
!45 = distinct !{!45, !9}
!46 = distinct !{!46, !9}
!47 = distinct !{!47, !9}
!48 = distinct !{!48, !9}
!49 = distinct !{!49, !9}
!50 = distinct !{!50, !9}
!51 = distinct !{!51, !9}
!52 = distinct !{!52, !9}
!53 = distinct !{!53, !9}
!54 = distinct !{!54, !9}
!55 = distinct !{!55, !9}
!56 = distinct !{!56, !9}
!57 = distinct !{!57, !9}
!58 = distinct !{!58, !9}
!59 = distinct !{!59, !9}
!60 = distinct !{!60, !9}
!61 = distinct !{!61, !9}
!62 = distinct !{!62, !9}
!63 = distinct !{!63, !9}
!64 = distinct !{!64, !9}
!65 = distinct !{!65, !9}
!66 = distinct !{!66, !9}
!67 = distinct !{!67, !9}
!68 = distinct !{!68, !9}
!69 = distinct !{!69, !9}
!70 = distinct !{!70, !9}
!71 = distinct !{!71, !9}
