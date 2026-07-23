// ISPC reference: indirect gather + distance + reduction

export void GatherReduce_ISPC_Impl(
    uniform float queryX[],
    uniform float queryY[],
    uniform float dataX[],
    uniform float dataY[],
    uniform int index[],
    uniform float result[],
    uniform int count)
{
    foreach (i = 0 ... count) {
        float qx = queryX[i];
        float qy = queryY[i];
        float best = 1e30f;
        int base = i * 50;
        for (int j = 0; j < 50; j++) {
            int idx = index[base + j];
            float dx = qx - dataX[idx];
            float dy = qy - dataY[idx];
            float distSq = dx * dx + dy * dy;
            if (distSq < best)
                best = distSq;
        }
        result[i] = best;
    }
}
