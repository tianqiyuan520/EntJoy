// ISPC reference: a[i] * b[i] + c[i]
// 编译: ispc SimpleArith.ispc --emit-llvm -o SimpleArith.ll --target=avx2-i32x8 --math-lib=fast

export void SimpleArith_ISPC_Impl(
    uniform float a[],
    uniform float b[],
    uniform float c[],
    uniform float result[],
    uniform int count)
{
    foreach (i = 0 ... count) {
        result[i] = a[i] * b[i] + c[i];
    }
}
