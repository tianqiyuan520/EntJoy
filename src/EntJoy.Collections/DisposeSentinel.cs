using System;
using System.Threading;

/// <summary>
/// 泄露检测
/// </summary>
internal class DisposeSentinel
{
    private static int s_nextId = 0;
    private int _id;

    public DisposeSentinel()
    {
        _id = Interlocked.Increment(ref s_nextId);
    }

    ~DisposeSentinel()
    {
        Console.Error.WriteLine($"NativeArray with id {_id} was not disposed! Possible memory leak.");
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }
}