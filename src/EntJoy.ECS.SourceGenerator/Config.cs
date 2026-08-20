namespace EntJoy.ECS.SourceGenerator
{
    /// <summary>
    /// ECS 源生成器内的硬编码名/字符串常量集中管理（对照 NativeTranspiler 的 Config）：
    /// 独立程序集无法复用 NativeTranspiler.Analyzer.Common.Config，故本工程自持一份。
    /// </summary>
    internal static class Config
    {
        /// <summary>IJobEntity 接口名（语义匹配 + 语法谓词 Contains 均使用）。</summary>
        public const string IJobEntity = "IJobEntity";

        /// <summary>Job 执行方法名。</summary>
        public const string Execute = "Execute";

        /// <summary>IJobEntity 声明所在命名空间（src/EntJoy.ECS/IJobEntity.cs，与 IJobChunk 同 `EntJoy`），防同名接口误判。</summary>
        public const string NamespaceEntJoy = "EntJoy";
    }
}
