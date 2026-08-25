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
            World.DefaultWorld.CurrentFrame = _currentFrame;

            var layers = _graph.GetLayers();
            foreach (var layer in layers)
            {
                foreach (var slot in layer)
                {
                    ExecuteSystem(slot);
                }
            }
            _eventCounter.Reset();
        }

        private void ExecuteSystem(SystemSlot slot)
        {
            var runWhenAttr = slot.SystemType.GetCustomAttribute<RunWhenAttribute>();
            if (runWhenAttr != null && _eventCounter.GetCount(runWhenAttr.EventType) == 0)
                return;

            var system = _systemInstances[slot.SystemType];
            system.OnUpdate();
        }
    }
}