using Godot;
using Godot.NativeInterop;

partial class JobDependencyTest
{
    /// <inheritdoc/>
    [global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]
    protected override void SaveGodotObjectData(global::Godot.Bridge.GodotSerializationInfo info)
    {
        base.SaveGodotObjectData(info);
        info.AddProperty(PropertyName.@_stage, global::Godot.Variant.From<int>(this.@_stage));
        info.AddProperty(PropertyName.@_errorCount, global::Godot.Variant.From<int>(this.@_errorCount));
    }
    /// <inheritdoc/>
    [global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]
    protected override void RestoreGodotObjectData(global::Godot.Bridge.GodotSerializationInfo info)
    {
        base.RestoreGodotObjectData(info);
        if (info.TryGetProperty(PropertyName.@_stage, out var _value__stage))
            this.@_stage = _value__stage.As<int>();
        if (info.TryGetProperty(PropertyName.@_errorCount, out var _value__errorCount))
            this.@_errorCount = _value__errorCount.As<int>();
    }
}
