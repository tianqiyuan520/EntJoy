using System.Runtime.CompilerServices;

namespace EntJoy
{
    public interface GetArchetypeChunkID
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        virtual void GetChunkID(int id) { }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        virtual void GetArchetypeID(int id) { }
    }

    public interface ISystem<T0> : GetArchetypeChunkID
        where T0 : struct
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        virtual unsafe void _execute(Entity* entities, T0* components, int Count, int LimitCount) { }
        virtual void Execute(ref Entity entity, ref T0 component0) { }
        virtual void Execute(ref T0 component0) { }
    }

    public interface ISystem<T0, T1> : GetArchetypeChunkID
        where T0 : struct
        where T1 : struct
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        virtual unsafe void _execute(Entity* entities, T0* components0, T1* components1, int Count, int LimitCount) { }
        virtual void Execute(ref Entity entity, ref T0 component0, ref T1 component1){ }
        virtual void Execute(ref T0 component0, ref T1 component1) { }
    }
}