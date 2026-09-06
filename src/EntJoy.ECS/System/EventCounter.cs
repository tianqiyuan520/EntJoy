using System;
using System.Collections.Generic;

namespace EntJoy.ECS
{
    /// <summary>
    /// 事件计数器：用于 RunWhen 条件判断，每帧末重置。
    /// 加锁：SystemRunner.EventCounter 为 public，用户代码（含多线程）可能并发调用。
    /// </summary>
    public class EventCounter
    {
        private readonly Dictionary<Type, long> _counters = new();
        private readonly object _sync = new();

        /// <summary>
        /// 增加事件计数
        /// </summary>
        public void Increment<T>() where T : struct
        {
            lock (_sync)
            {
                var type = typeof(T);
                _counters.TryGetValue(type, out var count);
                _counters[type] = count + 1;
            }
        }

        /// <summary>
        /// 减少事件计数
        /// </summary>
        public void Decrement<T>() where T : struct
        {
            lock (_sync)
            {
                var type = typeof(T);
                if (_counters.TryGetValue(type, out var count) && count > 0)
                    _counters[type] = count - 1;
            }
        }

        /// <summary>
        /// 获取事件计数
        /// </summary>
        public long GetCount<T>() where T : struct
        {
            lock (_sync)
            {
                _counters.TryGetValue(typeof(T), out var count);
                return count;
            }
        }

        /// <summary>
        /// 获取事件计数（非泛型版本，用于反射调用）
        /// </summary>
        public long GetCount(Type eventType)
        {
            lock (_sync)
            {
                _counters.TryGetValue(eventType, out var count);
                return count;
            }
        }

        /// <summary>按增量累加事件计数（非泛型，帧末统计事件流用）。</summary>
        public void Add(Type eventType, long delta)
        {
            if (delta == 0) return;
            lock (_sync)
            {
                _counters.TryGetValue(eventType, out var count);
                _counters[eventType] = count + delta;
            }
        }

        /// <summary>
        /// 帧末重置所有计数
        /// </summary>
        public void Reset()
        {
            lock (_sync)
            {
                _counters.Clear();
            }
        }
    }
}
