using System;
using System.Collections.Generic;
using System.Reflection;

namespace EntJoy.ECS
{
    public class SystemRunner
    {
        private readonly World _world;
        private readonly ScheduleGraph _graph = new();
        private readonly Dictionary<Type, ISystem> _systemInstances = new();
        private readonly EventCounter _eventCounter = new();
        private long _currentFrame;

        public long CurrentFrame => _currentFrame;
        public EventCounter EventCounter => _eventCounter;

        public SystemRunner(World world)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
        }

        public void RegisterSystem<T>() where T : struct, ISystem
        {
            _graph.RegisterSystem<T>();
            _systemInstances[typeof(T)] = default(T);
        }

        public void PrintSchedule() => _graph.PrintSchedule();

        public void Update()
        {
            _currentFrame++;
            _world.CurrentFrame = _currentFrame;

            var layers = _graph.GetLayers();
            foreach (var layer in layers)
            {
                foreach (var slot in layer)
                {
                    ExecuteSystem(slot);
                }
            }
            _world.NextFrameEvents();  // 帧末交换事件双缓冲
            _eventCounter.Reset();
        }

        private void ExecuteSystem(SystemSlot slot)
        {
            var runWhenAttr = slot.SystemType.GetCustomAttribute<RunWhenAttribute>();
            if (runWhenAttr != null && _eventCounter.GetCount(runWhenAttr.EventType) == 0)
                return;

            var system = _systemInstances[slot.SystemType];

            // 多 World 隔离：System 内通过 World.DefaultWorld 访问实体时，临时指向所属 World。
            // 保存旧值，执行后恢复（支持嵌套 World / 手动切换场景）。
            var prev = World.DefaultWorld;
            World.DefaultWorld = _world;
            try
            {
                system.OnUpdate();
            }
            finally
            {
                World.DefaultWorld = prev;
            }
        }
    }
}