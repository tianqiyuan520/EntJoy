using System;

namespace EntJoy
{
    public class World
    {
        public readonly EntityComponentStorage Storage = new();

        public void Add<T>() where T : struct
        {
            
            
        }
    }
}