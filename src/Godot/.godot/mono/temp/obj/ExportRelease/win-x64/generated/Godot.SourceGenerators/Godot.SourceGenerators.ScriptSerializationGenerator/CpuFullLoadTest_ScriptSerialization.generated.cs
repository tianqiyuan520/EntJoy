using Godot;
using Godot.NativeInterop;

partial class CpuFullLoadTest
{
    /// <inheritdoc/>
    [global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]
    protected override void SaveGodotObjectData(global::Godot.Bridge.GodotSerializationInfo info)
    {
        base.SaveGodotObjectData(info);
        info.AddProperty(PropertyName.@_startButton, global::Godot.Variant.From<global::Godot.Button>(this.@_startButton));
        info.AddProperty(PropertyName.@_statusLabel, global::Godot.Variant.From<global::Godot.Label>(this.@_statusLabel));
    }
    /// <inheritdoc/>
    [global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]
    protected override void RestoreGodotObjectData(global::Godot.Bridge.GodotSerializationInfo info)
    {
        base.RestoreGodotObjectData(info);
        if (info.TryGetProperty(PropertyName.@_startButton, out var _value__startButton))
            this.@_startButton = _value__startButton.As<global::Godot.Button>();
        if (info.TryGetProperty(PropertyName.@_statusLabel, out var _value__statusLabel))
            this.@_statusLabel = _value__statusLabel.As<global::Godot.Label>();
    }
}
