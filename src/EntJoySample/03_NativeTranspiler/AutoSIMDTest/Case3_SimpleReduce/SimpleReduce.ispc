// ISPC reference: min reduction
//
// 优化思路：将外层 foreach（并行 idx）改为 uniform for（串行 idx），
// 内层改为 foreach（协作 SIMD），末尾 reduce_min 跨 lane 归约。
// 原写法 foreach (i ...) 使 i 为 varying，a[base + j] 产生 gather；
// 优化后 base 为 uniform，内层 foreach(j) 读连续地址 → 顺序 SIMD load。

export void SimpleReduce_ISPC_Impl(
    uniform float a[],
    uniform float result[],
    uniform int count)
{
    for (uniform int i = 0; i < count; i++) {
        uniform int base = i * 100;
        varying float best = 1e30f;
        foreach (j = 0 ... 100) {
            float v = a[base + j];
            if (v < best) best = v;
        }
        result[i] = reduce_min(best);
    }
}
