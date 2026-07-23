; ModuleID = 'Case1_SimpleArith/SimpleArith.ispc'
source_filename = "Case1_SimpleArith/SimpleArith.ispc"
target datalayout = "e-m:w-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-f80:128-n8:16:32:64-S128"
target triple = "x86_64-pc-windows-msvc"

; Function Attrs: nounwind uwtable
define void @SimpleArith_ISPC_Impl___un_3C_unf_3E_un_3C_unf_3E_un_3C_unf_3E_un_3C_unf_3E_uni(ptr noalias %a, ptr noalias %b, ptr noalias %c, ptr noalias captures(none) %result, i32 %count, <8 x i32> %__mask) local_unnamed_addr #0 {
allocas:
  %nextras = srem i32 %count, 8
  %aligned_end = sub nsw i32 %count, %nextras
  %before_aligned_end19205 = icmp sgt i32 %aligned_end, 0
  br i1 %before_aligned_end19205, label %foreach_full_body.lr.ph, label %partial_inner_all_outer, !llvm.loop !8

foreach_full_body.lr.ph:                          ; preds = %allocas
  %0 = zext nneg i32 %aligned_end to i64
  br label %foreach_full_body, !llvm.loop !8

foreach_full_body:                                ; preds = %foreach_full_body.lr.ph, %foreach_full_body
  %indvars.iv = phi i64 [ 0, %foreach_full_body.lr.ph ], [ %indvars.iv.next, %foreach_full_body ]
  %1 = shl nuw nsw i64 %indvars.iv, 2
  %ptr = getelementptr i8, ptr %a, i64 %1, !filename !10, !first_line !11, !first_column !12, !last_line !11, !last_column !13
  %ptr_masked_load154 = load <8 x float>, ptr %ptr, align 4, !filename !10, !first_line !11, !first_column !12, !last_line !11, !last_column !13
  %ptr160 = getelementptr i8, ptr %b, i64 %1, !filename !10, !first_line !11, !first_column !14, !last_line !11, !last_column !15
  %ptr160_masked_load161 = load <8 x float>, ptr %ptr160, align 4, !filename !10, !first_line !11, !first_column !14, !last_line !11, !last_column !15
  %mul_a_load_offset_load_b_load_offset_load = fmul <8 x float> %ptr_masked_load154, %ptr160_masked_load161
  %ptr167 = getelementptr i8, ptr %c, i64 %1, !filename !10, !first_line !11, !first_column !16, !last_line !11, !last_column !17
  %ptr167_masked_load168 = load <8 x float>, ptr %ptr167, align 4, !filename !10, !first_line !11, !first_column !16, !last_line !11, !last_column !17
  %add_mul_a_load_offset_load_b_load_offset_load_c_load_offset_load = fadd <8 x float> %mul_a_load_offset_load_b_load_offset_load, %ptr167_masked_load168
  %ptr171 = getelementptr i8, ptr %result, i64 %1
  store <8 x float> %add_mul_a_load_offset_load_b_load_offset_load_c_load_offset_load, ptr %ptr171, align 4, !filename !10, !first_line !11, !first_column !18, !last_line !11, !last_column !19
  %indvars.iv.next = add nuw nsw i64 %indvars.iv, 8
  %before_aligned_end19 = icmp samesign ult i64 %indvars.iv.next, %0
  br i1 %before_aligned_end19, label %foreach_full_body, label %outer_not_in_extras.partial_inner_all_outer_crit_edge, !llvm.loop !8

foreach_reset:                                    ; preds = %partial_inner_only, %partial_inner_all_outer
  ret void

outer_not_in_extras.partial_inner_all_outer_crit_edge: ; preds = %foreach_full_body
  %2 = trunc nuw nsw i64 %indvars.iv.next to i32
  br label %partial_inner_all_outer, !llvm.loop !8

partial_inner_all_outer:                          ; preds = %outer_not_in_extras.partial_inner_all_outer_crit_edge, %allocas
  %counter.1.lcssa = phi i32 [ %2, %outer_not_in_extras.partial_inner_all_outer_crit_edge ], [ 0, %allocas ]
  %before_full_end = icmp slt i32 %counter.1.lcssa, %count
  br i1 %before_full_end, label %partial_inner_only, label %foreach_reset

partial_inner_only:                               ; preds = %partial_inner_all_outer
  %smear_counter_init56 = insertelement <8 x i32> poison, i32 %counter.1.lcssa, i64 0
  %smear_counter57 = shufflevector <8 x i32> %smear_counter_init56, <8 x i32> poison, <8 x i32> zeroinitializer
  %iter_val58 = or disjoint <8 x i32> %smear_counter57, <i32 0, i32 1, i32 2, i32 3, i32 4, i32 5, i32 6, i32 7>
  %smear_end_init59 = insertelement <8 x i32> poison, i32 %count, i64 0
  %smear_end60 = shufflevector <8 x i32> %smear_end_init59, <8 x i32> poison, <8 x i32> zeroinitializer
  %cmp61 = icmp slt <8 x i32> %iter_val58, %smear_end60
  %mul__i_load66.elt0 = shl nsw i32 %counter.1.lcssa, 2
  %3 = zext nneg i32 %mul__i_load66.elt0 to i64
  %ptr175 = getelementptr i8, ptr %a, i64 %3
  %floatval.i.i = tail call <8 x float> @llvm.masked.load.v8f32.p0(ptr %ptr175, i32 1, <8 x i1> %cmp61, <8 x float> zeroinitializer)
  %ptr183 = getelementptr i8, ptr %b, i64 %3
  %floatval.i.i201 = tail call <8 x float> @llvm.masked.load.v8f32.p0(ptr %ptr183, i32 1, <8 x i1> %cmp61, <8 x float> zeroinitializer)
  %mul_a_load70_offset_load_b_load77_offset_load = fmul <8 x float> %floatval.i.i, %floatval.i.i201
  %ptr191 = getelementptr i8, ptr %c, i64 %3
  %floatval.i.i203 = tail call <8 x float> @llvm.masked.load.v8f32.p0(ptr %ptr191, i32 1, <8 x i1> %cmp61, <8 x float> zeroinitializer)
  %add_mul_a_load70_offset_load_b_load77_offset_load_c_load84_offset_load = fadd <8 x float> %mul_a_load70_offset_load_b_load77_offset_load, %floatval.i.i203
  %ptr200 = getelementptr i8, ptr %result, i64 %3
  call void @llvm.masked.store.v8f32.p0(<8 x float> %add_mul_a_load70_offset_load_b_load77_offset_load_c_load84_offset_load, ptr %ptr200, i32 1, <8 x i1> %cmp61)
  br label %foreach_reset
}

; Function Attrs: nounwind uwtable
define void @SimpleArith_ISPC_Impl(ptr noalias %a, ptr noalias %b, ptr noalias %c, ptr noalias captures(none) %result, i32 %count) local_unnamed_addr #0 {
allocas:
  %nextras = srem i32 %count, 8
  %aligned_end = sub nsw i32 %count, %nextras
  %before_aligned_end10156 = icmp sgt i32 %aligned_end, 0
  br i1 %before_aligned_end10156, label %foreach_full_body.lr.ph, label %partial_inner_all_outer, !llvm.loop !20

foreach_full_body.lr.ph:                          ; preds = %allocas
  %0 = zext nneg i32 %aligned_end to i64
  br label %foreach_full_body, !llvm.loop !20

foreach_full_body:                                ; preds = %foreach_full_body.lr.ph, %foreach_full_body
  %indvars.iv = phi i64 [ 0, %foreach_full_body.lr.ph ], [ %indvars.iv.next, %foreach_full_body ]
  %1 = shl nuw nsw i64 %indvars.iv, 2
  %ptr = getelementptr i8, ptr %a, i64 %1, !filename !10, !first_line !11, !first_column !12, !last_line !11, !last_column !13
  %ptr_masked_load105 = load <8 x float>, ptr %ptr, align 4, !filename !10, !first_line !11, !first_column !12, !last_line !11, !last_column !13
  %ptr111 = getelementptr i8, ptr %b, i64 %1, !filename !10, !first_line !11, !first_column !14, !last_line !11, !last_column !15
  %ptr111_masked_load112 = load <8 x float>, ptr %ptr111, align 4, !filename !10, !first_line !11, !first_column !14, !last_line !11, !last_column !15
  %mul_a_load_offset_load_b_load_offset_load = fmul <8 x float> %ptr_masked_load105, %ptr111_masked_load112
  %ptr118 = getelementptr i8, ptr %c, i64 %1, !filename !10, !first_line !11, !first_column !16, !last_line !11, !last_column !17
  %ptr118_masked_load119 = load <8 x float>, ptr %ptr118, align 4, !filename !10, !first_line !11, !first_column !16, !last_line !11, !last_column !17
  %add_mul_a_load_offset_load_b_load_offset_load_c_load_offset_load = fadd <8 x float> %mul_a_load_offset_load_b_load_offset_load, %ptr118_masked_load119
  %ptr122 = getelementptr i8, ptr %result, i64 %1
  store <8 x float> %add_mul_a_load_offset_load_b_load_offset_load_c_load_offset_load, ptr %ptr122, align 4, !filename !10, !first_line !11, !first_column !18, !last_line !11, !last_column !19
  %indvars.iv.next = add nuw nsw i64 %indvars.iv, 8
  %before_aligned_end10 = icmp samesign ult i64 %indvars.iv.next, %0
  br i1 %before_aligned_end10, label %foreach_full_body, label %outer_not_in_extras.partial_inner_all_outer_crit_edge, !llvm.loop !20

foreach_reset:                                    ; preds = %partial_inner_only, %partial_inner_all_outer
  ret void

outer_not_in_extras.partial_inner_all_outer_crit_edge: ; preds = %foreach_full_body
  %2 = trunc nuw nsw i64 %indvars.iv.next to i32
  br label %partial_inner_all_outer, !llvm.loop !20

partial_inner_all_outer:                          ; preds = %outer_not_in_extras.partial_inner_all_outer_crit_edge, %allocas
  %counter.1.lcssa = phi i32 [ %2, %outer_not_in_extras.partial_inner_all_outer_crit_edge ], [ 0, %allocas ]
  %before_full_end = icmp slt i32 %counter.1.lcssa, %count
  br i1 %before_full_end, label %partial_inner_only, label %foreach_reset

partial_inner_only:                               ; preds = %partial_inner_all_outer
  %smear_counter_init35 = insertelement <8 x i32> poison, i32 %counter.1.lcssa, i64 0
  %smear_counter36 = shufflevector <8 x i32> %smear_counter_init35, <8 x i32> poison, <8 x i32> zeroinitializer
  %iter_val37 = or disjoint <8 x i32> %smear_counter36, <i32 0, i32 1, i32 2, i32 3, i32 4, i32 5, i32 6, i32 7>
  %smear_end_init38 = insertelement <8 x i32> poison, i32 %count, i64 0
  %smear_end39 = shufflevector <8 x i32> %smear_end_init38, <8 x i32> poison, <8 x i32> zeroinitializer
  %cmp40 = icmp slt <8 x i32> %iter_val37, %smear_end39
  %mul__i_load42.elt0 = shl nsw i32 %counter.1.lcssa, 2
  %3 = zext nneg i32 %mul__i_load42.elt0 to i64
  %ptr126 = getelementptr i8, ptr %a, i64 %3
  %floatval.i.i = tail call <8 x float> @llvm.masked.load.v8f32.p0(ptr %ptr126, i32 1, <8 x i1> %cmp40, <8 x float> zeroinitializer)
  %ptr134 = getelementptr i8, ptr %b, i64 %3
  %floatval.i.i152 = tail call <8 x float> @llvm.masked.load.v8f32.p0(ptr %ptr134, i32 1, <8 x i1> %cmp40, <8 x float> zeroinitializer)
  %mul_a_load46_offset_load_b_load51_offset_load = fmul <8 x float> %floatval.i.i, %floatval.i.i152
  %ptr142 = getelementptr i8, ptr %c, i64 %3
  %floatval.i.i154 = tail call <8 x float> @llvm.masked.load.v8f32.p0(ptr %ptr142, i32 1, <8 x i1> %cmp40, <8 x float> zeroinitializer)
  %add_mul_a_load46_offset_load_b_load51_offset_load_c_load56_offset_load = fadd <8 x float> %mul_a_load46_offset_load_b_load51_offset_load, %floatval.i.i154
  %ptr151 = getelementptr i8, ptr %result, i64 %3
  call void @llvm.masked.store.v8f32.p0(<8 x float> %add_mul_a_load46_offset_load_b_load51_offset_load_c_load56_offset_load, ptr %ptr151, i32 1, <8 x i1> %cmp40)
  br label %foreach_reset
}

; Function Attrs: nocallback nofree nosync nounwind willreturn memory(argmem: read)
declare <8 x float> @llvm.masked.load.v8f32.p0(ptr captures(none), i32 immarg, <8 x i1>, <8 x float>) #1

; Function Attrs: nocallback nofree nosync nounwind willreturn memory(argmem: write)
declare void @llvm.masked.store.v8f32.p0(<8 x float>, ptr captures(none), i32 immarg, <8 x i1>) #2

attributes #0 = { nounwind uwtable }
attributes #1 = { nocallback nofree nosync nounwind willreturn memory(argmem: read) }
attributes #2 = { nocallback nofree nosync nounwind willreturn memory(argmem: write) }

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
!10 = !{!"Case1_SimpleArith/SimpleArith.ispc"}
!11 = !{i32 12}
!12 = !{i32 21}
!13 = !{i32 25}
!14 = !{i32 28}
!15 = !{i32 32}
!16 = !{i32 35}
!17 = !{i32 39}
!18 = !{i32 9}
!19 = !{i32 18}
!20 = distinct !{!20, !9}
