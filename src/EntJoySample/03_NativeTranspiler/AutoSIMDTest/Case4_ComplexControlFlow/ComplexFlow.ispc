// ISPC reference: if/else if/else with gather

export void ComplexFlow_ISPC_Impl(
    uniform float a[],
    uniform float b[],
    uniform float result[],
    uniform float threshold,
    uniform int count)
{
    foreach (i = 0 ... count) {
        float v = a[i];
        if (v > threshold)
            result[i] = v * b[i];
        else if (v < -threshold)
            result[i] = v + b[i];
        else
            result[i] = 0.0f;
    }
}
