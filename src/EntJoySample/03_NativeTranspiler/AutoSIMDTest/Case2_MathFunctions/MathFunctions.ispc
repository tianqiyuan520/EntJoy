// ISPC reference: sqrt(a) + sin(a)*cos(a) + log(a+1)

export void MathFuncs_ISPC_Impl(
    uniform float a[],
    uniform float result[],
    uniform int count)
{
    foreach (i = 0 ... count) {
        float x = a[i];
        result[i] = sqrt(x) + sin(x) * cos(x) + log(x + 1.0f);
    }
}
