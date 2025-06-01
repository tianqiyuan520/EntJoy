using Godot;
using System;

[Tool]
public partial class AutoSpawnMultiMesh : Node
{
    [Export]
    PackedScene packedScene;
    [Export]
    Node MultiMeshgroup;
    [Export]
    bool refresh
    {
        get => true;
        set
        {
            generates();
        }
    }

    public void generates()
    {
        if (MultiMeshgroup == null || packedScene == null) return;
        GD.Print(MultiMeshgroup.GetChildCount());
        MultiMeshgroup.AddChild(packedScene.Instantiate<MultiMeshInstance2D>());
    }

}
