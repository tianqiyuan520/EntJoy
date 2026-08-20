namespace NativeTranspiler.Analyzer.Common
{
    /// <summary>
    /// 硬编码名/字符串字面量集中管理（消除各翻译器里散落的 .Name == "..." 魔数）。
    /// 纯命名常量化：展开后与原字符串逐字相同，行为不变。
    /// 集中维护，改 EntJoy 类型/Job 接口/方法/属性名时只需改此处。
    /// </summary>
    internal static class Config
    {
        // ============ 容器 / 数值类型名 ============
        public const string NativeList = "NativeList";
        public const string NativeArray = "NativeArray";
        public const string UnsafeList = "UnsafeList";
        public const string Span = "Span";
        public const string Float2 = "float2";
        public const string Int2 = "int2";
        public const string UInt2 = "uint2";

        // ============ Job 接口名 ============
        public const string IJob = "IJob";
        public const string IJobFor = "IJobFor";
        public const string IJobParallelFor = "IJobParallelFor";
        public const string IJobChunk = "IJobChunk";
        public const string IJobEntity = "IJobEntity";

        // ============ 方法名 ============
        public const string Execute = "Execute";
        public const string Resize = "Resize";
        public const string Add = "Add";
        public const string Exchange = "Exchange";
        public const string CompareExchange = "CompareExchange";
        public const string Likely = "Likely";
        public const string Unlikely = "Unlikely";
        public const string GetUnsafePtr = "GetUnsafePtr";
        public const string ArrayElementAsRef = "ArrayElementAsRef";
        public const string GetComponentDataNativeArray = "GetComponentDataNativeArray";
        public const string GetComponentDataSpan = "GetComponentDataSpan";
        public const string GetComponentDataPtr = "GetComponentDataPtr";

        // ============ 命名空间名 ============
        public const string NamespaceSystem = "System";
        public const string NamespaceEntJoy = "EntJoy";
        public const string NamespaceEntJoyJobSystem = "EntJoy.JobSystem";
        public const string NamespaceEntJoyCollections = "EntJoy.Collections";
        public const string NamespaceEntJoyMathematics = "EntJoy.Mathematics";

        // ============ Attribute 名 ============
        public const string NativeTranspileAttribute = "NativeTranspileAttribute";
    }
}
