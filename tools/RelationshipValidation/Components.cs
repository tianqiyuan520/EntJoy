using EntJoy.ECS;

namespace RelationshipValidation;

public struct State : IComponentData { public int Seen; }
[RelationshipComponent(Multiple = true)] public struct MultiLinks : IRelationComponent { public RelationSlot Target; }
[RelationshipComponent(MaxTargets = 8)] public partial struct FixedLinks : IRelationComponent { public RelationSlot Target; }
[RelationshipComponent(Multiple = true, DeleteWithTarget = true)] public struct CascadeLinks : IRelationComponent { public RelationSlot Target; }
public struct StressActive : IComponentData, IEnableableComponent { }
public struct MultiReadJob : IJobEntity
{
    public void Execute(ref State state) { state.Seen++; }
}
public struct Link : IComponentData, IRelationComponent { public RelationSlot Target; public int Data; }
[RelationshipComponent(ExclusiveTarget = true)]
public struct Unique : IComponentData, IRelationComponent { public RelationSlot Target; public int Data; }
[RelationshipComponent(Hierarchy = true)]
public struct Parent : IComponentData, IRelationComponent { public RelationSlot Target; public int Data; }
[RelationshipComponent(DeleteWithTarget = true)]
public struct Owns : IComponentData, IRelationComponent { public RelationSlot Target; public int Data; }
public struct ReadLinkJob : IJobEntity
{
    public void Execute(ref State state, in Link link) { state.Seen += 1; }
}
