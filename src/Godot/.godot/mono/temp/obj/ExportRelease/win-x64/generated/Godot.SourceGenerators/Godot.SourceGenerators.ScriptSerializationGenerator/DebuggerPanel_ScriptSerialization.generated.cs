using Godot;
using Godot.NativeInterop;

partial class DebuggerPanel
{
    /// <inheritdoc/>
    [global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]
    protected override void SaveGodotObjectData(global::Godot.Bridge.GodotSerializationInfo info)
    {
        base.SaveGodotObjectData(info);
        info.AddProperty(PropertyName.@TreeControl, global::Godot.Variant.From<global::Godot.Tree>(this.@TreeControl));
        info.AddProperty(PropertyName.@RefreshButton, global::Godot.Variant.From<global::Godot.Button>(this.@RefreshButton));
        info.AddProperty(PropertyName.@memoryGraphicsPackedScene, global::Godot.Variant.From<global::Godot.PackedScene>(this.@memoryGraphicsPackedScene));
        info.AddProperty(PropertyName.@memoryGraphics, global::Godot.Variant.From<global::MemoryGraphics>(this.@memoryGraphics));
        info.AddProperty(PropertyName.@_rootItem, global::Godot.Variant.From<global::Godot.TreeItem>(this.@_rootItem));
    }
    /// <inheritdoc/>
    [global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]
    protected override void RestoreGodotObjectData(global::Godot.Bridge.GodotSerializationInfo info)
    {
        base.RestoreGodotObjectData(info);
        if (info.TryGetProperty(PropertyName.@TreeControl, out var _value_TreeControl))
            this.@TreeControl = _value_TreeControl.As<global::Godot.Tree>();
        if (info.TryGetProperty(PropertyName.@RefreshButton, out var _value_RefreshButton))
            this.@RefreshButton = _value_RefreshButton.As<global::Godot.Button>();
        if (info.TryGetProperty(PropertyName.@memoryGraphicsPackedScene, out var _value_memoryGraphicsPackedScene))
            this.@memoryGraphicsPackedScene = _value_memoryGraphicsPackedScene.As<global::Godot.PackedScene>();
        if (info.TryGetProperty(PropertyName.@memoryGraphics, out var _value_memoryGraphics))
            this.@memoryGraphics = _value_memoryGraphics.As<global::MemoryGraphics>();
        if (info.TryGetProperty(PropertyName.@_rootItem, out var _value__rootItem))
            this.@_rootItem = _value__rootItem.As<global::Godot.TreeItem>();
    }
}
