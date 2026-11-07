namespace EntJoy.ECS
{
    /// <summary>主线程只读查询，返回值副本；结构或关系版本变化后需重新获取。</summary>
    public struct ReadOnlyComponentLookup<T> where T : struct
    {
        private ComponentLookup<T> _lookup;
        private readonly QueryReadState _state;

        internal ReadOnlyComponentLookup(EntityManager manager)
        {
            _lookup = new ComponentLookup<T>(manager);
            _state = new QueryReadState(manager, ComponentTypeManager.IsRelationType<T>());
        }

        public T this[Entity entity]
        {
            get
            {
                _state.Validate();
                return _lookup.ReadOnly(entity);
            }
        }
    }
}
