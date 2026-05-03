using System.Runtime.CompilerServices;

namespace EntJoy
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
