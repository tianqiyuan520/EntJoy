using Godot;
using Godot.NativeInterop;

partial class DebuggerPanel
{
#pragma warning disable CS0109 // Disable warning about redundant 'new' keyword
    /// <summary>
    /// Cached StringNames for the properties and fields contained in this class, for fast lookup.
    /// </summary>
    public new class PropertyName : global::Godot.Panel.PropertyName {
        /// <summary>
        /// Cached name for the 'TreeControl' field.
        /// </summary>
        public new static readonly global::Godot.StringName @TreeControl = "TreeControl";
        /// <summary>
        /// Cached name for the 'RefreshButton' field.
        /// </summary>
        public new static readonly global::Godot.StringName @RefreshButton = "RefreshButton";
        /// <summary>
        /// Cached name for the 'memoryGraphicsPackedScene' field.
        /// </summary>
        public new static readonly global::Godot.StringName @memoryGraphicsPackedScene = "memoryGraphicsPackedScene";
        /// <summary>
        /// Cached name for the 'memoryGraphics' field.
        /// </summary>
        public new static readonly global::Godot.StringName @memoryGraphics = "memoryGraphics";
        /// <summary>
        /// Cached name for the '_rootItem' field.
        /// </summary>
        public new static readonly global::Godot.StringName @_rootItem = "_rootItem";
    }
    /// <inheritdoc/>
    [global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]
    protected override bool SetGodotClassPropertyValue(in godot_string_name name, in godot_variant value)
    {
        if (name == PropertyName.@TreeControl) {
            this.@TreeControl = global::Godot.NativeInterop.VariantUtils.ConvertTo<global::Godot.Tree>(value);
            return true;
        }
        if (name == PropertyName.@RefreshButton) {
            this.@RefreshButton = global::Godot.NativeInterop.VariantUtils.ConvertTo<global::Godot.Button>(value);
            return true;
        }
        if (name == PropertyName.@memoryGraphicsPackedScene) {
            this.@memoryGraphicsPackedScene = global::Godot.NativeInterop.VariantUtils.ConvertTo<global::Godot.PackedScene>(value);
            return true;
        }
        if (name == PropertyName.@memoryGraphics) {
            this.@memoryGraphics = global::Godot.NativeInterop.VariantUtils.ConvertTo<global::MemoryGraphics>(value);
            return true;
        }
        if (name == PropertyName.@_rootItem) {
            this.@_rootItem = global::Godot.NativeInterop.VariantUtils.ConvertTo<global::Godot.TreeItem>(value);
            return true;
        }
        return base.SetGodotClassPropertyValue(name, value);
    }
    /// <inheritdoc/>
    [global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]
    protected override bool GetGodotClassPropertyValue(in godot_string_name name, out godot_variant value)
    {
        if (name == PropertyName.@TreeControl) {
            value = global::Godot.NativeInterop.VariantUtils.CreateFrom<global::Godot.Tree>(this.@TreeControl);
            return true;
        }
        if (name == PropertyName.@RefreshButton) {
            value = global::Godot.NativeInterop.VariantUtils.CreateFrom<global::Godot.Button>(this.@RefreshButton);
            return true;
        }
        if (name == PropertyName.@memoryGraphicsPackedScene) {
            value = global::Godot.NativeInterop.VariantUtils.CreateFrom<global::Godot.PackedScene>(this.@memoryGraphicsPackedScene);
            return true;
        }
        if (name == PropertyName.@memoryGraphics) {
            value = global::Godot.NativeInterop.VariantUtils.CreateFrom<global::MemoryGraphics>(this.@memoryGraphics);
            return true;
        }
        if (name == PropertyName.@_rootItem) {
            value = global::Godot.NativeInterop.VariantUtils.CreateFrom<global::Godot.TreeItem>(this.@_rootItem);
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
        properties.Add(new(type: (global::Godot.Variant.Type)24, name: PropertyName.@TreeControl, hint: (global::Godot.PropertyHint)34, hintString: "Tree", usage: (global::Godot.PropertyUsageFlags)4102, exported: true));
        properties.Add(new(type: (global::Godot.Variant.Type)24, name: PropertyName.@RefreshButton, hint: (global::Godot.PropertyHint)34, hintString: "Button", usage: (global::Godot.PropertyUsageFlags)4102, exported: true));
        properties.Add(new(type: (global::Godot.Variant.Type)24, name: PropertyName.@memoryGraphicsPackedScene, hint: (global::Godot.PropertyHint)17, hintString: "PackedScene", usage: (global::Godot.PropertyUsageFlags)4102, exported: true));
        properties.Add(new(type: (global::Godot.Variant.Type)24, name: PropertyName.@memoryGraphics, hint: (global::Godot.PropertyHint)0, hintString: "", usage: (global::Godot.PropertyUsageFlags)4096, exported: false));
        properties.Add(new(type: (global::Godot.Variant.Type)24, name: PropertyName.@_rootItem, hint: (global::Godot.PropertyHint)0, hintString: "", usage: (global::Godot.PropertyUsageFlags)4096, exported: false));
        return properties;
    }
#pragma warning restore CS0109
}
