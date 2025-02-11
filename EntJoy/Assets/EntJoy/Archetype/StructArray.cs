using System;

namespace EntJoy
{
    public abstract class StructArray
    {
        public StructArray(int defaultCapacity) { }
        public abstract void EnsureCapacity(int newCapacity);
        public abstract void Move(int from, int to);
        public abstract void SetDefault(int index);
        public abstract void CopyTo(int sourceIndex, StructArray target, int targetIndex);
    }

    public class StructArray<T> : StructArray
        where T : struct
    {
        public int Capacity { get; protected set; }
        public int Length { get; protected set; }
        public T[] Data;

        public StructArray(int defaultCapacity) : base(defaultCapacity)
        {
            Data = new T[defaultCapacity];
            Capacity = defaultCapacity;
            Length = 0;
        }
        
        public override void EnsureCapacity(int newCapacity)
        {
            if (Capacity < newCapacity)
            {
                Array.Resize(ref Data, newCapacity);
                Capacity = newCapacity;
            }
        }

        public void Add(T item)
        {
            EnsureCapacity(Length + 1);
            unchecked
            {
                Data[Length] = item;
                Length++;
            }
        }

        public ref T GetRef(int index)
        {
            return ref Data[index];
        }
        
        public void SetValue(int index, T value)
        {
            unchecked
            {
                Data[index] = value;
            }
        }

        public override void Move(int from, int to)
        {
            Data[to] = Data[from];
            Data[from] = default;
            Length--;
        }

        public override void SetDefault(int index)
        {
            Data[index] = default;
        }

        public override void CopyTo(int sourceIndex, StructArray target, int targetIndex)
        {
            var genericTarget = (StructArray<T>)target;
            genericTarget.SetValue(targetIndex, Data[sourceIndex]);
        }
    }
}