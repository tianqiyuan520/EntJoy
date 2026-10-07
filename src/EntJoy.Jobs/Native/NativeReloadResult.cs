namespace EntJoy.JobSystem
{
    /// <summary>
    /// 一次 <see cref="NativeJobScheduler.ReloadNativeLibrary"/> 的结局。
    /// 拒绝**不抛异常**，用本枚举区分原因；只有编程错误（`path` 为 null/空）才抛。
    /// </summary>
    public enum NativeReloadOutcome
    {
        /// <summary>句柄真的换了 ⇒ 成功。</summary>
        Swapped = 0,
        /// <summary>路径有效但句柄没变（该模块已加载 / 同一份文件）。</summary>
        NoChange,
        /// <summary>托管回退档：没有原生 `NativeTranspiled` 可换。</summary>
        NotApplicable,
        /// <summary>目标文件不存在。</summary>
        FileNotFound,
        /// <summary>加载新模块失败（`NativeLibrary.Load` 抛）。</summary>
        LoadFailed,
        /// <summary>布局守卫：DLL 旁没有布局清单，而本进程有组件 ⇒ 无法验证。</summary>
        LayoutManifestMissing,
        /// <summary>布局守卫：清单解析不了。</summary>
        LayoutManifestInvalid,
        /// <summary>布局守卫：清单声明的程序集在本进程一个组件都没登记（元数据未加载 / 是另一次构建）。</summary>
        LayoutScopeMissing,
        /// <summary>布局守卫：作用域内布局不一致（缺 / 多 / 哈希不同）。</summary>
        LayoutMismatch,
        /// <summary>无法排空在飞 job（当前 NativeDll 没有排空导出），且未显式放行。</summary>
        DrainNotAvailable,
    }

    /// <summary>热重载的结果：句柄换没换 + 为什么被拒。</summary>
    public readonly struct NativeReloadResult
    {
        public NativeReloadOutcome Outcome { get; }
        /// <summary>句柄是否真的换了（等价于 <see cref="Outcome"/> == Swapped）。</summary>
        public bool Swapped => Outcome == NativeReloadOutcome.Swapped;
        /// <summary>本次重载前是否成功排空了在飞 job。被拒时为 false。</summary>
        public bool Drained { get; }
        /// <summary>调用结束时的委托缓存代次。</summary>
        public int Generation { get; }
        /// <summary>拒绝原因（成功时为空串）。可直接展示给用户。</summary>
        public string Message { get; }

        internal NativeReloadResult(NativeReloadOutcome outcome, string message, bool drained, int generation)
        {
            Outcome = outcome;
            Message = message ?? "";
            Drained = drained;
            Generation = generation;
        }

        internal static NativeReloadResult Refuse(NativeReloadOutcome outcome, string message, bool drained, int generation)
            => new NativeReloadResult(outcome, message, drained, generation);

        public override string ToString()
            => Swapped
                ? $"[HOTRELOAD] {Outcome} generation={Generation} drained={Drained}"
                : $"[HOTRELOAD] {Outcome}: {Message}";
    }
}
