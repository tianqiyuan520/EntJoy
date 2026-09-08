using System;
using System.Runtime.CompilerServices;

namespace EntJoy.Collections
{
    /// <summary>
    /// 当前线程正在执行的 job 身份（IntPtr ctx 作为唯一 job 实例标识）。
    /// 主线程/非 job 代码为 default。容器写入点依此区分「同一 job 的并行 tile 写」（合法）
    /// 与「不同 job 交叉写同一容器」（冲突）。
    /// </summary>
    public static class JobIdentity
    {
        [ThreadStatic]
        private static nint _currentContext;

        /// <summary>当前线程所在 job 的执行上下文指针；default 表示不在任何 job 内。</summary>
        public static nint CurrentContext
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _currentContext;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void SetCurrentContext(nint ctx) => _currentContext = ctx;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static void ClearCurrentContext() => _currentContext = 0;
    }
}