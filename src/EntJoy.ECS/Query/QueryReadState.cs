using System.Runtime.CompilerServices;

namespace EntJoy.ECS
{
    /// <summary>查询和只读 lookup 的借用版本；由生成代码与运行时共用。</summary>
    public readonly struct QueryReadState
    {
        private readonly EntityManager _manager;
        private readonly int _structuralVersion;
        private readonly long _relationshipVersion;

        public QueryReadState(EntityManager manager, bool trackRelationships)
        {
            _manager = manager;
            _structuralVersion = manager?.StructuralVersion ?? 0;
            _relationshipVersion = trackRelationships && manager != null ? manager.RelationshipVersion : -1;
            Validate();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void ValidateRelationshipView()
        {
            if (_relationshipVersion >= 0) Validate();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Validate() => _manager?.ValidateQueryReadState(_structuralVersion, _relationshipVersion);
    }
}
