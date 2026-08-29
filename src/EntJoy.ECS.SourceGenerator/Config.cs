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

        /// <summary>IJobChunk 接口名（[NativeTranspile] 时同样由本生成器产出扩展方法）。</summary>
        public const string IJobChunk = "IJobChunk";

        /// <summary>Job 执行方法名。</summary>
        public const string Execute = "Execute";

        /// <summary>IJobEntity 声明所在命名空间（src/EntJoy.ECS/IJobEntity.cs，与 IJobChunk 同 `EntJoy.ECS`），防同名接口误判。</summary>
        public const string NamespaceEntJoy = "EntJoy";
        public const string NamespaceEntJoyECS = "EntJoy.ECS";

        // ─── QueryTupleSourceGenerator（N 元组查询） ───

        /// <summary>查询方法名（world.Query&lt;T0..Tn&gt;()）。</summary>
        public const string QueryMethod = "Query";

        /// <summary>Chunk 级查询方法名（world.QueryChunks&lt;T0..Tn&gt;()）。</summary>
        public const string QueryChunksMethod = "QueryChunks";

        /// <summary>QueryBuilder.WithAll&lt;T0..Tn&gt;() 方法名。</summary>
        public const string WithAllMethod = "WithAll";

        /// <summary>World 类型全名（语义匹配目标：只处理 World 上的 Query 调用）。</summary>
        public const string WorldFullName = "EntJoy.ECS.World";

        /// <summary>QueryBuilder 类型全名（语义匹配目标：WithAll N 元组扩展方法的接收者）。</summary>
        public const string QueryBuilderFullName = "EntJoy.ECS.QueryBuilder";

        /// <summary>最小元组数量：2 元组库内已有，生成器只处理 N ≥ 3。</summary>
        public const int MinTupleArity = 3;

        // ─── ECSComponentSourceGenerator（[ECSComponent] 补接口） ───

        /// <summary>ECSComponent 特性类名（语义匹配；用户写 [ECSComponent]，AttributeClass.Name 恒为完整类名）。</summary>
        public const string ECSComponentAttribute = "ECSComponentAttribute";

        // ─── SystemRegistrationSourceGenerator（自动收集 ISystem） ───

        /// <summary>ISystem 接口名（System 自动收集的语义匹配目标）。</summary>
        public const string ISystem = "ISystem";

        /// <summary>DisableAutoCreation 特性类名（带此特性的 System 跳过自动收集）。</summary>
        public const string DisableAutoCreation = "DisableAutoCreationAttribute";

        // ─── ReactiveSystemSourceGenerator（[Reactive] Observer 订阅） ───

        /// <summary>Reactive 特性类名（语义匹配）。</summary>
        public const string ReactiveAttribute = "ReactiveAttribute";

        /// <summary>ReadOnlySpan 类型名（Execute 参数推导组件类型的载体）。</summary>
        public const string ReadOnlySpan = "ReadOnlySpan";
    }
}
