using System;

namespace EntJoy
{
    public unsafe ref struct QueryEnumerable<T0, T1> where T0 : struct where T1 : struct
    {

        public QueryEnumerator<T0, T1> GetEnumerator()
            => new QueryEnumerator<T0, T1>();
    }

    //迭代
    public unsafe ref struct QueryEnumerator<T0, T1>
        where T0 : struct
        where T1 : struct
    {

        public bool MoveNext()
        {
            throw new NotImplementedException();
        }

        public EntityQueryResult<T0, T1> Current
        {
            get
            {
                throw new NotImplementedException();
            }
        }


    }

    public readonly ref struct EntityQueryResult<T0, T1> where T0 : struct where T1 : struct
    {
        private readonly RefRW<T0> _comp0;
        private readonly RefRW<T1> _comp1;

        public EntityQueryResult(ref T0 comp0, ref T1 comp1)
        {
            _comp0 = new RefRW<T0>(ref comp0);
            _comp1 = new RefRW<T1>(ref comp1);
        }

        public RefRW<T0> Comp0 => _comp0;
        public RefRW<T1> Comp1 => _comp1;

        public void Deconstruct(out RefRW<T0> comp0, out RefRW<T1> comp1)
        {
            comp0 = _comp0;
            comp1 = _comp1;
        }
    }

}
