using Godot;
using Godot.NativeInterop;

partial class SpritesRandomMove
{
    /// <inheritdoc/>
    [global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]
    protected override void SaveGodotObjectData(global::Godot.Bridge.GodotSerializationInfo info)
    {
        base.SaveGodotObjectData(info);
        info.AddProperty(PropertyName.@MultiMeshgroup, global::Godot.Variant.From<global::Godot.Node>(this.@MultiMeshgroup));
        info.AddProperty(PropertyName.@packedScene, global::Godot.Variant.From<global::Godot.PackedScene>(this.@packedScene));
        info.AddProperty(PropertyName.@SpawnMultiMeshCount, global::Godot.Variant.From<int>(this.@SpawnMultiMeshCount));
        info.AddProperty(PropertyName.@multiMeshInstances, global::Godot.Variant.CreateFrom(this.@multiMeshInstances));
        info.AddProperty(PropertyName.@viewportRect, global::Godot.Variant.From<global::Godot.Rect2>(this.@viewportRect));
        info.AddProperty(PropertyName.@ENTITIES_PER_MESH, global::Godot.Variant.From<int>(this.@ENTITIES_PER_MESH));
        info.AddProperty(PropertyName.@EntityCount, global::Godot.Variant.From<int>(this.@EntityCount));
        info.AddProperty(PropertyName.@isPaused, global::Godot.Variant.From<bool>(this.@isPaused));
        info.AddProperty(PropertyName.@time, global::Godot.Variant.From<double>(this.@time));
        info.AddProperty(PropertyName.@time2, global::Godot.Variant.From<double>(this.@time2));
        info.AddProperty(PropertyName.@count, global::Godot.Variant.From<int>(this.@count));
    }
    /// <inheritdoc/>
    [global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]
    protected override void RestoreGodotObjectData(global::Godot.Bridge.GodotSerializationInfo info)
    {
        base.RestoreGodotObjectData(info);
        if (info.TryGetProperty(PropertyName.@MultiMeshgroup, out var _value_MultiMeshgroup))
            this.@MultiMeshgroup = _value_MultiMeshgroup.As<global::Godot.Node>();
        if (info.TryGetProperty(PropertyName.@packedScene, out var _value_packedScene))
            this.@packedScene = _value_packedScene.As<global::Godot.PackedScene>();
        if (info.TryGetProperty(PropertyName.@SpawnMultiMeshCount, out var _value_SpawnMultiMeshCount))
            this.@SpawnMultiMeshCount = _value_SpawnMultiMeshCount.As<int>();
        if (info.TryGetProperty(PropertyName.@multiMeshInstances, out var _value_multiMeshInstances))
            this.@multiMeshInstances = _value_multiMeshInstances.AsGodotObjectArray<global::Godot.MultiMeshInstance2D>();
        if (info.TryGetProperty(PropertyName.@viewportRect, out var _value_viewportRect))
            this.@viewportRect = _value_viewportRect.As<global::Godot.Rect2>();
        if (info.TryGetProperty(PropertyName.@ENTITIES_PER_MESH, out var _value_ENTITIES_PER_MESH))
            this.@ENTITIES_PER_MESH = _value_ENTITIES_PER_MESH.As<int>();
        if (info.TryGetProperty(PropertyName.@EntityCount, out var _value_EntityCount))
            this.@EntityCount = _value_EntityCount.As<int>();
        if (info.TryGetProperty(PropertyName.@isPaused, out var _value_isPaused))
            this.@isPaused = _value_isPaused.As<bool>();
        if (info.TryGetProperty(PropertyName.@time, out var _value_time))
            this.@time = _value_time.As<double>();
        if (info.TryGetProperty(PropertyName.@time2, out var _value_time2))
            this.@time2 = _value_time2.As<double>();
        if (info.TryGetProperty(PropertyName.@count, out var _value_count))
            this.@count = _value_count.As<int>();
    }
}
