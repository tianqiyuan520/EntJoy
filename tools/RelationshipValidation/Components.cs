using EntJoy.ECS;

namespace RelationshipValidation;

public struct State : IComponentData { public int Seen; }
[MultiRelation] public struct MultiLinks : IRelationComponent { public RelationSlot Target; }
[MultiRelation(MaxSlots = 8)] public partial struct FixedLinks : IRelationComponent { public RelationSlot Target; }
[MultiRelation, OnTargetDeleted(Cascade = true)] public struct CascadeLinks : IRelationComponent { public RelationSlot Target; }
public struct StressActive : IComponentData, IEnableableComponent { }
public struct MultiReadJob : IJobEntity
{
    public void Execute(ref State state) { state.Seen++; }
}
public struct Link : IComponentData, IRelationComponent { public RelationSlot Target; public int Data; }
[ExclusiveRelation]
public struct Unique : IComponentData, IRelationComponent { public RelationSlot Target; public int Data; }
[AcyclicRelation]
public struct Parent : IComponentData, IRelationComponent { public RelationSlot Target; public int Data; }
[OnTargetDeleted(Cascade = true)]
public struct Owns : IComponentData, IRelationComponent { public RelationSlot Target; public int Data; }
public struct ReadLinkJob : IJobEntity
{
    public void Execute(ref State state, in Link link) { state.Seen += 1; }
}
