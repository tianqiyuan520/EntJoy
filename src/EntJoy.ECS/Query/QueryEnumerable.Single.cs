using System;
using System.Runtime.CompilerServices;

namespace EntJoy.ECS
{
    public unsafe ref struct QueryEnumerable<T0> where T0 : struct
    {
        private readonly EntityManager _entityManager;
        private readonly QueryBuilder _builder;

        internal QueryEnumerable(EntityManager entityManager, QueryBuilder builder)
        {
            _entityManager = entityManager;
            _builder = builder;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public QueryEnumerator<T0> GetEnumerator()
            => new QueryEnumerator<T0>(_entityManager, _builder);

        /// <summary>按段推进 + 内联回调：与多组件形态同构，循环体里没有逐实体游标状态。</summary>
        public void ForEach<TAction>(ref TAction action)
            where TAction : struct, IQueryForeach<T0>
        {
            var cursor = new QueryTraversalCursor(_entityManager, _builder, ComponentTypes<T0>.Share);
            // 块级过滤（shared / changed）整块要么全要要么全不要；逐实体过滤（关系目标、启用位）
            // 由 RangeEnd 给出一段连续可用槽位。两者都按段推进，循环里没有逐实体游标。
            int next = 0;
            while (true)
            {
                cursor = QueryTraversalCursor.Next(cursor, next);
                if (!cursor.HasChunk) break;
                int start = cursor.Slot;
                int end = cursor.RangeEnd;
                if (end > start)
                {
                    var column0 = (T0*)cursor.ComponentBase(0);
                    for (int slot = start; slot < end; slot++)
                        action.Update(ref column0[slot]);
                    next = end;
                }
                else next = start + 1;
            }
        }
    }

    public unsafe ref struct QueryEnumerator<T0> where T0 : struct
    {
        private static readonly ComponentType[] Columns = ComponentTypes<T0>.Share;
        private QueryTraversalCursor _cursor;
        private readonly QueryReadState _readState;
        // 相对连续区间末尾的偏移；有效实体为负数，0 表示区间耗尽。
        private int _slotOffset;
        private int _rangeEnd;
        private T0* _base0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal QueryEnumerator(EntityManager manager, QueryBuilder builder)
        {
            _cursor = new QueryTraversalCursor(manager, builder, Columns);
            _readState = new QueryReadState(manager, builder.HasRelationshipFilter || ComponentTypeManager.IsRelationType<T0>());
            _slotOffset = 0;
            _rangeEnd = 0;
            _base0 = null;
        }

        [SkipLocalsInit]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool MoveNext()
        {
            _slotOffset++;
            // 区间内只递增槽位；跨区间时再校验借用并解析过滤和列基址。
            if (_slotOffset < 0) return true;
            _readState.ValidateTracked();
            var cursor = QueryTraversalCursor.Next(_cursor, _slotOffset + _rangeEnd);
            _cursor = cursor;
            if (cursor.HasChunk)
            {
                _rangeEnd = cursor.RangeEnd;
                _slotOffset = cursor.Slot - _rangeEnd;
                _base0 = (T0*)cursor.ComponentBase(0) + _rangeEnd;
            }
            else
            {
                ResetRange();
            }
            // 区间内 _slotOffset 恒为负；HasChunk 时 RangeEnd 必大于 0，故该判据与基址判空等价。
            return _slotOffset < 0;
        }

        private void ResetRange()
        {
            _rangeEnd = 0;
            _slotOffset = 0;
            _base0 = null;
        }

        public EntityQueryResult<T0> Current
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                // 借用校验收敛到 MoveNext 的块边界；这里只拒绝越界读取。
                if (_slotOffset >= 0) throw new InvalidOperationException();
                return new EntityQueryResult<T0>(_base0 + _slotOffset);
            }
        }
    }

    // 结果对象只携带指针：借用校验由枚举器在块边界完成，结构变更后继续解引用属未定义行为。
    public unsafe readonly struct EntityQueryResult<T0> where T0 : struct
    {
        private readonly T0* _c0;

        /// <summary>裸指针构造由调用方保证寿命，不附带 World 借用版本。</summary>
        public EntityQueryResult(T0* c0)
        {
            _c0 = c0;
        }

        public ref readonly T0 ReadOnlyComp0
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { Validate(); return ref Unsafe.AsRef<T0>(_c0); }
        }

        public ref T0 Comp0
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                Validate();
                ComponentTypeManager.CheckWritableAccess<T0>();
                return ref Unsafe.AsRef<T0>(_c0);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void Validate()
        {
            if (_c0 == null) throw new InvalidOperationException("The query result has not been initialized.");
        }
    }
}
