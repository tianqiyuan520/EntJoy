namespace EntJoy
{
    public ref struct ArchetypeEnumerator<T> where T : struct
    {
        private StructArray<T> _array;
        private int _index;

        public ArchetypeEnumerator(StructArray<T> array)
        {
            _array = array;
            _index = -1;
        }

        public bool MoveNext() => ++_index < _array.Length;
        public ref T Current => ref _array.GetRef(_index);
    }
    
    public ref struct ArchetypeQuery<T> where T : struct
    {
        private StructArray<T> _array;

        public ArchetypeQuery(StructArray<T> array)
        {
            _array = array;
        }

        public ArchetypeEnumerator<T> GetEnumerator() => new(_array);
    }
}