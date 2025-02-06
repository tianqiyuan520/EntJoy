using System;
using Unity.Mathematics;

namespace EntJoy
{
    public unsafe class UnsafeTool
    {
        public static uint CalculateHash(ComponentType* componentTypes, int length)
        {
            ComponentType* dest = stackalloc ComponentType[length];
            for (int i = 0; i < length; i++)
            {
                dest[i] = componentTypes[i];
            }
            for (int i = 1; i < length; i++)
            {
                var current = dest[i];
                int j = i - 1;
                while (j >= 0 && dest[j].Id > current.Id) 
                {
                    dest[j + 1] = dest[j];
                    j--;
                }

                dest[j + 1] = current;
            }
            
            UInt32 hash = math.hash(dest, length * sizeof(ComponentType));
            return hash;
        }
    }
}