using System.Runtime.CompilerServices;

namespace EntJoy.ECS
{
    /// <summary>
    /// ECS SystemAPI 入口：C# 与 <c>[NativeTranspile]</c> 原生内核共用。
    /// 现只有 SendEvent；后续 ECS 系统级 API（查询 / 单例访问等）并入此处。
    /// </summary>
    public static class SystemAPI
    {
        /// <summary>发送事件；原生内核里由转译器翻译为 C++ EventBuffer 写入。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SendEvent<T>(in T evt) where T : unmanaged
        {
            World.DefaultWorld?.SendEvent(evt);
        }
    }
}
