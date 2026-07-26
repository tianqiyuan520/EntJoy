// ISPC reference: indirect gather + distance + reduction
//
// 优化思路：外层 foreach 使 i 为 varying → index[base+j] 需要 gather；
// 改为 uniform for + 内层 foreach，index[base+j] 变成顺序读，dataX/dataY 的 gather 无法避免但减少一级。

export void GatherReduce_ISPC_Impl(
    uniform float queryX[],
    uniform float queryY[],
    uniform float dataX[],
    uniform float dataY[],
    uniform int index[],
    uniform float result[],
    uniform int count)
{
    for (uniform int i = 0; i < count; i++) {
        uniform int base = i * 50;
        float qx = queryX[i];
        float qy = queryY[i];
        varying float best = 1e30f;
        foreach (j = 0 ... 50) {
            int idx = index[base + j];
            float dx = qx - dataX[idx];
            float dy = qy - dataY[idx];
            float distSq = dx * dx + dy * dy;
            if (distSq < best)
                best = distSq;
        }
        result[i] = reduce_min(best);
    }
}
