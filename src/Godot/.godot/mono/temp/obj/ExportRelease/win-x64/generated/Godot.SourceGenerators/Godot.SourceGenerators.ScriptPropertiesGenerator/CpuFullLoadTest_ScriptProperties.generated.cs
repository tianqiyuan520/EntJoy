using Godot;
using Godot.NativeInterop;

partial class CpuFullLoadTest
{
#pragma warning disable CS0109 // Disable warning about redundant 'new' keyword
    /// <summary>
    /// Cached StringNames for the properties and fields contained in this class, for fast lookup.
    /// </summary>
    public new class PropertyName : global::Godot.Node.PropertyName {
        /// <summary>
        /// Cached name for the '_startButton' field.
        /// </summary>
        public new static readonly global::Godot.StringName @_startButton = "_startButton";
        /// <summary>
        /// Cached name for the '_statusLabel' field.
        /// </summary>
        public new static readonly global::Godot.StringName @_statusLabel = "_statusLabel";
    }
    /// <inheritdoc/>
    [global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]
    protected override bool SetGodotClassPropertyValue(in godot_string_name name, in godot_variant value)
    {
        if (name == PropertyName.@_startButton) {
            this.@_startButton = global::Godot.NativeInterop.VariantUtils.ConvertTo<global::Godot.Button>(value);
            return true;
        }
        if (name == PropertyName.@_statusLabel) {
            this.@_statusLabel = global::Godot.NativeInterop.VariantUtils.ConvertTo<global::Godot.Label>(value);
            return true;
        }
        return base.SetGodotClassPropertyValue(name, value);
    }
    /// <inheritdoc/>
    [global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]
    protected override bool GetGodotClassPropertyValue(in godot_string_name name, out godot_variant value)
    {
        if (name == PropertyName.@_startButton) {
            value = global::Godot.NativeInterop.VariantUtils.CreateFrom<global::Godot.Button>(this.@_startButton);
            return true;
        }
        if (name == PropertyName.@_statusLabel) {
            value = global::Godot.NativeInterop.VariantUtils.CreateFrom<global::Godot.Label>(this.@_statusLabel);
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
        properties.Add(new(type: (global::Godot.Variant.Type)24, name: PropertyName.@_startButton, hint: (global::Godot.PropertyHint)0, hintString: "", usage: (global::Godot.PropertyUsageFlags)4096, exported: false));
        properties.Add(new(type: (global::Godot.Variant.Type)24, name: PropertyName.@_statusLabel, hint: (global::Godot.PropertyHint)0, hintString: "", usage: (global::Godot.PropertyUsageFlags)4096, exported: false));
        return properties;
    }
#pragma warning restore CS0109
}
