
using EntJoy;
using Godot;

internal class test
{
    public struct aa : ISystem<Position, Vel>
    {
        public void Execute(ref Entity entity, ref Position t0, ref Vel t1)
        {
        }

        public void init()
        {
            var xx = 1123;
        }
    }
}