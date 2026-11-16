using System;

namespace EntJoy.ECS
{
    /// <summary>
    /// 延迟结构变更的作用域：<c>using (world.BeginDeferred()) { ... }</c>。
    /// 作用域内的结构变更进队列，Dispose 时由最外层作用域统一回放。
    /// </summary>
    public readonly struct DeferredScope : IDisposable
    {
        private readonly World _world;
        private readonly DeferredCommandBuffer _buffer;

        internal DeferredScope(World world, DeferredCommandBuffer buffer)
        {
            _world = world;
            _buffer = buffer;
        }

        public void Dispose() => _world.EndDeferred(_buffer);
    }
}
