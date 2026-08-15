using System.Threading;
namespace EntJoy.JobSystem
{


// 线程计数器
public class ThreadCounter
{
    private int _count;
    public void RecordCurrentThread() => Interlocked.Increment(ref _count);
    public int Count => _count;
}
}