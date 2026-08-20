using System.Runtime.CompilerServices;
namespace EntJoy.JobSystem
{

public interface IJob
{
    [MethodImpl(MethodImplOptions.AggressiveOptimization | MethodImplOptions.AggressiveInlining)]
    void Execute();
}

public interface IJobParallelFor
{
    [MethodImpl(MethodImplOptions.AggressiveOptimization | MethodImplOptions.AggressiveInlining)]
    void Execute(int index);
}

public interface IJobFor
{
    [MethodImpl(MethodImplOptions.AggressiveOptimization | MethodImplOptions.AggressiveInlining)]
    void Execute(int index);
}

public interface IJobParallelForBatch
{
    [MethodImpl(MethodImplOptions.AggressiveOptimization | MethodImplOptions.AggressiveInlining)]
    void Execute(int startIndex, int count);
}

}
