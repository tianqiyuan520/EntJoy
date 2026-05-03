using Godot;
using Godot.NativeInterop;

partial class TestGridSearch
{
#pragma warning disable CS0109 // Disable warning about redundant 'new' keyword
    /// <summary>
    /// Cached StringNames for the properties and fields contained in this class, for fast lookup.
    /// </summary>
    public new class PropertyName : global::Godot.Node.PropertyName {
        /// <summary>
        /// Cached name for the 'mytext' field.
        /// </summary>
        public new static readonly global::Godot.StringName @mytext = "mytext";
        /// <summary>
        /// Cached name for the 'N' field.
        /// </summary>
        public new static readonly global::Godot.StringName @N = "N";
        /// <summary>
        /// Cached name for the 'K' field.
        /// </summary>
        public new static readonly global::Godot.StringName @K = "K";
    }
    /// <inheritdoc/>
    [global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]
    protected override bool SetGodotClassPropertyValue(in godot_string_name name, in godot_variant value)
    {
        if (name == PropertyName.@mytext) {
            this.@mytext = global::Godot.NativeInterop.VariantUtils.ConvertTo<global::Godot.Label>(value);
            return true;
        }
        if (name == PropertyName.@N) {
            this.@N = global::Godot.NativeInterop.VariantUtils.ConvertTo<int>(value);
            return true;
        }
        if (name == PropertyName.@K) {
            this.@K = global::Godot.NativeInterop.VariantUtils.ConvertTo<int>(value);
            return true;
        }
        return base.SetGodotClassPropertyValue(name, value);
    }
    /// <inheritdoc/>
    [global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]
    protected override bool GetGodotClassPropertyValue(in godot_string_name name, out godot_variant value)
    {
        if (name == PropertyName.@mytext) {
            value = global::Godot.NativeInterop.VariantUtils.CreateFrom<global::Godot.Label>(this.@mytext);
            return true;
        }
        if (name == PropertyName.@N) {
            value = global::Godot.NativeInterop.VariantUtils.CreateFrom<int>(this.@N);
            return true;
        }
        if (name == PropertyName.@K) {
            value = global::Godot.NativeInterop.VariantUtils.CreateFrom<int>(this.@K);
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
        properties.Add(new(type: (global::Godot.Variant.Type)24, name: PropertyName.@mytext, hint: (global::Godot.PropertyHint)34, hintString: "Label", usage: (global::Godot.PropertyUsageFlags)4102, exported: true));
        properties.Add(new(type: (global::Godot.Variant.Type)2, name: PropertyName.@N, hint: (global::Godot.PropertyHint)0, hintString: "", usage: (global::Godot.PropertyUsageFlags)4102, exported: true));
        properties.Add(new(type: (global::Godot.Variant.Type)2, name: PropertyName.@K, hint: (global::Godot.PropertyHint)0, hintString: "", usage: (global::Godot.PropertyUsageFlags)4102, exported: true));
        return properties;
    }
#pragma warning restore CS0109
}
