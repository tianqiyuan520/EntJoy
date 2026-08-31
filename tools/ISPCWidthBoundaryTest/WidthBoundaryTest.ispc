// WidthBoundaryTest.ispc
// 验证 foreach / foreach_tiled / reduce / 单例(programIndex!=0) 在不同 SIMD 宽度下边界一致。
// 编译目标：sse4(4-wide) / avx2(8-wide) / avx512skx-x16(16-wide)，结果必须一致。

// foreach 尾块边界（count 非 gang 宽度整数倍时自动 mask）
export void foreach_copy(uniform float src[], uniform float dst[], uniform int count) {
    foreach (i = 0 ... count) {
        dst[i] = src[i] * 2.0f + 1.0f;
    }
}

// foreach_tiled 分块边界
export void foreach_tiled_copy(uniform float src[], uniform float dst[], uniform int count) {
    foreach_tiled (i = 0 ... count) {
        dst[i] = src[i] * 2.0f + 1.0f;
    }
}

// 跨 lane 归约（reduce_add）
export uniform float reduce_sum(uniform float src[], uniform int count) {
    float acc = 0.0f;
    foreach (i = 0 ... count) {
        acc += src[i];
    }
    return reduce_add(acc);
}

// 单例执行：仅 lane 0 执行 uniform for（与生成器 programIndex!=0 同模式）
export void singleton_increment(uniform int dst[], uniform int count) {
    if (programIndex != 0) return;
    for (uniform int i = 0; i < count; i++) {
        dst[0] += 1;
    }
}
