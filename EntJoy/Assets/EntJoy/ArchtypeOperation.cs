using System;

namespace EntJoy
{
    public class ArchOperation
    {
        // 从一个原型移动组件集到另一个原型中
        public static void Move(ref ArchetypeLocate sourceLocate, Archetype dest)
        {
            var srcArch = sourceLocate.Archetype;
            for (int i = 0; i < srcArch.Types.Length; i++)
            {
                if (!dest.Has(srcArch.Types[i]))
                {
                    continue;
                }
                
                
                
            }
        }

        private static void Move(StructArray source, StructArray dest)
        {
            
            
        }
    }
}