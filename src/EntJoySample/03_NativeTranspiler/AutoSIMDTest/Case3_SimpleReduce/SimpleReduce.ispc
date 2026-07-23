// ISPC reference: min reduction

export void SimpleReduce_ISPC_Impl(
    uniform float a[],
    uniform float result[],
    uniform int count)
{
    foreach (i = 0 ... count) {
        float best = 1e30f;
        // 内部 100 元素归约
        int base = i * 100;
        for (int j = 0; j < 100; j++) {
            float v = a[base + j];
            if (v < best) best = v;
        }
        result[i] = best;
    }
}
