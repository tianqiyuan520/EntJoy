namespace EntJoy
{
    public readonly ref struct RefRW<T> where T : struct
    {
        private readonly ref T _value;

        public RefRW(ref T value)
        {
            _value = ref value;
        }

        public ref T ValueRW => ref _value;
        public ref readonly T ValueRO => ref _value;
    }

    public readonly ref struct RefRO<T> where T : struct
    {
        private readonly ref readonly T _value;

        public RefRO(in T value)
        {
            _value = ref value;
        }

        public ref readonly T ValueRO => ref _value;
    }
}
