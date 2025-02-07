using System;

namespace EntJoy
{
    public abstract class StructArray
    {
        public abstract void CopyComponentTo(int sourceIndex, StructArray dest, int destIndex);
    }

    public class StructArray<T> : StructArray where T : struct
    {
        public T[] components;

        public StructArray(int capacity)
        {
            components = new T[capacity];
        }

        public ref T GetComponentRef(int index)
        {
            return ref components[index];
        }
        
        public override void CopyComponentTo(int sourceIndex, StructArray dest, int destIndex)
        {
            var target = (StructArray<T>)dest;
            target.components[destIndex] = components[sourceIndex];
        }
    }
}