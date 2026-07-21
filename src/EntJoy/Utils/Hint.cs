using System.Runtime.CompilerServices;

namespace EntJoy
{
    /// <summary>
    /// Provides branch prediction hints for the NativeTranspiler.
    /// When transpiled to C++, <see cref="Likely"/> emits <c>[[likely]]</c>
    /// and <see cref="Unlikely"/> emits <c>[[unlikely]]</c> on the if-statement's
    /// true-branch. When transpiled to ISPC, they emit <c>__builtin_expect</c>.
    /// In pure C# execution (no native transpilation) they are no-ops that
    /// simply return the condition as-is.
    /// </summary>
    /// <remarks>
    /// Usage:
    /// <code>
    /// if (Hint.Likely(condition))
    /// {
    ///     // This branch is more likely — compiler can optimize for it.
    /// }
    /// </code>
    /// </remarks>
    public static class Hint
    {
        /// <summary>
        /// Hints to the native compiler that <paramref name="condition"/>
        /// is likely to be true.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Likely(bool condition) => condition;

        /// <summary>
        /// Hints to the native compiler that <paramref name="condition"/>
        /// is unlikely to be true (i.e., the false-branch is more likely).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool Unlikely(bool condition) => condition;
    }
}
