using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace EntJoy.ECS
{
    /// <summary>查询和只读 lookup 的借用版本；由生成代码与运行时共用。</summary>
    public readonly struct QueryReadState
    {
        internal static readonly QueryReadState Untracked = new(new QueryBorrowVersion());
        private readonly QueryBorrowToken _token;
        internal bool IsCurrent => _token != null && !_token.Invalidated;

        private QueryReadState(QueryBorrowVersion source)
        {
            _token = source.Borrow();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public QueryReadState(EntityManager manager, bool trackRelationships)
        {
            _token = manager?.GetQueryBorrowVersion(trackRelationships).Borrow();
            Validate();
        }

        /// <summary>校验由 EntityManager 创建的查询借用；默认状态不能用于此入口。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ValidateTracked()
        {
            if (_token == null) throw new InvalidOperationException("The query has not been initialized.");
            if (_token.Invalidated)
                throw _token.InvalidatedException();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Validate()
        {
            if (_token != null && _token.Invalidated)
                throw _token.InvalidatedException();
        }
    }

    // 每个管理器分别保存结构和关系借用源；当前代次按需创建并由视图共享。
    internal sealed class QueryBorrowVersion
    {
        private QueryBorrowToken _current;
        private bool _disposed;

        internal QueryBorrowToken Borrow()
        {
            var token = Volatile.Read(ref _current);
            if (token != null) return token;
            token = new QueryBorrowToken(this);
            return Interlocked.CompareExchange(ref _current, token, null) ?? token;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal void Invalidate()
        {
            // 没有借阅者时不必做带栅栏的交换：新借阅会拿到反映改动后状态的令牌。
            if (Volatile.Read(ref _current) == null) return;
            var token = Interlocked.Exchange(ref _current, null);
            if (token != null) token.Invalidated = true;
        }

        internal void Dispose()
        {
            _disposed = true;
            Invalidate();
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal Exception InvalidatedException() => _disposed
            ? new ObjectDisposedException(nameof(EntityManager))
            : new InvalidOperationException("Structural or relationship change invalidated the query or read-only view.");
    }

    internal sealed class QueryBorrowToken
    {
        private readonly QueryBorrowVersion _source;
        internal volatile bool Invalidated;

        internal QueryBorrowToken(QueryBorrowVersion source) => _source = source;

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal Exception InvalidatedException() => _source.InvalidatedException();
    }
}
