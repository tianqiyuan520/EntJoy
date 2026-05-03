using Godot;
using Godot.NativeInterop;

partial class AutoSpawnMultiMesh
{
    /// <inheritdoc/>
    [global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]
    protected override void SaveGodotObjectData(global::Godot.Bridge.GodotSerializationInfo info)
    {
        base.SaveGodotObjectData(info);
        info.AddProperty(PropertyName.@refresh, global::Godot.Variant.From<bool>(this.@refresh));
        info.AddProperty(PropertyName.@packedScene, global::Godot.Variant.From<global::Godot.PackedScene>(this.@packedScene));
        info.AddProperty(PropertyName.@MultiMeshgroup, global::Godot.Variant.From<global::Godot.Node>(this.@MultiMeshgroup));
    }
    /// <inheritdoc/>
    [global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]
    protected override void RestoreGodotObjectData(global::Godot.Bridge.GodotSerializationInfo info)
    {
        base.RestoreGodotObjectData(info);
        if (info.TryGetProperty(PropertyName.@refresh, out var _value_refresh))
            this.@refresh = _value_refresh.As<bool>();
        if (info.TryGetProperty(PropertyName.@packedScene, out var _value_packedScene))
            this.@packedScene = _value_packedScene.As<global::Godot.PackedScene>();
        if (info.TryGetProperty(PropertyName.@MultiMeshgroup, out var _value_MultiMeshgroup))
            this.@MultiMeshgroup = _value_MultiMeshgroup.As<global::Godot.Node>();
    }
}
