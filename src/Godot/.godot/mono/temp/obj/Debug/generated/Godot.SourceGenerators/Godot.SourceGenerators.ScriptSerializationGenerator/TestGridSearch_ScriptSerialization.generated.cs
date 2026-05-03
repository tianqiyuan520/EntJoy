using Godot;
using Godot.NativeInterop;

partial class TestGridSearch
{
    /// <inheritdoc/>
    [global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]
    protected override void SaveGodotObjectData(global::Godot.Bridge.GodotSerializationInfo info)
    {
        base.SaveGodotObjectData(info);
        info.AddProperty(PropertyName.@mytext, global::Godot.Variant.From<global::Godot.Label>(this.@mytext));
        info.AddProperty(PropertyName.@N, global::Godot.Variant.From<int>(this.@N));
        info.AddProperty(PropertyName.@K, global::Godot.Variant.From<int>(this.@K));
    }
    /// <inheritdoc/>
    [global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]
    protected override void RestoreGodotObjectData(global::Godot.Bridge.GodotSerializationInfo info)
    {
        base.RestoreGodotObjectData(info);
        if (info.TryGetProperty(PropertyName.@mytext, out var _value_mytext))
            this.@mytext = _value_mytext.As<global::Godot.Label>();
        if (info.TryGetProperty(PropertyName.@N, out var _value_N))
            this.@N = _value_N.As<int>();
        if (info.TryGetProperty(PropertyName.@K, out var _value_K))
            this.@K = _value_K.As<int>();
    }
}
