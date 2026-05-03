using Godot;
using Godot.NativeInterop;

partial class AutoSpawnMultiMesh
{
#pragma warning disable CS0109 // Disable warning about redundant 'new' keyword
    /// <summary>
    /// Cached StringNames for the properties and fields contained in this class, for fast lookup.
    /// </summary>
    public new class PropertyName : global::Godot.Node.PropertyName {
        /// <summary>
        /// Cached name for the 'refresh' property.
        /// </summary>
        public new static readonly global::Godot.StringName @refresh = "refresh";
        /// <summary>
        /// Cached name for the 'packedScene' field.
        /// </summary>
        public new static readonly global::Godot.StringName @packedScene = "packedScene";
        /// <summary>
        /// Cached name for the 'MultiMeshgroup' field.
        /// </summary>
        public new static readonly global::Godot.StringName @MultiMeshgroup = "MultiMeshgroup";
    }
    /// <inheritdoc/>
    [global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]
    protected override bool SetGodotClassPropertyValue(in godot_string_name name, in godot_variant value)
    {
        if (name == PropertyName.@refresh) {
            this.@refresh = global::Godot.NativeInterop.VariantUtils.ConvertTo<bool>(value);
            return true;
        }
        if (name == PropertyName.@packedScene) {
            this.@packedScene = global::Godot.NativeInterop.VariantUtils.ConvertTo<global::Godot.PackedScene>(value);
            return true;
        }
        if (name == PropertyName.@MultiMeshgroup) {
            this.@MultiMeshgroup = global::Godot.NativeInterop.VariantUtils.ConvertTo<global::Godot.Node>(value);
            return true;
        }
        return base.SetGodotClassPropertyValue(name, value);
    }
    /// <inheritdoc/>
    [global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]
    protected override bool GetGodotClassPropertyValue(in godot_string_name name, out godot_variant value)
    {
        if (name == PropertyName.@refresh) {
            value = global::Godot.NativeInterop.VariantUtils.CreateFrom<bool>(this.@refresh);
            return true;
        }
        if (name == PropertyName.@packedScene) {
            value = global::Godot.NativeInterop.VariantUtils.CreateFrom<global::Godot.PackedScene>(this.@packedScene);
            return true;
        }
        if (name == PropertyName.@MultiMeshgroup) {
            value = global::Godot.NativeInterop.VariantUtils.CreateFrom<global::Godot.Node>(this.@MultiMeshgroup);
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
        properties.Add(new(type: (global::Godot.Variant.Type)24, name: PropertyName.@packedScene, hint: (global::Godot.PropertyHint)17, hintString: "PackedScene", usage: (global::Godot.PropertyUsageFlags)4102, exported: true));
        properties.Add(new(type: (global::Godot.Variant.Type)24, name: PropertyName.@MultiMeshgroup, hint: (global::Godot.PropertyHint)34, hintString: "Node", usage: (global::Godot.PropertyUsageFlags)4102, exported: true));
        properties.Add(new(type: (global::Godot.Variant.Type)1, name: PropertyName.@refresh, hint: (global::Godot.PropertyHint)0, hintString: "", usage: (global::Godot.PropertyUsageFlags)4102, exported: true));
        return properties;
    }
#pragma warning restore CS0109
}
