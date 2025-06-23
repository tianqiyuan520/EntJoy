using Godot;
using System;
using System.Linq;

namespace EntJoy
{
    //专门输出原型等的信息
    public partial class World
    {
        public unsafe void ReportAllArchetypes()
        {
            for (int i = 0; i < allArchetypes.Count(); i++)
            {
                if (allArchetypes[i] != null)
                {
                    GD.Print(allArchetypes[i].GetMemoryLayoutInfo());
                }

            }
        }
    }
}
