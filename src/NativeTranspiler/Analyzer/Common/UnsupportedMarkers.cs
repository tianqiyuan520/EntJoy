namespace NativeTranspiler.Analyzer.Common
{
    /// <summary>
    /// 生成器"静默降级"标记。
    ///
    /// 生成器遇到无法转译的构造时，**必须**把标记写进产物（而不是静默丢弃语句/返回 0）：
    /// NativeTranspiler.Tasks 的 NativeCompileTask 扫描产物里的 <see cref="Prefix"/> 前缀，
    /// 命中即让构建失败 —— 把问题挡在"编译通过但算错"之前。
    ///
    /// 历史教训（两处真实事故，都是"编译通过但算错"）：
    ///   1. `unchecked { ... }` 语句块不转译 → 生成**空函数体**（非 void 函数无 return = C++ UB，
    ///      调用时随机访问违例）；
    ///   2. `histPtr[key]++` 被翻成 `/* unsupported expr */ 0;` → 写入丢失且无任何提示。
    ///
    /// 新增"跳过某个构造"的分支时，请一律写标记并让构建期扫描拦下它。
    /// </summary>
    internal static class UnsupportedMarkers
    {
        /// <summary>构建期扫描用前缀（NativeCompileTask.CheckGeneratedMarkers 只认这个前缀）。</summary>
        public const string Prefix = "__ENTJOY_UNSUPPORTED";

        /// <summary>表达式级：该表达式无法转译（替换成它的占位符会算错）。</summary>
        public const string Expr = Prefix + "_EXPR__";

        /// <summary>语句级：该语句无法转译（整条语句会被丢弃 / 生成非法体）。</summary>
        public const string Stmt = Prefix + "_STMT__";
    }
}
