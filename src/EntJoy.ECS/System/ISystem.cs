using System.Runtime.CompilerServices;

namespace EntJoy.ECS
{
    public interface ISystem
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnCreate() { }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnUpdate() { }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnDestroy() { }

    }
}
