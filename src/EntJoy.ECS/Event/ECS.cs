using System.Runtime.CompilerServices;

namespace EntJoy.ECS
{
    /// <summary>
    /// 事件总线静态入口：供 [NativeTranspile] Job 调用 SendEvent。
    /// NativeTranspiler 识别此调用并翻译为 C++ EventBuffer 写入，
    /// 不需要 World 引用（managed 类型在 C++ 层被消除）。
    /// </summary>
    public static class EventBus
    {
        /// <summary>
        /// 发送事件（Native Transpile 安全）。
        /// 在 NativeTranspile Job 中调用：EventBus.SendEvent(new MyEvent { ... })
        /// NativeTranspiler 翻译为 C++ __header->eventBufferHeaders[index] 写入。
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SendEvent<T>(in T evt) where T : unmanaged
        {
            World.DefaultWorld?.SendEvent(evt);
        }
    }
}