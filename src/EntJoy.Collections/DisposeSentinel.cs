using System;
using System.Collections.Concurrent;
using System.Threading;

/// <summary>
/// 泄漏检测（Debug 构建专用）。
///
/// 
/// - 不能作为 NativeArray/NativeList 的托管引用字段——那会使容器 struct 含托管引用，
///   Debug 下破坏 `unmanaged` 约束（含容器的 Job 无法进 Batch/JobSystem 的 blittable 快路径）。
/// - 因此 sentinel 存于**静态表**（key = 容器的 safety handle index，blittable int），
///   容器只持有 int，不持有对象引用 → struct 保持 blittable。
/// - 泄漏检测：finalizer 无法在 struct 被丢弃时触发（struct 无析构），故改为：
///   容器 Dispose 时注销 sentinel；容器未被 Dispose 时 sentinel 留在表中，
///   由 <see cref="DumpLeaks"/>（帧末/World Dispose 时调用）扫描表报告未释放容器。
///
/// 语义：safety handle index 在容器生命周期内唯一（Allocate 时不复用直到 Dispose 归还），
/// 故 (index → sentinel) 映射稳定可靠。
/// </summary>
public class DisposeSentinel
{
    private static readonly ConcurrentDictionary<int, DisposeSentinel> s_registry = new();
    private static int s_nextId = 0;

    /// <summary>当前未 Dispose 的 NativeContainer 数量（内存分析器用；仅 Debug 注册，Release 恒 0）。</summary>
    public static int LeakedCount => s_registry.Count;

    private readonly int _id;
    private readonly int _safetyIndex;
    private volatile bool _disposed;

    [ThreadStatic] private static string? s_lastCallSite;

    public DisposeSentinel(int safetyIndex)
    {
        _id = Interlocked.Increment(ref s_nextId);
        _safetyIndex = safetyIndex;
    }

    /// <summary>将 sentinel 注册到静态表（容器构造时调用，仅 Debug）。</summary>
    public static DisposeSentinel Register(int safetyIndex, string callSite)
    {
        var s = new DisposeSentinel(safetyIndex) { _callSite = callSite };
        s_registry[safetyIndex] = s;
        return s;
    }

    private string? _callSite;

    /// <summary>容器 Dispose 时注销（不再泄漏）。</summary>
    public void Unregister()
    {
        if (_disposed) return;
        _disposed = true;
        s_registry.TryRemove(_safetyIndex, out _);
    }

    /// <summary>按 safety index 注销（容器 Dispose 时调用，仅 Debug）。</summary>
    public static void Unregister(int safetyIndex)
    {
        s_registry.TryRemove(safetyIndex, out _);
    }

    /// <summary>
    /// 扫描静态表，报告所有未 Dispose 的容器。
    /// 调用时机：帧末 / World Dispose / 测试收尾。
    /// </summary>
    public static void DumpLeaks()
    {
        if (s_registry.IsEmpty) return;
        Console.Error.WriteLine($"[EntJoy.Collections] {s_registry.Count} un-disposed NativeContainers detected:");
        foreach (var kv in s_registry)
        {
            Console.Error.WriteLine($"  safetyIdx={kv.Key} created at: {kv.Value._callSite}");
        }
        s_registry.Clear();
    }
}