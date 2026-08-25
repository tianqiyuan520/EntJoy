using System;
using System.Collections.Generic;

namespace EntJoy.ECS
{
    /// <summary>
    /// 事件计数器：用于 RunWhen 条件判断
    /// 每帧末重置，配合 RunWhenAttribute 实现空闲跳过
    /// </summary>
    public class EventCounter
    {
        private readonly Dictionary<Type, long> _counters = new();

        /// <summary>
        /// 增加事件计数
        /// </summary>
        public void Increment<T>() where T : struct
        {
            var type = typeof(T);
            _counters.TryGetValue(type, out var count);
            _counters[type] = count + 1;
        }

        /// <summary>
        /// 减少事件计数
        /// </summary>
        public void Decrement<T>() where T : struct
        {
            var type = typeof(T);
            if (_counters.TryGetValue(type, out var count) && count > 0)
                _counters[type] = count - 1;
        }

        /// <summary>
        /// 获取事件计数
        /// </summary>
        public long GetCount<T>() where T : struct
        {
            _counters.TryGetValue(typeof(T), out var count);
            return count;
        }

        /// <summary>
        /// 获取事件计数（非泛型版本，用于反射调用）
        /// </summary>
        public long GetCount(Type eventType)
        {
            _counters.TryGetValue(eventType, out var count);
            return count;
        }

        /// <summary>
        /// 帧末重置所有计数
        /// </summary>
        public void Reset() => _counters.Clear();
    }
}