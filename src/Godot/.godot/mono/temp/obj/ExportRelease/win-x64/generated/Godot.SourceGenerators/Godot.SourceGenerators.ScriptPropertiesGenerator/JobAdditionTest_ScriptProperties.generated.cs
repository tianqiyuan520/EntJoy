using Godot;
using Godot.NativeInterop;

partial class JobAdditionTest
{
#pragma warning disable CS0109 // Disable warning about redundant 'new' keyword
    /// <summary>
    /// Cached StringNames for the properties and fields contained in this class, for fast lookup.
    /// </summary>
    public new class PropertyName : global::Godot.Node.PropertyName {
        /// <summary>
        /// Cached name for the '_a' field.
        /// </summary>
        public new static readonly global::Godot.StringName @_a = "_a";
        /// <summary>
        /// Cached name for the '_b' field.
        /// </summary>
        public new static readonly global::Godot.StringName @_b = "_b";
        /// <summary>
        /// Cached name for the '_result' field.
        /// </summary>
        public new static readonly global::Godot.StringName @_result = "_result";
        /// <summary>
        /// Cached name for the '_dataCount' field.
        /// </summary>
        public new static readonly global::Godot.StringName @_dataCount = "_dataCount";
        /// <summary>
        /// Cached name for the '_time' field.
        /// </summary>
        public new static readonly global::Godot.StringName @_time = "_time";
    }
    /// <inheritdoc/>
    [global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]
    protected override bool SetGodotClassPropertyValue(in godot_string_name name, in godot_variant value)
    {
        if (name == PropertyName.@_a) {
            this.@_a = global::Godot.NativeInterop.VariantUtils.ConvertTo<float[]>(value);
            return true;
        }
        if (name == PropertyName.@_b) {
            this.@_b = global::Godot.NativeInterop.VariantUtils.ConvertTo<float[]>(value);
            return true;
        }
        if (name == PropertyName.@_result) {
            this.@_result = global::Godot.NativeInterop.VariantUtils.ConvertTo<float[]>(value);
            return true;
        }
        if (name == PropertyName.@_dataCount) {
            this.@_dataCount = global::Godot.NativeInterop.VariantUtils.ConvertTo<int>(value);
            return true;
        }
        if (name == PropertyName.@_time) {
            this.@_time = global::Godot.NativeInterop.VariantUtils.ConvertTo<int>(value);
            return true;
        }
        return base.SetGodotClassPropertyValue(name, value);
    }
    /// <inheritdoc/>
    [global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]
    protected override bool GetGodotClassPropertyValue(in godot_string_name name, out godot_variant value)
    {
        if (name == PropertyName.@_a) {
            value = global::Godot.NativeInterop.VariantUtils.CreateFrom<float[]>(this.@_a);
            return true;
        }
        if (name == PropertyName.@_b) {
            value = global::Godot.NativeInterop.VariantUtils.CreateFrom<float[]>(this.@_b);
            return true;
        }
        if (name == PropertyName.@_result) {
            value = global::Godot.NativeInterop.VariantUtils.CreateFrom<float[]>(this.@_result);
            return true;
        }
        if (name == PropertyName.@_dataCount) {
            value = global::Godot.NativeInterop.VariantUtils.CreateFrom<int>(this.@_dataCount);
            return true;
        }
        if (name == PropertyName.@_time) {
            value = global::Godot.NativeInterop.VariantUtils.CreateFrom<int>(this.@_time);
            return true;
        }
        return base.GetGodotClassPropertyValue(name, out value);
    }
    /// <summary>
    /// Get the property information for all the properties declared in this class.
    /// This method is used by Godot to register the available properties in the editor.
    /// Do not call this method.
    /// </summary>
    [global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]
    internal new static global::System.Collections.Generic.List<global::Godot.Bridge.PropertyInfo> GetGodotPropertyList()
    {
        var properties = new global::System.Collections.Generic.List<global::Godot.Bridge.PropertyInfo>();
        properties.Add(new(type: (global::Godot.Variant.Type)32, name: PropertyName.@_a, hint: (global::Godot.PropertyHint)0, hintString: "", usage: (global::Godot.PropertyUsageFlags)4096, exported: false));
        properties.Add(new(type: (global::Godot.Variant.Type)32, name: PropertyName.@_b, hint: (global::Godot.PropertyHint)0, hintString: "", usage: (global::Godot.PropertyUsageFlags)4096, exported: false));
        properties.Add(new(type: (global::Godot.Variant.Type)32, name: PropertyName.@_result, hint: (global::Godot.PropertyHint)0, hintString: "", usage: (global::Godot.PropertyUsageFlags)4096, exported: false));
        properties.Add(new(type: (global::Godot.Variant.Type)2, name: PropertyName.@_dataCount, hint: (global::Godot.PropertyHint)0, hintString: "", usage: (global::Godot.PropertyUsageFlags)4096, exported: false));
        properties.Add(new(type: (global::Godot.Variant.Type)2, name: PropertyName.@_time, hint: (global::Godot.PropertyHint)0, hintString: "", usage: (global::Godot.PropertyUsageFlags)4096, exported: false));
        return properties;
    }
#pragma warning restore CS0109
}
