using Godot;
using Godot.NativeInterop;

partial class JobAdditionTest
{
    /// <inheritdoc/>
    [global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]
    protected override void SaveGodotObjectData(global::Godot.Bridge.GodotSerializationInfo info)
    {
        base.SaveGodotObjectData(info);
        info.AddProperty(PropertyName.@_a, global::Godot.Variant.From<float[]>(this.@_a));
        info.AddProperty(PropertyName.@_b, global::Godot.Variant.From<float[]>(this.@_b));
        info.AddProperty(PropertyName.@_result, global::Godot.Variant.From<float[]>(this.@_result));
        info.AddProperty(PropertyName.@_dataCount, global::Godot.Variant.From<int>(this.@_dataCount));
        info.AddProperty(PropertyName.@_time, global::Godot.Variant.From<int>(this.@_time));
    }
    /// <inheritdoc/>
    [global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]
    protected override void RestoreGodotObjectData(global::Godot.Bridge.GodotSerializationInfo info)
    {
        base.RestoreGodotObjectData(info);
        if (info.TryGetProperty(PropertyName.@_a, out var _value__a))
            this.@_a = _value__a.As<float[]>();
        if (info.TryGetProperty(PropertyName.@_b, out var _value__b))
            this.@_b = _value__b.As<float[]>();
        if (info.TryGetProperty(PropertyName.@_result, out var _value__result))
            this.@_result = _value__result.As<float[]>();
        if (info.TryGetProperty(PropertyName.@_dataCount, out var _value__dataCount))
            this.@_dataCount = _value__dataCount.As<int>();
        if (info.TryGetProperty(PropertyName.@_time, out var _value__time))
            this.@_time = _value__time.As<int>();
    }
}
