; ModuleID = 'Case2_MathFunctions/MathFunctions.ispc'
source_filename = "Case2_MathFunctions/MathFunctions.ispc"
target datalayout = "e-m:w-p270:32:32-p271:32:32-p272:64:64-i64:64-i128:128-f80:128-n8:16:32:64-S128"
target triple = "x86_64-pc-windows-msvc"

; Function Attrs: nounwind uwtable
define void @MathFuncs_ISPC_Impl___un_3C_unf_3E_un_3C_unf_3E_uni(ptr noalias %a, ptr noalias captures(none) %result, i32 %count, <8 x i32> %__mask) local_unnamed_addr #0 {
allocas:
  %nextras = srem i32 %count, 8
  %aligned_end = sub nsw i32 %count, %nextras
  %before_aligned_end17427 = icmp sgt i32 %aligned_end, 0
  br i1 %before_aligned_end17427, label %foreach_full_body.lr.ph, label %partial_inner_all_outer, !llvm.loop !8

foreach_full_body.lr.ph:                          ; preds = %allocas
  %0 = zext nneg i32 %aligned_end to i64
  br label %foreach_full_body, !llvm.loop !8

foreach_full_body:                                ; preds = %foreach_full_body.lr.ph, %foreach_full_body
  %indvars.iv = phi i64 [ 0, %foreach_full_body.lr.ph ], [ %indvars.iv.next, %foreach_full_body ]
  %1 = shl nuw nsw i64 %indvars.iv, 2
  %ptr = getelementptr i8, ptr %a, i64 %1, !filename !10, !first_line !11, !first_column !12, !last_line !11, !last_column !13
  %ptr_masked_load159 = load <8 x float>, ptr %ptr, align 4, !filename !10, !first_line !11, !first_column !12, !last_line !11, !last_column !13
  %2 = tail call <8 x float> @llvm.sqrt.v8f32(<8 x float> %ptr_masked_load159)
  %mul_x_full_load51_.i = fmul <8 x float> %ptr_masked_load159, splat (float 0x3FE45F3060000000)
  %call.i.i.i = tail call <8 x float> @llvm.x86.avx.round.ps.256(<8 x float> %mul_x_full_load51_.i, i32 9)
  %k_real_load_to_int32.i = fptosi <8 x float> %call.i.i.i to <8 x i32>
  %mul_k_real_load56_.i = fmul <8 x float> %call.i.i.i, splat (float 0x3FF921FB60000000)
  %sub_x_full_load55_mul_k_real_load56_.i = fsub <8 x float> %ptr_masked_load159, %mul_k_real_load56_.i
  %bitop.i = and <8 x i32> %k_real_load_to_int32.i, splat (i32 2)
  %greater_k_mod4_load59_.i.not = icmp eq <8 x i32> %bitop.i, zeroinitializer
  %3 = and <8 x i32> %k_real_load_to_int32.i, splat (i32 1)
  %sin_usecos_load_toMaskBool.i = sub nsw <8 x i32> zeroinitializer, %3
  %mask_as_float.i.i = bitcast <8 x i32> %sin_usecos_load_toMaskBool.i to <8 x float>
  %blend.i.i = call <8 x float> @llvm.x86.avx.blendv.ps.256(<8 x float> %sub_x_full_load55_mul_k_real_load56_.i, <8 x float> splat (float 1.000000e+00), <8 x float> %mask_as_float.i.i)
  %blend.i.i308 = call <8 x float> @llvm.x86.avx.blendv.ps.256(<8 x float> splat (float 0xBFC5555560000000), <8 x float> splat (float -5.000000e-01), <8 x float> %mask_as_float.i.i)
  %blend.i.i311 = call <8 x float> @llvm.x86.avx.blendv.ps.256(<8 x float> splat (float 0x3F81111300000000), <8 x float> splat (float 0x3FA5555480000000), <8 x float> %mask_as_float.i.i)
  %blend.i.i314 = call <8 x float> @llvm.x86.avx.blendv.ps.256(<8 x float> splat (float 0xBF2A0212C0000000), <8 x float> splat (float 0xBF56C13020000000), <8 x float> %mask_as_float.i.i)
  %blend.i.i317 = call <8 x float> @llvm.x86.avx.blendv.ps.256(<8 x float> splat (float 0x3EC7271500000000), <8 x float> splat (float 0x3EF9F57380000000), <8 x float> %mask_as_float.i.i)
  %blend.i.i320 = call <8 x float> @llvm.x86.avx.blendv.ps.256(<8 x float> splat (float 0xBE5AE00260000000), <8 x float> splat (float 0xBE916C69A0000000), <8 x float> %mask_as_float.i.i)
  %mul_x_load155_x_load156.i = fmul <8 x float> %sub_x_full_load55_mul_k_real_load56_.i, %sub_x_full_load55_mul_k_real_load56_.i
  %mul_x2_load_c10_load.i = fmul <8 x float> %mul_x_load155_x_load156.i, %blend.i.i320
  %add_mul_x2_load_c10_load_c8_load.i = fadd <8 x float> %blend.i.i317, %mul_x2_load_c10_load.i
  %mul_x2_load157_formula_load.i = fmul <8 x float> %mul_x_load155_x_load156.i, %add_mul_x2_load_c10_load_c8_load.i
  %add_mul_x2_load157_formula_load_c6_load.i = fadd <8 x float> %blend.i.i314, %mul_x2_load157_formula_load.i
  %mul_x2_load158_formula_load159.i = fmul <8 x float> %mul_x_load155_x_load156.i, %add_mul_x2_load157_formula_load_c6_load.i
  %add_mul_x2_load158_formula_load159_c4_load.i = fadd <8 x float> %blend.i.i311, %mul_x2_load158_formula_load159.i
  %mul_x2_load160_formula_load161.i = fmul <8 x float> %mul_x_load155_x_load156.i, %add_mul_x2_load158_formula_load159_c4_load.i
  %add_mul_x2_load160_formula_load161_c2_load.i = fadd <8 x float> %blend.i.i308, %mul_x2_load160_formula_load161.i
  %mul_x2_load162_formula_load163.i = fmul <8 x float> %mul_x_load155_x_load156.i, %add_mul_x2_load160_formula_load161_c2_load.i
  %add_mul_x2_load162_formula_load163_.i = fadd <8 x float> %mul_x2_load162_formula_load163.i, splat (float 1.000000e+00)
  %mul_formula_load165_outside_load.i = fmul <8 x float> %blend.i.i, %add_mul_x2_load162_formula_load163_.i
  %formula_load172_negate.i = fneg <8 x float> %mul_formula_load165_outside_load.i
  %blend.i.i323 = select <8 x i1> %greater_k_mod4_load59_.i.not, <8 x float> %mul_formula_load165_outside_load.i, <8 x float> %formula_load172_negate.i
  %bitop.i182 = and <8 x i32> %k_real_load_to_int32.i, splat (i32 3)
  %logical_or5898.i = icmp eq <8 x i32> %3, zeroinitializer
  %4 = add nsw <8 x i32> %bitop.i182, splat (i32 -1)
  %logical_or6199.i = icmp ult <8 x i32> %4, splat (i32 2)
  %blend.i.i326 = select <8 x i1> %logical_or5898.i, <8 x float> splat (float 1.000000e+00), <8 x float> %sub_x_full_load55_mul_k_real_load56_.i
  %blend.i.i329 = select <8 x i1> %logical_or5898.i, <8 x float> splat (float -5.000000e-01), <8 x float> splat (float 0xBFC5555560000000)
  %blend.i.i332 = select <8 x i1> %logical_or5898.i, <8 x float> splat (float 0x3FA5555480000000), <8 x float> splat (float 0x3F81111300000000)
  %blend.i.i335 = select <8 x i1> %logical_or5898.i, <8 x float> splat (float 0xBF56C13020000000), <8 x float> splat (float 0xBF2A0212C0000000)
  %blend.i.i338 = select <8 x i1> %logical_or5898.i, <8 x float> splat (float 0x3EF9F57380000000), <8 x float> splat (float 0x3EC7271500000000)
  %blend.i.i341 = select <8 x i1> %logical_or5898.i, <8 x float> splat (float 0xBE916C69A0000000), <8 x float> splat (float 0xBE5AE00260000000)
  %mul_x2_load_c10_load.i184 = fmul <8 x float> %mul_x_load155_x_load156.i, %blend.i.i341
  %add_mul_x2_load_c10_load_c8_load.i185 = fadd <8 x float> %blend.i.i338, %mul_x2_load_c10_load.i184
  %mul_x2_load159_formula_load.i = fmul <8 x float> %mul_x_load155_x_load156.i, %add_mul_x2_load_c10_load_c8_load.i185
  %add_mul_x2_load159_formula_load_c6_load.i = fadd <8 x float> %blend.i.i335, %mul_x2_load159_formula_load.i
  %mul_x2_load160_formula_load161.i186 = fmul <8 x float> %mul_x_load155_x_load156.i, %add_mul_x2_load159_formula_load_c6_load.i
  %add_mul_x2_load160_formula_load161_c4_load.i = fadd <8 x float> %blend.i.i332, %mul_x2_load160_formula_load161.i186
  %mul_x2_load162_formula_load163.i187 = fmul <8 x float> %mul_x_load155_x_load156.i, %add_mul_x2_load160_formula_load161_c4_load.i
  %add_mul_x2_load162_formula_load163_c2_load.i = fadd <8 x float> %blend.i.i329, %mul_x2_load162_formula_load163.i187
  %mul_x2_load164_formula_load165.i = fmul <8 x float> %mul_x_load155_x_load156.i, %add_mul_x2_load162_formula_load163_c2_load.i
  %add_mul_x2_load164_formula_load165_.i = fadd <8 x float> %mul_x2_load164_formula_load165.i, splat (float 1.000000e+00)
  %mul_formula_load167_outside_load.i = fmul <8 x float> %blend.i.i326, %add_mul_x2_load164_formula_load165_.i
  %formula_load174_negate.i = fneg <8 x float> %mul_formula_load167_outside_load.i
  %blend.i.i344 = select <8 x i1> %logical_or6199.i, <8 x float> %formula_load174_negate.i, <8 x float> %mul_formula_load167_outside_load.i
  %mul_calltmp37_calltmp41 = fmul <8 x float> %blend.i.i344, %blend.i.i323
  %add_calltmp_mul_calltmp37_calltmp41 = fadd <8 x float> %2, %mul_calltmp37_calltmp41
  %add_x_load42_ = fadd <8 x float> %ptr_masked_load159, splat (float 1.000000e+00)
  %5 = bitcast <8 x float> %add_x_load42_ to <8 x i32>
  %bitop8.i.i = and <8 x i32> %5, splat (i32 -2139095041)
  %bitop.i.i = lshr <8 x i32> %5, splat (i32 23)
  %bitop10.i.i = and <8 x i32> %bitop.i.i, splat (i32 255)
  %sub_bitop10_.i.i = add nsw <8 x i32> %bitop10.i.i, splat (i32 -126)
  %bitop15.i.i = or disjoint <8 x i32> %bitop8.i.i, splat (i32 1056964608)
  %6 = bitcast <8 x i32> %bitop15.i.i to <8 x float>
  %greater__x_full_load54.i = fcmp olt <8 x float> %6, splat (float 0x3FE6A09E60000000)
  %blend.i = select <8 x i1> %greater__x_full_load54.i, <8 x float> splat (float 0xFFFFFFFFE0000000), <8 x float> zeroinitializer
  %7 = bitcast <8 x float> %blend.i to <8 x i32>
  %add_e_load_x_smaller_SQRTHF_load.i = add nsw <8 x i32> %sub_bitop10_.i.i, %7
  %bitop.i190 = and <8 x i32> %bitop15.i.i, %7
  %8 = bitcast <8 x i32> %bitop.i190 to <8 x float>
  %sub_calltmp75_.i = fadd <8 x float> %8, splat (float -1.000000e+00)
  %add_x_full1_load_sub_calltmp75_.i = fadd <8 x float> %sub_calltmp75_.i, %6
  %mul_x_full_load77_x_full_load78.i = fmul <8 x float> %add_x_full1_load_sub_calltmp75_.i, %add_x_full1_load_sub_calltmp75_.i
  %mul__x_full_load79.i = fmul <8 x float> %add_x_full1_load_sub_calltmp75_.i, splat (float 0x3FB2043760000000)
  %add_mul__x_full_load79_.i = fadd <8 x float> %mul__x_full_load79.i, splat (float 0xBFBD7A3700000000)
  %mul_add_mul__x_full_load79__x_full_load80.i = fmul <8 x float> %add_x_full1_load_sub_calltmp75_.i, %add_mul__x_full_load79_.i
  %add_mul_add_mul__x_full_load79__x_full_load80_.i = fadd <8 x float> %mul_add_mul__x_full_load79__x_full_load80.i, splat (float 0x3FBDE4A340000000)
  %mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81.i = fmul <8 x float> %add_x_full1_load_sub_calltmp75_.i, %add_mul_add_mul__x_full_load79__x_full_load80_.i
  %add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81_.i = fadd <8 x float> %mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81.i, splat (float 0xBFBFCBA9E0000000)
  %mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82.i = fmul <8 x float> %add_x_full1_load_sub_calltmp75_.i, %add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81_.i
  %add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82_.i = fadd <8 x float> %mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82.i, splat (float 0x3FC23D37E0000000)
  %mul_add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82__x_full_load83.i = fmul <8 x float> %add_x_full1_load_sub_calltmp75_.i, %add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82_.i
  %add_mul_add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82__x_full_load83_.i = fadd <8 x float> %mul_add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82__x_full_load83.i, splat (float 0xBFC555CA00000000)
  %mul_add_mul_add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82__x_full_load83__x_full_load84.i = fmul <8 x float> %add_x_full1_load_sub_calltmp75_.i, %add_mul_add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82__x_full_load83_.i
  %add_mul_add_mul_add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82__x_full_load83__x_full_load84_.i = fadd <8 x float> %mul_add_mul_add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82__x_full_load83__x_full_load84.i, splat (float 0x3FC999D580000000)
  %mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82__x_full_load83__x_full_load84__x_full_load85.i = fmul <8 x float> %add_x_full1_load_sub_calltmp75_.i, %add_mul_add_mul_add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82__x_full_load83__x_full_load84_.i
  %add_mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82__x_full_load83__x_full_load84__x_full_load85_.i = fadd <8 x float> %mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82__x_full_load83__x_full_load84__x_full_load85.i, splat (float 0xBFCFFFFF80000000)
  %mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82__x_full_load83__x_full_load84__x_full_load85__x_full_load86.i = fmul <8 x float> %add_x_full1_load_sub_calltmp75_.i, %add_mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82__x_full_load83__x_full_load84__x_full_load85_.i
  %add_mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82__x_full_load83__x_full_load84__x_full_load85__x_full_load86_.i = fadd <8 x float> %mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82__x_full_load83__x_full_load84__x_full_load85__x_full_load86.i, splat (float 0x3FD5555540000000)
  %mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82__x_full_load83__x_full_load84__x_full_load85__x_full_load86__x_full_load87.i = fmul <8 x float> %add_x_full1_load_sub_calltmp75_.i, %add_mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82__x_full_load83__x_full_load84__x_full_load85__x_full_load86_.i
  %mul_mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82__x_full_load83__x_full_load84__x_full_load85__x_full_load86__x_full_load87_z_load.i = fmul <8 x float> %mul_x_full_load77_x_full_load78.i, %mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82__x_full_load83__x_full_load84__x_full_load85__x_full_load86__x_full_load87.i
  %e_load88_to_float.i = sitofp <8 x i32> %add_e_load_x_smaller_SQRTHF_load.i to <8 x float>
  %mul_fe_load_.i = fmul <8 x float> %e_load88_to_float.i, splat (float 0x3F2BD01060000000)
  %9 = fsub <8 x float> %mul_mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82__x_full_load83__x_full_load84__x_full_load85__x_full_load86__x_full_load87_z_load.i, %mul_fe_load_.i
  %mul__z_load90.i = fmul <8 x float> %mul_x_full_load77_x_full_load78.i, splat (float 5.000000e-01)
  %sub_y_load92_mul__z_load90.i = fsub <8 x float> %9, %mul__z_load90.i
  %add_x_full_load93_y_load94.i = fadd <8 x float> %add_x_full1_load_sub_calltmp75_.i, %sub_y_load92_mul__z_load90.i
  %mul__fe_load96.i = fmul <8 x float> %e_load88_to_float.i, splat (float 0x3FE6300000000000)
  %add_z_load95_mul__fe_load96.i = fadd <8 x float> %mul__fe_load96.i, %add_x_full_load93_y_load94.i
  %add_add_calltmp_mul_calltmp37_calltmp41_calltmp45 = fadd <8 x float> %add_calltmp_mul_calltmp37_calltmp41, %add_z_load95_mul__fe_load96.i
  %ptr162 = getelementptr i8, ptr %result, i64 %1
  store <8 x float> %add_add_calltmp_mul_calltmp37_calltmp41_calltmp45, ptr %ptr162, align 4, !filename !10, !first_line !14, !first_column !11, !last_line !14, !last_column !15
  %indvars.iv.next = add nuw nsw i64 %indvars.iv, 8
  %before_aligned_end17 = icmp samesign ult i64 %indvars.iv.next, %0
  br i1 %before_aligned_end17, label %foreach_full_body, label %outer_not_in_extras.partial_inner_all_outer_crit_edge, !llvm.loop !8

foreach_reset:                                    ; preds = %partial_inner_only, %partial_inner_all_outer
  ret void

outer_not_in_extras.partial_inner_all_outer_crit_edge: ; preds = %foreach_full_body
  %10 = trunc nuw nsw i64 %indvars.iv.next to i32
  br label %partial_inner_all_outer, !llvm.loop !8

partial_inner_all_outer:                          ; preds = %outer_not_in_extras.partial_inner_all_outer_crit_edge, %allocas
  %counter.1.lcssa = phi i32 [ %10, %outer_not_in_extras.partial_inner_all_outer_crit_edge ], [ 0, %allocas ]
  %before_full_end = icmp slt i32 %counter.1.lcssa, %count
  br i1 %before_full_end, label %partial_inner_only, label %foreach_reset

partial_inner_only:                               ; preds = %partial_inner_all_outer
  %smear_counter_init56 = insertelement <8 x i32> poison, i32 %counter.1.lcssa, i64 0
  %smear_counter57 = shufflevector <8 x i32> %smear_counter_init56, <8 x i32> poison, <8 x i32> zeroinitializer
  %iter_val58 = or disjoint <8 x i32> %smear_counter57, <i32 0, i32 1, i32 2, i32 3, i32 4, i32 5, i32 6, i32 7>
  %smear_end_init59 = insertelement <8 x i32> poison, i32 %count, i64 0
  %smear_end60 = shufflevector <8 x i32> %smear_end_init59, <8 x i32> poison, <8 x i32> zeroinitializer
  %cmp61 = icmp slt <8 x i32> %iter_val58, %smear_end60
  %mul__i_load67.elt0 = shl nsw i32 %counter.1.lcssa, 2
  %11 = zext nneg i32 %mul__i_load67.elt0 to i64
  %ptr166 = getelementptr i8, ptr %a, i64 %11
  %floatval.i.i = tail call <8 x float> @llvm.masked.load.v8f32.p0(ptr %ptr166, i32 1, <8 x i1> %cmp61, <8 x float> zeroinitializer)
  %12 = tail call <8 x float> @llvm.sqrt.v8f32(<8 x float> %floatval.i.i)
  %mul_x_full_load51_.i198 = fmul <8 x float> %floatval.i.i, splat (float 0x3FE45F3060000000)
  %call.i.i.i199 = tail call <8 x float> @llvm.x86.avx.round.ps.256(<8 x float> %mul_x_full_load51_.i198, i32 9)
  %k_real_load_to_int32.i200 = fptosi <8 x float> %call.i.i.i199 to <8 x i32>
  %mul_k_real_load56_.i201 = fmul <8 x float> %call.i.i.i199, splat (float 0x3FF921FB60000000)
  %sub_x_full_load55_mul_k_real_load56_.i202 = fsub <8 x float> %floatval.i.i, %mul_k_real_load56_.i201
  %bitop.i203 = and <8 x i32> %k_real_load_to_int32.i200, splat (i32 2)
  %greater_k_mod4_load59_.i204.not = icmp eq <8 x i32> %bitop.i203, zeroinitializer
  %13 = and <8 x i32> %k_real_load_to_int32.i200, splat (i32 1)
  %sin_usecos_load_toMaskBool.i206 = sub nsw <8 x i32> zeroinitializer, %13
  %mask_as_float.i.i345 = bitcast <8 x i32> %sin_usecos_load_toMaskBool.i206 to <8 x float>
  %blend.i.i347 = call <8 x float> @llvm.x86.avx.blendv.ps.256(<8 x float> %sub_x_full_load55_mul_k_real_load56_.i202, <8 x float> splat (float 1.000000e+00), <8 x float> %mask_as_float.i.i345)
  %blend.i.i350 = call <8 x float> @llvm.x86.avx.blendv.ps.256(<8 x float> splat (float 0xBFC5555560000000), <8 x float> splat (float -5.000000e-01), <8 x float> %mask_as_float.i.i345)
  %blend.i.i353 = call <8 x float> @llvm.x86.avx.blendv.ps.256(<8 x float> splat (float 0x3F81111300000000), <8 x float> splat (float 0x3FA5555480000000), <8 x float> %mask_as_float.i.i345)
  %blend.i.i356 = call <8 x float> @llvm.x86.avx.blendv.ps.256(<8 x float> splat (float 0xBF2A0212C0000000), <8 x float> splat (float 0xBF56C13020000000), <8 x float> %mask_as_float.i.i345)
  %blend.i.i359 = call <8 x float> @llvm.x86.avx.blendv.ps.256(<8 x float> splat (float 0x3EC7271500000000), <8 x float> splat (float 0x3EF9F57380000000), <8 x float> %mask_as_float.i.i345)
  %blend.i.i362 = call <8 x float> @llvm.x86.avx.blendv.ps.256(<8 x float> splat (float 0xBE5AE00260000000), <8 x float> splat (float 0xBE916C69A0000000), <8 x float> %mask_as_float.i.i345)
  %mul_x_load155_x_load156.i213 = fmul <8 x float> %sub_x_full_load55_mul_k_real_load56_.i202, %sub_x_full_load55_mul_k_real_load56_.i202
  %mul_x2_load_c10_load.i214 = fmul <8 x float> %mul_x_load155_x_load156.i213, %blend.i.i362
  %add_mul_x2_load_c10_load_c8_load.i215 = fadd <8 x float> %blend.i.i359, %mul_x2_load_c10_load.i214
  %mul_x2_load157_formula_load.i216 = fmul <8 x float> %mul_x_load155_x_load156.i213, %add_mul_x2_load_c10_load_c8_load.i215
  %add_mul_x2_load157_formula_load_c6_load.i217 = fadd <8 x float> %blend.i.i356, %mul_x2_load157_formula_load.i216
  %mul_x2_load158_formula_load159.i218 = fmul <8 x float> %mul_x_load155_x_load156.i213, %add_mul_x2_load157_formula_load_c6_load.i217
  %add_mul_x2_load158_formula_load159_c4_load.i219 = fadd <8 x float> %blend.i.i353, %mul_x2_load158_formula_load159.i218
  %mul_x2_load160_formula_load161.i220 = fmul <8 x float> %mul_x_load155_x_load156.i213, %add_mul_x2_load158_formula_load159_c4_load.i219
  %add_mul_x2_load160_formula_load161_c2_load.i221 = fadd <8 x float> %blend.i.i350, %mul_x2_load160_formula_load161.i220
  %mul_x2_load162_formula_load163.i222 = fmul <8 x float> %mul_x_load155_x_load156.i213, %add_mul_x2_load160_formula_load161_c2_load.i221
  %add_mul_x2_load162_formula_load163_.i223 = fadd <8 x float> %mul_x2_load162_formula_load163.i222, splat (float 1.000000e+00)
  %mul_formula_load165_outside_load.i224 = fmul <8 x float> %blend.i.i347, %add_mul_x2_load162_formula_load163_.i223
  %formula_load172_negate.i225 = fneg <8 x float> %mul_formula_load165_outside_load.i224
  %blend.i.i365 = select <8 x i1> %greater_k_mod4_load59_.i204.not, <8 x float> %mul_formula_load165_outside_load.i224, <8 x float> %formula_load172_negate.i225
  %bitop.i239 = and <8 x i32> %k_real_load_to_int32.i200, splat (i32 3)
  %logical_or5898.i240 = icmp eq <8 x i32> %13, zeroinitializer
  %14 = add nsw <8 x i32> %bitop.i239, splat (i32 -1)
  %logical_or6199.i241 = icmp ult <8 x i32> %14, splat (i32 2)
  %blend.i.i368 = select <8 x i1> %logical_or5898.i240, <8 x float> splat (float 1.000000e+00), <8 x float> %sub_x_full_load55_mul_k_real_load56_.i202
  %blend.i.i371 = select <8 x i1> %logical_or5898.i240, <8 x float> splat (float -5.000000e-01), <8 x float> splat (float 0xBFC5555560000000)
  %blend.i.i374 = select <8 x i1> %logical_or5898.i240, <8 x float> splat (float 0x3FA5555480000000), <8 x float> splat (float 0x3F81111300000000)
  %blend.i.i377 = select <8 x i1> %logical_or5898.i240, <8 x float> splat (float 0xBF56C13020000000), <8 x float> splat (float 0xBF2A0212C0000000)
  %blend.i.i380 = select <8 x i1> %logical_or5898.i240, <8 x float> splat (float 0x3EF9F57380000000), <8 x float> splat (float 0x3EC7271500000000)
  %blend.i.i383 = select <8 x i1> %logical_or5898.i240, <8 x float> splat (float 0xBE916C69A0000000), <8 x float> splat (float 0xBE5AE00260000000)
  %mul_x2_load_c10_load.i250 = fmul <8 x float> %mul_x_load155_x_load156.i213, %blend.i.i383
  %add_mul_x2_load_c10_load_c8_load.i251 = fadd <8 x float> %blend.i.i380, %mul_x2_load_c10_load.i250
  %mul_x2_load159_formula_load.i252 = fmul <8 x float> %mul_x_load155_x_load156.i213, %add_mul_x2_load_c10_load_c8_load.i251
  %add_mul_x2_load159_formula_load_c6_load.i253 = fadd <8 x float> %blend.i.i377, %mul_x2_load159_formula_load.i252
  %mul_x2_load160_formula_load161.i254 = fmul <8 x float> %mul_x_load155_x_load156.i213, %add_mul_x2_load159_formula_load_c6_load.i253
  %add_mul_x2_load160_formula_load161_c4_load.i255 = fadd <8 x float> %blend.i.i374, %mul_x2_load160_formula_load161.i254
  %mul_x2_load162_formula_load163.i256 = fmul <8 x float> %mul_x_load155_x_load156.i213, %add_mul_x2_load160_formula_load161_c4_load.i255
  %add_mul_x2_load162_formula_load163_c2_load.i257 = fadd <8 x float> %blend.i.i371, %mul_x2_load162_formula_load163.i256
  %mul_x2_load164_formula_load165.i258 = fmul <8 x float> %mul_x_load155_x_load156.i213, %add_mul_x2_load162_formula_load163_c2_load.i257
  %add_mul_x2_load164_formula_load165_.i259 = fadd <8 x float> %mul_x2_load164_formula_load165.i258, splat (float 1.000000e+00)
  %mul_formula_load167_outside_load.i260 = fmul <8 x float> %blend.i.i368, %add_mul_x2_load164_formula_load165_.i259
  %formula_load174_negate.i262 = fneg <8 x float> %mul_formula_load167_outside_load.i260
  %blend.i.i386 = select <8 x i1> %logical_or6199.i241, <8 x float> %formula_load174_negate.i262, <8 x float> %mul_formula_load167_outside_load.i260
  %mul_calltmp84_calltmp88 = fmul <8 x float> %blend.i.i386, %blend.i.i365
  %add_calltmp80_mul_calltmp84_calltmp88 = fadd <8 x float> %12, %mul_calltmp84_calltmp88
  %add_x_load89_ = fadd <8 x float> %floatval.i.i, splat (float 1.000000e+00)
  %15 = bitcast <8 x float> %add_x_load89_ to <8 x i32>
  %bitop8.i.i266 = and <8 x i32> %15, splat (i32 -2139095041)
  %bitop.i.i267 = lshr <8 x i32> %15, splat (i32 23)
  %bitop10.i.i268 = and <8 x i32> %bitop.i.i267, splat (i32 255)
  %sub_bitop10_.i.i269 = add nsw <8 x i32> %bitop10.i.i268, splat (i32 -126)
  %16 = select <8 x i1> %cmp61, <8 x i32> %sub_bitop10_.i.i269, <8 x i32> undef
  %bitop15.i.i270 = or disjoint <8 x i32> %bitop8.i.i266, splat (i32 1056964608)
  %17 = bitcast <8 x i32> %bitop15.i.i270 to <8 x float>
  %greater__x_full_load54.i271 = fcmp olt <8 x float> %17, splat (float 0x3FE6A09E60000000)
  %blend.i392 = select <8 x i1> %greater__x_full_load54.i271, <8 x float> splat (float 0xFFFFFFFFE0000000), <8 x float> zeroinitializer
  %18 = bitcast <8 x float> %blend.i392 to <8 x i32>
  %add_e_load_x_smaller_SQRTHF_load.i275 = add nsw <8 x i32> %16, %18
  %bitop.i276 = and <8 x i32> %bitop15.i.i270, %18
  %19 = bitcast <8 x i32> %bitop.i276 to <8 x float>
  %sub_calltmp75_.i277 = fadd <8 x float> %19, splat (float -1.000000e+00)
  %add_x_full1_load_sub_calltmp75_.i278 = fadd <8 x float> %sub_calltmp75_.i277, %17
  %mul_x_full_load77_x_full_load78.i279 = fmul <8 x float> %add_x_full1_load_sub_calltmp75_.i278, %add_x_full1_load_sub_calltmp75_.i278
  %mul__x_full_load79.i280 = fmul <8 x float> %add_x_full1_load_sub_calltmp75_.i278, splat (float 0x3FB2043760000000)
  %add_mul__x_full_load79_.i281 = fadd <8 x float> %mul__x_full_load79.i280, splat (float 0xBFBD7A3700000000)
  %mul_add_mul__x_full_load79__x_full_load80.i282 = fmul <8 x float> %add_x_full1_load_sub_calltmp75_.i278, %add_mul__x_full_load79_.i281
  %add_mul_add_mul__x_full_load79__x_full_load80_.i283 = fadd <8 x float> %mul_add_mul__x_full_load79__x_full_load80.i282, splat (float 0x3FBDE4A340000000)
  %mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81.i284 = fmul <8 x float> %add_x_full1_load_sub_calltmp75_.i278, %add_mul_add_mul__x_full_load79__x_full_load80_.i283
  %add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81_.i285 = fadd <8 x float> %mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81.i284, splat (float 0xBFBFCBA9E0000000)
  %mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82.i286 = fmul <8 x float> %add_x_full1_load_sub_calltmp75_.i278, %add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81_.i285
  %add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82_.i287 = fadd <8 x float> %mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82.i286, splat (float 0x3FC23D37E0000000)
  %mul_add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82__x_full_load83.i288 = fmul <8 x float> %add_x_full1_load_sub_calltmp75_.i278, %add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82_.i287
  %add_mul_add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82__x_full_load83_.i289 = fadd <8 x float> %mul_add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82__x_full_load83.i288, splat (float 0xBFC555CA00000000)
  %mul_add_mul_add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82__x_full_load83__x_full_load84.i290 = fmul <8 x float> %add_x_full1_load_sub_calltmp75_.i278, %add_mul_add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82__x_full_load83_.i289
  %add_mul_add_mul_add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82__x_full_load83__x_full_load84_.i291 = fadd <8 x float> %mul_add_mul_add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82__x_full_load83__x_full_load84.i290, splat (float 0x3FC999D580000000)
  %mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82__x_full_load83__x_full_load84__x_full_load85.i292 = fmul <8 x float> %add_x_full1_load_sub_calltmp75_.i278, %add_mul_add_mul_add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82__x_full_load83__x_full_load84_.i291
  %add_mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82__x_full_load83__x_full_load84__x_full_load85_.i293 = fadd <8 x float> %mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82__x_full_load83__x_full_load84__x_full_load85.i292, splat (float 0xBFCFFFFF80000000)
  %mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82__x_full_load83__x_full_load84__x_full_load85__x_full_load86.i294 = fmul <8 x float> %add_x_full1_load_sub_calltmp75_.i278, %add_mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82__x_full_load83__x_full_load84__x_full_load85_.i293
  %add_mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82__x_full_load83__x_full_load84__x_full_load85__x_full_load86_.i295 = fadd <8 x float> %mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82__x_full_load83__x_full_load84__x_full_load85__x_full_load86.i294, splat (float 0x3FD5555540000000)
  %mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82__x_full_load83__x_full_load84__x_full_load85__x_full_load86__x_full_load87.i296 = fmul <8 x float> %add_x_full1_load_sub_calltmp75_.i278, %add_mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82__x_full_load83__x_full_load84__x_full_load85__x_full_load86_.i295
  %mul_mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82__x_full_load83__x_full_load84__x_full_load85__x_full_load86__x_full_load87_z_load.i297 = fmul <8 x float> %mul_x_full_load77_x_full_load78.i279, %mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82__x_full_load83__x_full_load84__x_full_load85__x_full_load86__x_full_load87.i296
  %e_load88_to_float.i299 = sitofp <8 x i32> %add_e_load_x_smaller_SQRTHF_load.i275 to <8 x float>
  %mul_fe_load_.i300 = fmul <8 x float> %e_load88_to_float.i299, splat (float 0x3F2BD01060000000)
  %20 = fsub <8 x float> %mul_mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82__x_full_load83__x_full_load84__x_full_load85__x_full_load86__x_full_load87_z_load.i297, %mul_fe_load_.i300
  %mul__z_load90.i301 = fmul <8 x float> %mul_x_full_load77_x_full_load78.i279, splat (float 5.000000e-01)
  %sub_y_load92_mul__z_load90.i302 = fsub <8 x float> %20, %mul__z_load90.i301
  %add_x_full_load93_y_load94.i303 = fadd <8 x float> %add_x_full1_load_sub_calltmp75_.i278, %sub_y_load92_mul__z_load90.i302
  %mul__fe_load96.i304 = fmul <8 x float> %e_load88_to_float.i299, splat (float 0x3FE6300000000000)
  %add_z_load95_mul__fe_load96.i305 = fadd <8 x float> %mul__fe_load96.i304, %add_x_full_load93_y_load94.i303
  %add_add_calltmp80_mul_calltmp84_calltmp88_calltmp92 = fadd <8 x float> %add_calltmp80_mul_calltmp84_calltmp88, %add_z_load95_mul__fe_load96.i305
  %ptr175 = getelementptr i8, ptr %result, i64 %11
  call void @llvm.masked.store.v8f32.p0(<8 x float> %add_add_calltmp80_mul_calltmp84_calltmp88_calltmp92, ptr %ptr175, i32 1, <8 x i1> %cmp61)
  br label %foreach_reset
}

; Function Attrs: nounwind uwtable
define void @MathFuncs_ISPC_Impl(ptr noalias %a, ptr noalias captures(none) %result, i32 %count) local_unnamed_addr #0 {
allocas:
  %nextras = srem i32 %count, 8
  %aligned_end = sub nsw i32 %count, %nextras
  %before_aligned_end8378 = icmp sgt i32 %aligned_end, 0
  br i1 %before_aligned_end8378, label %foreach_full_body.lr.ph, label %partial_inner_all_outer, !llvm.loop !16

foreach_full_body.lr.ph:                          ; preds = %allocas
  %0 = zext nneg i32 %aligned_end to i64
  br label %foreach_full_body, !llvm.loop !16

foreach_full_body:                                ; preds = %foreach_full_body.lr.ph, %foreach_full_body
  %indvars.iv = phi i64 [ 0, %foreach_full_body.lr.ph ], [ %indvars.iv.next, %foreach_full_body ]
  %1 = shl nuw nsw i64 %indvars.iv, 2
  %ptr = getelementptr i8, ptr %a, i64 %1, !filename !10, !first_line !11, !first_column !12, !last_line !11, !last_column !13
  %ptr_masked_load110 = load <8 x float>, ptr %ptr, align 4, !filename !10, !first_line !11, !first_column !12, !last_line !11, !last_column !13
  %2 = tail call <8 x float> @llvm.sqrt.v8f32(<8 x float> %ptr_masked_load110)
  %mul_x_full_load51_.i = fmul <8 x float> %ptr_masked_load110, splat (float 0x3FE45F3060000000)
  %call.i.i.i = tail call <8 x float> @llvm.x86.avx.round.ps.256(<8 x float> %mul_x_full_load51_.i, i32 9)
  %k_real_load_to_int32.i = fptosi <8 x float> %call.i.i.i to <8 x i32>
  %mul_k_real_load56_.i = fmul <8 x float> %call.i.i.i, splat (float 0x3FF921FB60000000)
  %sub_x_full_load55_mul_k_real_load56_.i = fsub <8 x float> %ptr_masked_load110, %mul_k_real_load56_.i
  %bitop.i = and <8 x i32> %k_real_load_to_int32.i, splat (i32 2)
  %greater_k_mod4_load59_.i.not = icmp eq <8 x i32> %bitop.i, zeroinitializer
  %3 = and <8 x i32> %k_real_load_to_int32.i, splat (i32 1)
  %sin_usecos_load_toMaskBool.i = sub nsw <8 x i32> zeroinitializer, %3
  %mask_as_float.i.i = bitcast <8 x i32> %sin_usecos_load_toMaskBool.i to <8 x float>
  %blend.i.i = call <8 x float> @llvm.x86.avx.blendv.ps.256(<8 x float> %sub_x_full_load55_mul_k_real_load56_.i, <8 x float> splat (float 1.000000e+00), <8 x float> %mask_as_float.i.i)
  %blend.i.i259 = call <8 x float> @llvm.x86.avx.blendv.ps.256(<8 x float> splat (float 0xBFC5555560000000), <8 x float> splat (float -5.000000e-01), <8 x float> %mask_as_float.i.i)
  %blend.i.i262 = call <8 x float> @llvm.x86.avx.blendv.ps.256(<8 x float> splat (float 0x3F81111300000000), <8 x float> splat (float 0x3FA5555480000000), <8 x float> %mask_as_float.i.i)
  %blend.i.i265 = call <8 x float> @llvm.x86.avx.blendv.ps.256(<8 x float> splat (float 0xBF2A0212C0000000), <8 x float> splat (float 0xBF56C13020000000), <8 x float> %mask_as_float.i.i)
  %blend.i.i268 = call <8 x float> @llvm.x86.avx.blendv.ps.256(<8 x float> splat (float 0x3EC7271500000000), <8 x float> splat (float 0x3EF9F57380000000), <8 x float> %mask_as_float.i.i)
  %blend.i.i271 = call <8 x float> @llvm.x86.avx.blendv.ps.256(<8 x float> splat (float 0xBE5AE00260000000), <8 x float> splat (float 0xBE916C69A0000000), <8 x float> %mask_as_float.i.i)
  %mul_x_load155_x_load156.i = fmul <8 x float> %sub_x_full_load55_mul_k_real_load56_.i, %sub_x_full_load55_mul_k_real_load56_.i
  %mul_x2_load_c10_load.i = fmul <8 x float> %mul_x_load155_x_load156.i, %blend.i.i271
  %add_mul_x2_load_c10_load_c8_load.i = fadd <8 x float> %blend.i.i268, %mul_x2_load_c10_load.i
  %mul_x2_load157_formula_load.i = fmul <8 x float> %mul_x_load155_x_load156.i, %add_mul_x2_load_c10_load_c8_load.i
  %add_mul_x2_load157_formula_load_c6_load.i = fadd <8 x float> %blend.i.i265, %mul_x2_load157_formula_load.i
  %mul_x2_load158_formula_load159.i = fmul <8 x float> %mul_x_load155_x_load156.i, %add_mul_x2_load157_formula_load_c6_load.i
  %add_mul_x2_load158_formula_load159_c4_load.i = fadd <8 x float> %blend.i.i262, %mul_x2_load158_formula_load159.i
  %mul_x2_load160_formula_load161.i = fmul <8 x float> %mul_x_load155_x_load156.i, %add_mul_x2_load158_formula_load159_c4_load.i
  %add_mul_x2_load160_formula_load161_c2_load.i = fadd <8 x float> %blend.i.i259, %mul_x2_load160_formula_load161.i
  %mul_x2_load162_formula_load163.i = fmul <8 x float> %mul_x_load155_x_load156.i, %add_mul_x2_load160_formula_load161_c2_load.i
  %add_mul_x2_load162_formula_load163_.i = fadd <8 x float> %mul_x2_load162_formula_load163.i, splat (float 1.000000e+00)
  %mul_formula_load165_outside_load.i = fmul <8 x float> %blend.i.i, %add_mul_x2_load162_formula_load163_.i
  %formula_load172_negate.i = fneg <8 x float> %mul_formula_load165_outside_load.i
  %blend.i.i274 = select <8 x i1> %greater_k_mod4_load59_.i.not, <8 x float> %mul_formula_load165_outside_load.i, <8 x float> %formula_load172_negate.i
  %bitop.i133 = and <8 x i32> %k_real_load_to_int32.i, splat (i32 3)
  %logical_or5898.i = icmp eq <8 x i32> %3, zeroinitializer
  %4 = add nsw <8 x i32> %bitop.i133, splat (i32 -1)
  %logical_or6199.i = icmp ult <8 x i32> %4, splat (i32 2)
  %blend.i.i277 = select <8 x i1> %logical_or5898.i, <8 x float> splat (float 1.000000e+00), <8 x float> %sub_x_full_load55_mul_k_real_load56_.i
  %blend.i.i280 = select <8 x i1> %logical_or5898.i, <8 x float> splat (float -5.000000e-01), <8 x float> splat (float 0xBFC5555560000000)
  %blend.i.i283 = select <8 x i1> %logical_or5898.i, <8 x float> splat (float 0x3FA5555480000000), <8 x float> splat (float 0x3F81111300000000)
  %blend.i.i286 = select <8 x i1> %logical_or5898.i, <8 x float> splat (float 0xBF56C13020000000), <8 x float> splat (float 0xBF2A0212C0000000)
  %blend.i.i289 = select <8 x i1> %logical_or5898.i, <8 x float> splat (float 0x3EF9F57380000000), <8 x float> splat (float 0x3EC7271500000000)
  %blend.i.i292 = select <8 x i1> %logical_or5898.i, <8 x float> splat (float 0xBE916C69A0000000), <8 x float> splat (float 0xBE5AE00260000000)
  %mul_x2_load_c10_load.i135 = fmul <8 x float> %mul_x_load155_x_load156.i, %blend.i.i292
  %add_mul_x2_load_c10_load_c8_load.i136 = fadd <8 x float> %blend.i.i289, %mul_x2_load_c10_load.i135
  %mul_x2_load159_formula_load.i = fmul <8 x float> %mul_x_load155_x_load156.i, %add_mul_x2_load_c10_load_c8_load.i136
  %add_mul_x2_load159_formula_load_c6_load.i = fadd <8 x float> %blend.i.i286, %mul_x2_load159_formula_load.i
  %mul_x2_load160_formula_load161.i137 = fmul <8 x float> %mul_x_load155_x_load156.i, %add_mul_x2_load159_formula_load_c6_load.i
  %add_mul_x2_load160_formula_load161_c4_load.i = fadd <8 x float> %blend.i.i283, %mul_x2_load160_formula_load161.i137
  %mul_x2_load162_formula_load163.i138 = fmul <8 x float> %mul_x_load155_x_load156.i, %add_mul_x2_load160_formula_load161_c4_load.i
  %add_mul_x2_load162_formula_load163_c2_load.i = fadd <8 x float> %blend.i.i280, %mul_x2_load162_formula_load163.i138
  %mul_x2_load164_formula_load165.i = fmul <8 x float> %mul_x_load155_x_load156.i, %add_mul_x2_load162_formula_load163_c2_load.i
  %add_mul_x2_load164_formula_load165_.i = fadd <8 x float> %mul_x2_load164_formula_load165.i, splat (float 1.000000e+00)
  %mul_formula_load167_outside_load.i = fmul <8 x float> %blend.i.i277, %add_mul_x2_load164_formula_load165_.i
  %formula_load174_negate.i = fneg <8 x float> %mul_formula_load167_outside_load.i
  %blend.i.i295 = select <8 x i1> %logical_or6199.i, <8 x float> %formula_load174_negate.i, <8 x float> %mul_formula_load167_outside_load.i
  %mul_calltmp22_calltmp25 = fmul <8 x float> %blend.i.i295, %blend.i.i274
  %add_calltmp_mul_calltmp22_calltmp25 = fadd <8 x float> %2, %mul_calltmp22_calltmp25
  %add_x_load26_ = fadd <8 x float> %ptr_masked_load110, splat (float 1.000000e+00)
  %5 = bitcast <8 x float> %add_x_load26_ to <8 x i32>
  %bitop8.i.i = and <8 x i32> %5, splat (i32 -2139095041)
  %bitop.i.i = lshr <8 x i32> %5, splat (i32 23)
  %bitop10.i.i = and <8 x i32> %bitop.i.i, splat (i32 255)
  %sub_bitop10_.i.i = add nsw <8 x i32> %bitop10.i.i, splat (i32 -126)
  %bitop15.i.i = or disjoint <8 x i32> %bitop8.i.i, splat (i32 1056964608)
  %6 = bitcast <8 x i32> %bitop15.i.i to <8 x float>
  %greater__x_full_load54.i = fcmp olt <8 x float> %6, splat (float 0x3FE6A09E60000000)
  %blend.i = select <8 x i1> %greater__x_full_load54.i, <8 x float> splat (float 0xFFFFFFFFE0000000), <8 x float> zeroinitializer
  %7 = bitcast <8 x float> %blend.i to <8 x i32>
  %add_e_load_x_smaller_SQRTHF_load.i = add nsw <8 x i32> %sub_bitop10_.i.i, %7
  %bitop.i141 = and <8 x i32> %bitop15.i.i, %7
  %8 = bitcast <8 x i32> %bitop.i141 to <8 x float>
  %sub_calltmp75_.i = fadd <8 x float> %8, splat (float -1.000000e+00)
  %add_x_full1_load_sub_calltmp75_.i = fadd <8 x float> %sub_calltmp75_.i, %6
  %mul_x_full_load77_x_full_load78.i = fmul <8 x float> %add_x_full1_load_sub_calltmp75_.i, %add_x_full1_load_sub_calltmp75_.i
  %mul__x_full_load79.i = fmul <8 x float> %add_x_full1_load_sub_calltmp75_.i, splat (float 0x3FB2043760000000)
  %add_mul__x_full_load79_.i = fadd <8 x float> %mul__x_full_load79.i, splat (float 0xBFBD7A3700000000)
  %mul_add_mul__x_full_load79__x_full_load80.i = fmul <8 x float> %add_x_full1_load_sub_calltmp75_.i, %add_mul__x_full_load79_.i
  %add_mul_add_mul__x_full_load79__x_full_load80_.i = fadd <8 x float> %mul_add_mul__x_full_load79__x_full_load80.i, splat (float 0x3FBDE4A340000000)
  %mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81.i = fmul <8 x float> %add_x_full1_load_sub_calltmp75_.i, %add_mul_add_mul__x_full_load79__x_full_load80_.i
  %add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81_.i = fadd <8 x float> %mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81.i, splat (float 0xBFBFCBA9E0000000)
  %mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82.i = fmul <8 x float> %add_x_full1_load_sub_calltmp75_.i, %add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81_.i
  %add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82_.i = fadd <8 x float> %mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82.i, splat (float 0x3FC23D37E0000000)
  %mul_add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82__x_full_load83.i = fmul <8 x float> %add_x_full1_load_sub_calltmp75_.i, %add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82_.i
  %add_mul_add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82__x_full_load83_.i = fadd <8 x float> %mul_add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82__x_full_load83.i, splat (float 0xBFC555CA00000000)
  %mul_add_mul_add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82__x_full_load83__x_full_load84.i = fmul <8 x float> %add_x_full1_load_sub_calltmp75_.i, %add_mul_add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82__x_full_load83_.i
  %add_mul_add_mul_add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82__x_full_load83__x_full_load84_.i = fadd <8 x float> %mul_add_mul_add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82__x_full_load83__x_full_load84.i, splat (float 0x3FC999D580000000)
  %mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82__x_full_load83__x_full_load84__x_full_load85.i = fmul <8 x float> %add_x_full1_load_sub_calltmp75_.i, %add_mul_add_mul_add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82__x_full_load83__x_full_load84_.i
  %add_mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82__x_full_load83__x_full_load84__x_full_load85_.i = fadd <8 x float> %mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82__x_full_load83__x_full_load84__x_full_load85.i, splat (float 0xBFCFFFFF80000000)
  %mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82__x_full_load83__x_full_load84__x_full_load85__x_full_load86.i = fmul <8 x float> %add_x_full1_load_sub_calltmp75_.i, %add_mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82__x_full_load83__x_full_load84__x_full_load85_.i
  %add_mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82__x_full_load83__x_full_load84__x_full_load85__x_full_load86_.i = fadd <8 x float> %mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82__x_full_load83__x_full_load84__x_full_load85__x_full_load86.i, splat (float 0x3FD5555540000000)
  %mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82__x_full_load83__x_full_load84__x_full_load85__x_full_load86__x_full_load87.i = fmul <8 x float> %add_x_full1_load_sub_calltmp75_.i, %add_mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82__x_full_load83__x_full_load84__x_full_load85__x_full_load86_.i
  %mul_mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82__x_full_load83__x_full_load84__x_full_load85__x_full_load86__x_full_load87_z_load.i = fmul <8 x float> %mul_x_full_load77_x_full_load78.i, %mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82__x_full_load83__x_full_load84__x_full_load85__x_full_load86__x_full_load87.i
  %e_load88_to_float.i = sitofp <8 x i32> %add_e_load_x_smaller_SQRTHF_load.i to <8 x float>
  %mul_fe_load_.i = fmul <8 x float> %e_load88_to_float.i, splat (float 0x3F2BD01060000000)
  %9 = fsub <8 x float> %mul_mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82__x_full_load83__x_full_load84__x_full_load85__x_full_load86__x_full_load87_z_load.i, %mul_fe_load_.i
  %mul__z_load90.i = fmul <8 x float> %mul_x_full_load77_x_full_load78.i, splat (float 5.000000e-01)
  %sub_y_load92_mul__z_load90.i = fsub <8 x float> %9, %mul__z_load90.i
  %add_x_full_load93_y_load94.i = fadd <8 x float> %add_x_full1_load_sub_calltmp75_.i, %sub_y_load92_mul__z_load90.i
  %mul__fe_load96.i = fmul <8 x float> %e_load88_to_float.i, splat (float 0x3FE6300000000000)
  %add_z_load95_mul__fe_load96.i = fadd <8 x float> %mul__fe_load96.i, %add_x_full_load93_y_load94.i
  %add_add_calltmp_mul_calltmp22_calltmp25_calltmp28 = fadd <8 x float> %add_calltmp_mul_calltmp22_calltmp25, %add_z_load95_mul__fe_load96.i
  %ptr113 = getelementptr i8, ptr %result, i64 %1
  store <8 x float> %add_add_calltmp_mul_calltmp22_calltmp25_calltmp28, ptr %ptr113, align 4, !filename !10, !first_line !14, !first_column !11, !last_line !14, !last_column !15
  %indvars.iv.next = add nuw nsw i64 %indvars.iv, 8
  %before_aligned_end8 = icmp samesign ult i64 %indvars.iv.next, %0
  br i1 %before_aligned_end8, label %foreach_full_body, label %outer_not_in_extras.partial_inner_all_outer_crit_edge, !llvm.loop !16

foreach_reset:                                    ; preds = %partial_inner_only, %partial_inner_all_outer
  ret void

outer_not_in_extras.partial_inner_all_outer_crit_edge: ; preds = %foreach_full_body
  %10 = trunc nuw nsw i64 %indvars.iv.next to i32
  br label %partial_inner_all_outer, !llvm.loop !16

partial_inner_all_outer:                          ; preds = %outer_not_in_extras.partial_inner_all_outer_crit_edge, %allocas
  %counter.1.lcssa = phi i32 [ %10, %outer_not_in_extras.partial_inner_all_outer_crit_edge ], [ 0, %allocas ]
  %before_full_end = icmp slt i32 %counter.1.lcssa, %count
  br i1 %before_full_end, label %partial_inner_only, label %foreach_reset

partial_inner_only:                               ; preds = %partial_inner_all_outer
  %smear_counter_init35 = insertelement <8 x i32> poison, i32 %counter.1.lcssa, i64 0
  %smear_counter36 = shufflevector <8 x i32> %smear_counter_init35, <8 x i32> poison, <8 x i32> zeroinitializer
  %iter_val37 = or disjoint <8 x i32> %smear_counter36, <i32 0, i32 1, i32 2, i32 3, i32 4, i32 5, i32 6, i32 7>
  %smear_end_init38 = insertelement <8 x i32> poison, i32 %count, i64 0
  %smear_end39 = shufflevector <8 x i32> %smear_end_init38, <8 x i32> poison, <8 x i32> zeroinitializer
  %cmp40 = icmp slt <8 x i32> %iter_val37, %smear_end39
  %mul__i_load43.elt0 = shl nsw i32 %counter.1.lcssa, 2
  %11 = zext nneg i32 %mul__i_load43.elt0 to i64
  %ptr117 = getelementptr i8, ptr %a, i64 %11
  %floatval.i.i = tail call <8 x float> @llvm.masked.load.v8f32.p0(ptr %ptr117, i32 1, <8 x i1> %cmp40, <8 x float> zeroinitializer)
  %12 = tail call <8 x float> @llvm.sqrt.v8f32(<8 x float> %floatval.i.i)
  %mul_x_full_load51_.i149 = fmul <8 x float> %floatval.i.i, splat (float 0x3FE45F3060000000)
  %call.i.i.i150 = tail call <8 x float> @llvm.x86.avx.round.ps.256(<8 x float> %mul_x_full_load51_.i149, i32 9)
  %k_real_load_to_int32.i151 = fptosi <8 x float> %call.i.i.i150 to <8 x i32>
  %mul_k_real_load56_.i152 = fmul <8 x float> %call.i.i.i150, splat (float 0x3FF921FB60000000)
  %sub_x_full_load55_mul_k_real_load56_.i153 = fsub <8 x float> %floatval.i.i, %mul_k_real_load56_.i152
  %bitop.i154 = and <8 x i32> %k_real_load_to_int32.i151, splat (i32 2)
  %greater_k_mod4_load59_.i155.not = icmp eq <8 x i32> %bitop.i154, zeroinitializer
  %13 = and <8 x i32> %k_real_load_to_int32.i151, splat (i32 1)
  %sin_usecos_load_toMaskBool.i157 = sub nsw <8 x i32> zeroinitializer, %13
  %mask_as_float.i.i296 = bitcast <8 x i32> %sin_usecos_load_toMaskBool.i157 to <8 x float>
  %blend.i.i298 = call <8 x float> @llvm.x86.avx.blendv.ps.256(<8 x float> %sub_x_full_load55_mul_k_real_load56_.i153, <8 x float> splat (float 1.000000e+00), <8 x float> %mask_as_float.i.i296)
  %blend.i.i301 = call <8 x float> @llvm.x86.avx.blendv.ps.256(<8 x float> splat (float 0xBFC5555560000000), <8 x float> splat (float -5.000000e-01), <8 x float> %mask_as_float.i.i296)
  %blend.i.i304 = call <8 x float> @llvm.x86.avx.blendv.ps.256(<8 x float> splat (float 0x3F81111300000000), <8 x float> splat (float 0x3FA5555480000000), <8 x float> %mask_as_float.i.i296)
  %blend.i.i307 = call <8 x float> @llvm.x86.avx.blendv.ps.256(<8 x float> splat (float 0xBF2A0212C0000000), <8 x float> splat (float 0xBF56C13020000000), <8 x float> %mask_as_float.i.i296)
  %blend.i.i310 = call <8 x float> @llvm.x86.avx.blendv.ps.256(<8 x float> splat (float 0x3EC7271500000000), <8 x float> splat (float 0x3EF9F57380000000), <8 x float> %mask_as_float.i.i296)
  %blend.i.i313 = call <8 x float> @llvm.x86.avx.blendv.ps.256(<8 x float> splat (float 0xBE5AE00260000000), <8 x float> splat (float 0xBE916C69A0000000), <8 x float> %mask_as_float.i.i296)
  %mul_x_load155_x_load156.i164 = fmul <8 x float> %sub_x_full_load55_mul_k_real_load56_.i153, %sub_x_full_load55_mul_k_real_load56_.i153
  %mul_x2_load_c10_load.i165 = fmul <8 x float> %mul_x_load155_x_load156.i164, %blend.i.i313
  %add_mul_x2_load_c10_load_c8_load.i166 = fadd <8 x float> %blend.i.i310, %mul_x2_load_c10_load.i165
  %mul_x2_load157_formula_load.i167 = fmul <8 x float> %mul_x_load155_x_load156.i164, %add_mul_x2_load_c10_load_c8_load.i166
  %add_mul_x2_load157_formula_load_c6_load.i168 = fadd <8 x float> %blend.i.i307, %mul_x2_load157_formula_load.i167
  %mul_x2_load158_formula_load159.i169 = fmul <8 x float> %mul_x_load155_x_load156.i164, %add_mul_x2_load157_formula_load_c6_load.i168
  %add_mul_x2_load158_formula_load159_c4_load.i170 = fadd <8 x float> %blend.i.i304, %mul_x2_load158_formula_load159.i169
  %mul_x2_load160_formula_load161.i171 = fmul <8 x float> %mul_x_load155_x_load156.i164, %add_mul_x2_load158_formula_load159_c4_load.i170
  %add_mul_x2_load160_formula_load161_c2_load.i172 = fadd <8 x float> %blend.i.i301, %mul_x2_load160_formula_load161.i171
  %mul_x2_load162_formula_load163.i173 = fmul <8 x float> %mul_x_load155_x_load156.i164, %add_mul_x2_load160_formula_load161_c2_load.i172
  %add_mul_x2_load162_formula_load163_.i174 = fadd <8 x float> %mul_x2_load162_formula_load163.i173, splat (float 1.000000e+00)
  %mul_formula_load165_outside_load.i175 = fmul <8 x float> %blend.i.i298, %add_mul_x2_load162_formula_load163_.i174
  %formula_load172_negate.i176 = fneg <8 x float> %mul_formula_load165_outside_load.i175
  %blend.i.i316 = select <8 x i1> %greater_k_mod4_load59_.i155.not, <8 x float> %mul_formula_load165_outside_load.i175, <8 x float> %formula_load172_negate.i176
  %bitop.i190 = and <8 x i32> %k_real_load_to_int32.i151, splat (i32 3)
  %logical_or5898.i191 = icmp eq <8 x i32> %13, zeroinitializer
  %14 = add nsw <8 x i32> %bitop.i190, splat (i32 -1)
  %logical_or6199.i192 = icmp ult <8 x i32> %14, splat (i32 2)
  %blend.i.i319 = select <8 x i1> %logical_or5898.i191, <8 x float> splat (float 1.000000e+00), <8 x float> %sub_x_full_load55_mul_k_real_load56_.i153
  %blend.i.i322 = select <8 x i1> %logical_or5898.i191, <8 x float> splat (float -5.000000e-01), <8 x float> splat (float 0xBFC5555560000000)
  %blend.i.i325 = select <8 x i1> %logical_or5898.i191, <8 x float> splat (float 0x3FA5555480000000), <8 x float> splat (float 0x3F81111300000000)
  %blend.i.i328 = select <8 x i1> %logical_or5898.i191, <8 x float> splat (float 0xBF56C13020000000), <8 x float> splat (float 0xBF2A0212C0000000)
  %blend.i.i331 = select <8 x i1> %logical_or5898.i191, <8 x float> splat (float 0x3EF9F57380000000), <8 x float> splat (float 0x3EC7271500000000)
  %blend.i.i334 = select <8 x i1> %logical_or5898.i191, <8 x float> splat (float 0xBE916C69A0000000), <8 x float> splat (float 0xBE5AE00260000000)
  %mul_x2_load_c10_load.i201 = fmul <8 x float> %mul_x_load155_x_load156.i164, %blend.i.i334
  %add_mul_x2_load_c10_load_c8_load.i202 = fadd <8 x float> %blend.i.i331, %mul_x2_load_c10_load.i201
  %mul_x2_load159_formula_load.i203 = fmul <8 x float> %mul_x_load155_x_load156.i164, %add_mul_x2_load_c10_load_c8_load.i202
  %add_mul_x2_load159_formula_load_c6_load.i204 = fadd <8 x float> %blend.i.i328, %mul_x2_load159_formula_load.i203
  %mul_x2_load160_formula_load161.i205 = fmul <8 x float> %mul_x_load155_x_load156.i164, %add_mul_x2_load159_formula_load_c6_load.i204
  %add_mul_x2_load160_formula_load161_c4_load.i206 = fadd <8 x float> %blend.i.i325, %mul_x2_load160_formula_load161.i205
  %mul_x2_load162_formula_load163.i207 = fmul <8 x float> %mul_x_load155_x_load156.i164, %add_mul_x2_load160_formula_load161_c4_load.i206
  %add_mul_x2_load162_formula_load163_c2_load.i208 = fadd <8 x float> %blend.i.i322, %mul_x2_load162_formula_load163.i207
  %mul_x2_load164_formula_load165.i209 = fmul <8 x float> %mul_x_load155_x_load156.i164, %add_mul_x2_load162_formula_load163_c2_load.i208
  %add_mul_x2_load164_formula_load165_.i210 = fadd <8 x float> %mul_x2_load164_formula_load165.i209, splat (float 1.000000e+00)
  %mul_formula_load167_outside_load.i211 = fmul <8 x float> %blend.i.i319, %add_mul_x2_load164_formula_load165_.i210
  %formula_load174_negate.i213 = fneg <8 x float> %mul_formula_load167_outside_load.i211
  %blend.i.i337 = select <8 x i1> %logical_or6199.i192, <8 x float> %formula_load174_negate.i213, <8 x float> %mul_formula_load167_outside_load.i211
  %mul_calltmp56_calltmp59 = fmul <8 x float> %blend.i.i337, %blend.i.i316
  %add_calltmp53_mul_calltmp56_calltmp59 = fadd <8 x float> %12, %mul_calltmp56_calltmp59
  %add_x_load60_ = fadd <8 x float> %floatval.i.i, splat (float 1.000000e+00)
  %15 = bitcast <8 x float> %add_x_load60_ to <8 x i32>
  %bitop8.i.i217 = and <8 x i32> %15, splat (i32 -2139095041)
  %bitop.i.i218 = lshr <8 x i32> %15, splat (i32 23)
  %bitop10.i.i219 = and <8 x i32> %bitop.i.i218, splat (i32 255)
  %sub_bitop10_.i.i220 = add nsw <8 x i32> %bitop10.i.i219, splat (i32 -126)
  %16 = select <8 x i1> %cmp40, <8 x i32> %sub_bitop10_.i.i220, <8 x i32> undef
  %bitop15.i.i221 = or disjoint <8 x i32> %bitop8.i.i217, splat (i32 1056964608)
  %17 = bitcast <8 x i32> %bitop15.i.i221 to <8 x float>
  %greater__x_full_load54.i222 = fcmp olt <8 x float> %17, splat (float 0x3FE6A09E60000000)
  %blend.i343 = select <8 x i1> %greater__x_full_load54.i222, <8 x float> splat (float 0xFFFFFFFFE0000000), <8 x float> zeroinitializer
  %18 = bitcast <8 x float> %blend.i343 to <8 x i32>
  %add_e_load_x_smaller_SQRTHF_load.i226 = add nsw <8 x i32> %16, %18
  %bitop.i227 = and <8 x i32> %bitop15.i.i221, %18
  %19 = bitcast <8 x i32> %bitop.i227 to <8 x float>
  %sub_calltmp75_.i228 = fadd <8 x float> %19, splat (float -1.000000e+00)
  %add_x_full1_load_sub_calltmp75_.i229 = fadd <8 x float> %sub_calltmp75_.i228, %17
  %mul_x_full_load77_x_full_load78.i230 = fmul <8 x float> %add_x_full1_load_sub_calltmp75_.i229, %add_x_full1_load_sub_calltmp75_.i229
  %mul__x_full_load79.i231 = fmul <8 x float> %add_x_full1_load_sub_calltmp75_.i229, splat (float 0x3FB2043760000000)
  %add_mul__x_full_load79_.i232 = fadd <8 x float> %mul__x_full_load79.i231, splat (float 0xBFBD7A3700000000)
  %mul_add_mul__x_full_load79__x_full_load80.i233 = fmul <8 x float> %add_x_full1_load_sub_calltmp75_.i229, %add_mul__x_full_load79_.i232
  %add_mul_add_mul__x_full_load79__x_full_load80_.i234 = fadd <8 x float> %mul_add_mul__x_full_load79__x_full_load80.i233, splat (float 0x3FBDE4A340000000)
  %mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81.i235 = fmul <8 x float> %add_x_full1_load_sub_calltmp75_.i229, %add_mul_add_mul__x_full_load79__x_full_load80_.i234
  %add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81_.i236 = fadd <8 x float> %mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81.i235, splat (float 0xBFBFCBA9E0000000)
  %mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82.i237 = fmul <8 x float> %add_x_full1_load_sub_calltmp75_.i229, %add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81_.i236
  %add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82_.i238 = fadd <8 x float> %mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82.i237, splat (float 0x3FC23D37E0000000)
  %mul_add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82__x_full_load83.i239 = fmul <8 x float> %add_x_full1_load_sub_calltmp75_.i229, %add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82_.i238
  %add_mul_add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82__x_full_load83_.i240 = fadd <8 x float> %mul_add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82__x_full_load83.i239, splat (float 0xBFC555CA00000000)
  %mul_add_mul_add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82__x_full_load83__x_full_load84.i241 = fmul <8 x float> %add_x_full1_load_sub_calltmp75_.i229, %add_mul_add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82__x_full_load83_.i240
  %add_mul_add_mul_add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82__x_full_load83__x_full_load84_.i242 = fadd <8 x float> %mul_add_mul_add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82__x_full_load83__x_full_load84.i241, splat (float 0x3FC999D580000000)
  %mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82__x_full_load83__x_full_load84__x_full_load85.i243 = fmul <8 x float> %add_x_full1_load_sub_calltmp75_.i229, %add_mul_add_mul_add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82__x_full_load83__x_full_load84_.i242
  %add_mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82__x_full_load83__x_full_load84__x_full_load85_.i244 = fadd <8 x float> %mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82__x_full_load83__x_full_load84__x_full_load85.i243, splat (float 0xBFCFFFFF80000000)
  %mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82__x_full_load83__x_full_load84__x_full_load85__x_full_load86.i245 = fmul <8 x float> %add_x_full1_load_sub_calltmp75_.i229, %add_mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82__x_full_load83__x_full_load84__x_full_load85_.i244
  %add_mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82__x_full_load83__x_full_load84__x_full_load85__x_full_load86_.i246 = fadd <8 x float> %mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82__x_full_load83__x_full_load84__x_full_load85__x_full_load86.i245, splat (float 0x3FD5555540000000)
  %mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82__x_full_load83__x_full_load84__x_full_load85__x_full_load86__x_full_load87.i247 = fmul <8 x float> %add_x_full1_load_sub_calltmp75_.i229, %add_mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82__x_full_load83__x_full_load84__x_full_load85__x_full_load86_.i246
  %mul_mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82__x_full_load83__x_full_load84__x_full_load85__x_full_load86__x_full_load87_z_load.i248 = fmul <8 x float> %mul_x_full_load77_x_full_load78.i230, %mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82__x_full_load83__x_full_load84__x_full_load85__x_full_load86__x_full_load87.i247
  %e_load88_to_float.i250 = sitofp <8 x i32> %add_e_load_x_smaller_SQRTHF_load.i226 to <8 x float>
  %mul_fe_load_.i251 = fmul <8 x float> %e_load88_to_float.i250, splat (float 0x3F2BD01060000000)
  %20 = fsub <8 x float> %mul_mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul_add_mul__x_full_load79__x_full_load80__x_full_load81__x_full_load82__x_full_load83__x_full_load84__x_full_load85__x_full_load86__x_full_load87_z_load.i248, %mul_fe_load_.i251
  %mul__z_load90.i252 = fmul <8 x float> %mul_x_full_load77_x_full_load78.i230, splat (float 5.000000e-01)
  %sub_y_load92_mul__z_load90.i253 = fsub <8 x float> %20, %mul__z_load90.i252
  %add_x_full_load93_y_load94.i254 = fadd <8 x float> %add_x_full1_load_sub_calltmp75_.i229, %sub_y_load92_mul__z_load90.i253
  %mul__fe_load96.i255 = fmul <8 x float> %e_load88_to_float.i250, splat (float 0x3FE6300000000000)
  %add_z_load95_mul__fe_load96.i256 = fadd <8 x float> %mul__fe_load96.i255, %add_x_full_load93_y_load94.i254
  %add_add_calltmp53_mul_calltmp56_calltmp59_calltmp62 = fadd <8 x float> %add_calltmp53_mul_calltmp56_calltmp59, %add_z_load95_mul__fe_load96.i256
  %ptr126 = getelementptr i8, ptr %result, i64 %11
  call void @llvm.masked.store.v8f32.p0(<8 x float> %add_add_calltmp53_mul_calltmp56_calltmp59_calltmp62, ptr %ptr126, i32 1, <8 x i1> %cmp40)
  br label %foreach_reset
}

; Function Attrs: nocallback nofree nosync nounwind willreturn memory(none)
declare <8 x float> @llvm.x86.avx.blendv.ps.256(<8 x float>, <8 x float>, <8 x float>) #1

; Function Attrs: nocallback nofree nosync nounwind speculatable willreturn memory(none)
declare <8 x float> @llvm.sqrt.v8f32(<8 x float>) #2

; Function Attrs: nocallback nofree nosync nounwind willreturn memory(none)
declare <8 x float> @llvm.x86.avx.round.ps.256(<8 x float>, i32 immarg) #1

; Function Attrs: nocallback nofree nosync nounwind willreturn memory(argmem: read)
declare <8 x float> @llvm.masked.load.v8f32.p0(ptr captures(none), i32 immarg, <8 x i1>, <8 x float>) #3

; Function Attrs: nocallback nofree nosync nounwind willreturn memory(argmem: write)
declare void @llvm.masked.store.v8f32.p0(<8 x float>, ptr captures(none), i32 immarg, <8 x i1>) #4

attributes #0 = { nounwind uwtable }
attributes #1 = { nocallback nofree nosync nounwind willreturn memory(none) }
attributes #2 = { nocallback nofree nosync nounwind speculatable willreturn memory(none) }
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
!10 = !{!"Case2_MathFunctions/MathFunctions.ispc"}
!11 = !{i32 9}
!12 = !{i32 19}
!13 = !{i32 23}
!14 = !{i32 10}
!15 = !{i32 18}
!16 = distinct !{!16, !9}
