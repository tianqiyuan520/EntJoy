namespace EntJoy
{
    public ref struct ArchetypeEnumerator<T> where T : struct  // 原型枚举器
    {
        private StructArray<T> _array;  // 组件数据数组
        private int _index;  // 当前索引

        public ArchetypeEnumerator(StructArray<T> array)
        {
            _array = array; 
            _index = -1; 
        }

        public bool MoveNext() => ++_index < _array.Length;  // 移动到下一个元素
        public ref T Current => ref _array.GetRef(_index);  // 获取当前元素引用
    }

    public ref struct ArchetypeQuery<T> where T : struct  // 原型查询结构
    {
        public StructArray<T> _array;  // 组件数据数组
        public int Count => _array.Length;
        public ArchetypeQuery() { }
        public ArchetypeQuery(StructArray<T> array)
        {
            _array = array;
        }
        

        public ArchetypeEnumerator<T> GetEnumerator() => new(_array);  // 获取枚举器
    }
}
