using Godot;
using Godot.NativeInterop;

partial class DebuggerTrigger
{
#pragma warning disable CS0109 // Disable warning about redundant 'new' keyword
    /// <summary>
    /// Cached StringNames for the properties and fields contained in this class, for fast lookup.
    /// </summary>
    public new class PropertyName : global::Godot.Button.PropertyName {
        /// <summary>
        /// Cached name for the 'debuggerPanelPackedScene' field.
        /// </summary>
        public new static readonly global::Godot.StringName @debuggerPanelPackedScene = "debuggerPanelPackedScene";
        /// <summary>
        /// Cached name for the 'Pos' field.
        /// </summary>
        public new static readonly global::Godot.StringName @Pos = "Pos";
        /// <summary>
        /// Cached name for the 'debuggerPanel' field.
        /// </summary>
        public new static readonly global::Godot.StringName @debuggerPanel = "debuggerPanel";
        /// <summary>
        /// Cached name for the 'IsOpen' field.
        /// </summary>
        public new static readonly global::Godot.StringName @IsOpen = "IsOpen";
    }
    /// <inheritdoc/>
    [global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]
    protected override bool SetGodotClassPropertyValue(in godot_string_name name, in godot_variant value)
    {
        if (name == PropertyName.@debuggerPanelPackedScene) {
            this.@debuggerPanelPackedScene = global::Godot.NativeInterop.VariantUtils.ConvertTo<global::Godot.PackedScene>(value);
            return true;
        }
        if (name == PropertyName.@Pos) {
            this.@Pos = global::Godot.NativeInterop.VariantUtils.ConvertTo<global::Godot.Node>(value);
            return true;
        }
        if (name == PropertyName.@debuggerPanel) {
            this.@debuggerPanel = global::Godot.NativeInterop.VariantUtils.ConvertTo<global::DebuggerPanel>(value);
            return true;
        }
        if (name == PropertyName.@IsOpen) {
            this.@IsOpen = global::Godot.NativeInterop.VariantUtils.ConvertTo<bool>(value);
            return true;
        }
        return base.SetGodotClassPropertyValue(name, value);
    }
    /// <inheritdoc/>
    [global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]
    protected override bool GetGodotClassPropertyValue(in godot_string_name name, out godot_variant value)
    {
        if (name == PropertyName.@debuggerPanelPackedScene) {
            value = global::Godot.NativeInterop.VariantUtils.CreateFrom<global::Godot.PackedScene>(this.@debuggerPanelPackedScene);
            return true;
        }
        if (name == PropertyName.@Pos) {
            value = global::Godot.NativeInterop.VariantUtils.CreateFrom<global::Godot.Node>(this.@Pos);
            return true;
        }
        if (name == PropertyName.@debuggerPanel) {
            value = global::Godot.NativeInterop.VariantUtils.CreateFrom<global::DebuggerPanel>(this.@debuggerPanel);
            return true;
        }
        if (name == PropertyName.@IsOpen) {
            value = global::Godot.NativeInterop.VariantUtils.CreateFrom<bool>(this.@IsOpen);
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
        properties.Add(new(type: (global::Godot.Variant.Type)24, name: PropertyName.@debuggerPanelPackedScene, hint: (global::Godot.PropertyHint)17, hintString: "PackedScene", usage: (global::Godot.PropertyUsageFlags)4102, exported: true));
        properties.Add(new(type: (global::Godot.Variant.Type)24, name: PropertyName.@Pos, hint: (global::Godot.PropertyHint)34, hintString: "Node", usage: (global::Godot.PropertyUsageFlags)4102, exported: true));
        properties.Add(new(type: (global::Godot.Variant.Type)24, name: PropertyName.@debuggerPanel, hint: (global::Godot.PropertyHint)0, hintString: "", usage: (global::Godot.PropertyUsageFlags)4096, exported: false));
        properties.Add(new(type: (global::Godot.Variant.Type)1, name: PropertyName.@IsOpen, hint: (global::Godot.PropertyHint)0, hintString: "", usage: (global::Godot.PropertyUsageFlags)4096, exported: false));
        return properties;
    }
#pragma warning restore CS0109
}
