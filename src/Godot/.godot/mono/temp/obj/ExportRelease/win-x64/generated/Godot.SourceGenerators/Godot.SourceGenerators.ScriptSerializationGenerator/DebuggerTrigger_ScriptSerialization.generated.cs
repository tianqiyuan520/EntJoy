using Godot;
using Godot.NativeInterop;

partial class DebuggerTrigger
{
    /// <inheritdoc/>
    [global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]
    protected override void SaveGodotObjectData(global::Godot.Bridge.GodotSerializationInfo info)
    {
        base.SaveGodotObjectData(info);
        info.AddProperty(PropertyName.@debuggerPanelPackedScene, global::Godot.Variant.From<global::Godot.PackedScene>(this.@debuggerPanelPackedScene));
        info.AddProperty(PropertyName.@Pos, global::Godot.Variant.From<global::Godot.Node>(this.@Pos));
        info.AddProperty(PropertyName.@debuggerPanel, global::Godot.Variant.From<global::DebuggerPanel>(this.@debuggerPanel));
        info.AddProperty(PropertyName.@IsOpen, global::Godot.Variant.From<bool>(this.@IsOpen));
    }
    /// <inheritdoc/>
    [global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]
    protected override void RestoreGodotObjectData(global::Godot.Bridge.GodotSerializationInfo info)
    {
        base.RestoreGodotObjectData(info);
        if (info.TryGetProperty(PropertyName.@debuggerPanelPackedScene, out var _value_debuggerPanelPackedScene))
            this.@debuggerPanelPackedScene = _value_debuggerPanelPackedScene.As<global::Godot.PackedScene>();
        if (info.TryGetProperty(PropertyName.@Pos, out var _value_Pos))
            this.@Pos = _value_Pos.As<global::Godot.Node>();
        if (info.TryGetProperty(PropertyName.@debuggerPanel, out var _value_debuggerPanel))
            this.@debuggerPanel = _value_debuggerPanel.As<global::DebuggerPanel>();
        if (info.TryGetProperty(PropertyName.@IsOpen, out var _value_IsOpen))
            this.@IsOpen = _value_IsOpen.As<bool>();
    }
}
