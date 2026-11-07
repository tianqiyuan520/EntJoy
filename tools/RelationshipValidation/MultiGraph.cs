using EntJoy.ECS;

namespace RelationshipValidation;

// 独立整数邻接表是预期结果；验证不能从 ECS 的反向表推导预期边。
internal sealed class MultiGraph<T> : IDisposable where T : unmanaged, IRelationComponent
{
    public readonly World World = new("MultiGraphValidation");
    public EntityManager Manager => World.EntityManager;
    public readonly Entity[] Sources, Targets;
    public readonly int[,] Edges;
    public readonly int[] Seen;
    public readonly int Degree;
    public readonly bool Enabled;
    private readonly Entity[] _row, _reverse;
    private readonly int[] _counts;
    private readonly Dictionary<Entity, int> _sourceIndices;
    private readonly HashSet<Entity> _reverseSet;

    public MultiGraph(int size, int degree, bool enabled = false)
    {
        Degree = degree;
        Enabled = enabled;
        Sources = enabled ? Manager.CreateEntities(size, typeof(State), typeof(StressActive)) : Manager.CreateEntities(size, typeof(State));
        Targets = Manager.CreateEntities(Math.Max(32, degree * 4), typeof(State));
        Edges = new int[size, degree]; Seen = new int[size];
        _row = new Entity[degree]; _reverse = new Entity[size]; _counts = new int[Targets.Length];
        _sourceIndices = new Dictionary<Entity, int>(size); _reverseSet = new HashSet<Entity>(size);
        for (int i = 0; i < size; i++)
        {
            Seen[i] = i; Manager.SetComponent(Sources[i], new State { Seen = i });
            if (enabled) Manager.SetComponentEnabled<StressActive>(Sources[i], i % 5 != 0);
            _sourceIndices.Add(Sources[i], i);
            for (int slot = 0; slot < degree; slot++)
            {
                int target = slot == 0 ? 0 : 1 + (i * 17 + slot - 1) % (Targets.Length - 1);
                Edges[i, slot] = target;
                Manager.AddRelationship<T>(Sources[i], Targets[target]);
            }
        }
    }

    public bool HasTarget(int source, int target)
    {
        for (int slot = 0; slot < Degree; slot++) if (Edges[source, slot] == target) return true;
        return false;
    }

    public void Verify()
    {
        Array.Clear(_counts);
        for (int i = 0; i < Sources.Length; i++)
        {
            Require(Manager.GetRelationships<T>(Sources[i], _row) == Degree, "forward degree");
            Require(Manager.GetComponentReadOnly<State>(Sources[i]).Seen == Seen[i], "source payload");
            for (int slot = 0; slot < Degree; slot++)
            {
                var expected = Targets[Edges[i, slot]];
                Require(_row.AsSpan().Contains(expected), "forward edge identity");
                _counts[Edges[i, slot]]++;
            }
        }
        for (int target = 0; target < Targets.Length; target++)
        {
            int count = Manager.GetRelationsOf<T>(Targets[target], _reverse);
            Require(count == _counts[target], "reverse degree");
            _reverseSet.Clear();
            for (int i = 0; i < count; i++)
                Require(_sourceIndices.TryGetValue(_reverse[i], out int source) && HasTarget(source, target)
                    && _reverseSet.Add(_reverse[i]), "reverse edge identity");
        }
        Require(Manager.VerifyLocateTable(out _) == 0 && !SteadyScenario.InvalidSlabs(Manager), "storage invariants");
    }

    public Entity[] CopyInputs() => Sources.Concat(Targets).ToArray();

    public void VerifyCopies(Entity[] copies)
    {
        Require(copies.Length == Sources.Length + Targets.Length, "copy population");
        for (int i = 0; i < Sources.Length; i++)
        {
            Require(Manager.GetRelationships<T>(copies[i], _row) == Degree, "copy degree");
            for (int slot = 0; slot < Degree; slot++)
                Require(_row.AsSpan().Contains(copies[Sources.Length + Edges[i, slot]]), "copy target remapping");
            Require(Manager.GetComponentReadOnly<State>(copies[i]).Seen == Seen[i], "copy payload");
            if (Enabled) Require(Manager.IsComponentEnabled<StressActive>(copies[i]) == (i % 5 != 0), "copy enabled state");
        }
        for (int target = 0; target < Targets.Length; target++)
            Require(Manager.GetRelationsOf<T>(copies[Sources.Length + target], _reverse) == _counts[target], "copy reverse index");
    }

    public static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException($"M:N {typeof(T).Name}: {message}");
    }

    public void Dispose() => World.Dispose();
}
