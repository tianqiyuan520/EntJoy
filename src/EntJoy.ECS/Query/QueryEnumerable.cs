using System;
using System.Runtime.CompilerServices;
using EntJoy.ECS.JobSystem;
using EntJoy.JobSystem;

namespace EntJoy.ECS
{
    public unsafe ref struct QueryEnumerable<T0, T1> where T0 : struct where T1 : struct
    {
        private readonly EntityManager _entityManager;
        private readonly QueryBuilder _builder;

        internal QueryEnumerable(EntityManager entityManager, QueryBuilder builder)
        {
            _entityManager = entityManager;
            _builder = builder;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public QueryEnumerator<T0, T1> GetEnumerator()
            => new QueryEnumerator<T0, T1>(_entityManager, _builder);
    }

    public unsafe ref struct QueryEnumerator<T0, T1> where T0 : struct where T1 : struct
    {
        private static readonly ComponentType[] Columns = ComponentTypes<T0, T1>.Share;
        private QueryTraversalCursor _cursor;
        private readonly QueryReadState _readState;
        // 相对连续区间末尾的偏移；有效实体为负数，0 表示区间耗尽。
        private int _slotOffset;
        private int _rangeEnd;
        private T0* _base0;
        private T1* _base1;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal QueryEnumerator(EntityManager manager, QueryBuilder builder)
        {
            _cursor = new QueryTraversalCursor(manager, builder, Columns);
            _readState = new QueryReadState(manager, builder.HasRelationshipFilter || ComponentTypeManager.IsRelationType<T0>() || ComponentTypeManager.IsRelationType<T1>());
            _slotOffset = 0;
            _rangeEnd = 0;
            _base0 = null;
            _base1 = null;

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
                SetBase1(cursor.ComponentBase(1), _rangeEnd);
            }
            else
            {
                ResetRange();
            }
            // 区间内 _slotOffset 恒为负；HasChunk 时 RangeEnd 必大于 0，故该判据与基址判空等价。
            return _slotOffset < 0;
        }

        // 关系列可以是托管多目标（无列），此时 Comp1 不可用。
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void SetBase1(nint column1, int count)
        {
            _base1 = column1 == 0 && ComponentTypeManager.IsRelationType<T1>() ? null : (T1*)column1 + count;
        }

        private void ResetRange()
        {
            _rangeEnd = 0;
            _slotOffset = 0;
            _base0 = null;
            _base1 = null;
        }

        public EntityQueryResult<T0, T1> Current
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                // 借用校验收敛到 MoveNext 的块边界；这里只拒绝越界读取。
                if (_slotOffset >= 0) throw new InvalidOperationException();
                return new EntityQueryResult<T0, T1>(_base0 + _slotOffset,
                    ComponentTypeManager.IsRelationType<T1>() && _base1 == null ? null : _base1 + _slotOffset);
            }
        }
    }

    // 结果对象只携带指针：借用校验由枚举器在 MoveNext 的块边界完成，结构变更后继续解引用属未定义行为。
    public unsafe readonly struct EntityQueryResult<T0, T1> where T0 : struct where T1 : struct
    {
        private readonly T0* _c0;
        private readonly T1* _c1;

        /// <summary>裸指针构造由调用方保证寿命，不附带 World 借用版本。</summary>
        public EntityQueryResult(T0* c0, T1* c1)
        {
            _c0 = c0;
            _c1 = c1;
        }

        public ref readonly T0 ReadOnlyComp0
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => ref Unsafe.AsRef<T0>(_c0);
        }

        public ref T0 Comp0
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                // 关系列只能读：直接写会绕过关系索引。这是按类型定死的判据，不随运行期状态变化。
                if (ComponentTypeManager.IsRelationType<T0>())
                    throw new InvalidOperationException($"Relationship '{typeof(T0)}' requires read-only access; use EntityManager or DeferredCommandBuffer to change it.");
                return ref Unsafe.AsRef<T0>(_c0);
            }
        }

        public ref readonly T1 ReadOnlyComp1
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                if (_c1 == null) throw new NotSupportedException("Managed multi-target relations have no component column; use GetRelationships or CopyRelationships.");
                return ref Unsafe.AsRef<T1>(_c1);
            }
        }

        public ref T1 Comp1
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get
            {
                if (ComponentTypeManager.IsRelationType<T1>())
                    throw new InvalidOperationException($"Relationship '{typeof(T1)}' requires read-only access; use EntityManager or DeferredCommandBuffer to change it.");
                if (_c1 == null) throw new NotSupportedException("Managed multi-target relations have no component column; use GetRelationships or CopyRelationships.");
                return ref Unsafe.AsRef<T1>(_c1);
            }
        }
    }

}
