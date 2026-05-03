using Godot;
using Godot.NativeInterop;

partial class SpritesRandomMove
{
#pragma warning disable CS0109 // Disable warning about redundant 'new' keyword
    /// <summary>
    /// Cached StringNames for the properties and fields contained in this class, for fast lookup.
    /// </summary>
    public new class PropertyName : global::Godot.Node2D.PropertyName {
        /// <summary>
        /// Cached name for the 'MultiMeshgroup' field.
        /// </summary>
        public new static readonly global::Godot.StringName @MultiMeshgroup = "MultiMeshgroup";
        /// <summary>
        /// Cached name for the 'packedScene' field.
        /// </summary>
        public new static readonly global::Godot.StringName @packedScene = "packedScene";
        /// <summary>
        /// Cached name for the 'SpawnMultiMeshCount' field.
        /// </summary>
        public new static readonly global::Godot.StringName @SpawnMultiMeshCount = "SpawnMultiMeshCount";
        /// <summary>
        /// Cached name for the 'multiMeshInstances' field.
        /// </summary>
        public new static readonly global::Godot.StringName @multiMeshInstances = "multiMeshInstances";
        /// <summary>
        /// Cached name for the 'viewportRect' field.
        /// </summary>
        public new static readonly global::Godot.StringName @viewportRect = "viewportRect";
        /// <summary>
        /// Cached name for the 'ENTITIES_PER_MESH' field.
        /// </summary>
        public new static readonly global::Godot.StringName @ENTITIES_PER_MESH = "ENTITIES_PER_MESH";
        /// <summary>
        /// Cached name for the 'EntityCount' field.
        /// </summary>
        public new static readonly global::Godot.StringName @EntityCount = "EntityCount";
        /// <summary>
        /// Cached name for the 'isPaused' field.
        /// </summary>
        public new static readonly global::Godot.StringName @isPaused = "isPaused";
        /// <summary>
        /// Cached name for the 'time' field.
        /// </summary>
        public new static readonly global::Godot.StringName @time = "time";
        /// <summary>
        /// Cached name for the 'time2' field.
        /// </summary>
        public new static readonly global::Godot.StringName @time2 = "time2";
        /// <summary>
        /// Cached name for the 'count' field.
        /// </summary>
        public new static readonly global::Godot.StringName @count = "count";
    }
    /// <inheritdoc/>
    [global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]
    protected override bool SetGodotClassPropertyValue(in godot_string_name name, in godot_variant value)
    {
        if (name == PropertyName.@MultiMeshgroup) {
            this.@MultiMeshgroup = global::Godot.NativeInterop.VariantUtils.ConvertTo<global::Godot.Node>(value);
            return true;
        }
        if (name == PropertyName.@packedScene) {
            this.@packedScene = global::Godot.NativeInterop.VariantUtils.ConvertTo<global::Godot.PackedScene>(value);
            return true;
        }
        if (name == PropertyName.@SpawnMultiMeshCount) {
            this.@SpawnMultiMeshCount = global::Godot.NativeInterop.VariantUtils.ConvertTo<int>(value);
            return true;
        }
        if (name == PropertyName.@multiMeshInstances) {
            this.@multiMeshInstances = global::Godot.NativeInterop.VariantUtils.ConvertToSystemArrayOfGodotObject<global::Godot.MultiMeshInstance2D>(value);
            return true;
        }
        if (name == PropertyName.@viewportRect) {
            this.@viewportRect = global::Godot.NativeInterop.VariantUtils.ConvertTo<global::Godot.Rect2>(value);
            return true;
        }
        if (name == PropertyName.@ENTITIES_PER_MESH) {
            this.@ENTITIES_PER_MESH = global::Godot.NativeInterop.VariantUtils.ConvertTo<int>(value);
            return true;
        }
        if (name == PropertyName.@EntityCount) {
            this.@EntityCount = global::Godot.NativeInterop.VariantUtils.ConvertTo<int>(value);
            return true;
        }
        if (name == PropertyName.@isPaused) {
            this.@isPaused = global::Godot.NativeInterop.VariantUtils.ConvertTo<bool>(value);
            return true;
        }
        if (name == PropertyName.@time) {
            this.@time = global::Godot.NativeInterop.VariantUtils.ConvertTo<double>(value);
            return true;
        }
        if (name == PropertyName.@time2) {
            this.@time2 = global::Godot.NativeInterop.VariantUtils.ConvertTo<double>(value);
            return true;
        }
        if (name == PropertyName.@count) {
            this.@count = global::Godot.NativeInterop.VariantUtils.ConvertTo<int>(value);
            return true;
        }
        return base.SetGodotClassPropertyValue(name, value);
    }
    /// <inheritdoc/>
    [global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]
    protected override bool GetGodotClassPropertyValue(in godot_string_name name, out godot_variant value)
    {
        if (name == PropertyName.@MultiMeshgroup) {
            value = global::Godot.NativeInterop.VariantUtils.CreateFrom<global::Godot.Node>(this.@MultiMeshgroup);
            return true;
        }
        if (name == PropertyName.@packedScene) {
            value = global::Godot.NativeInterop.VariantUtils.CreateFrom<global::Godot.PackedScene>(this.@packedScene);
            return true;
        }
        if (name == PropertyName.@SpawnMultiMeshCount) {
            value = global::Godot.NativeInterop.VariantUtils.CreateFrom<int>(this.@SpawnMultiMeshCount);
            return true;
        }
        if (name == PropertyName.@multiMeshInstances) {
            value = global::Godot.NativeInterop.VariantUtils.CreateFromSystemArrayOfGodotObject(this.@multiMeshInstances);
            return true;
        }
        if (name == PropertyName.@viewportRect) {
            value = global::Godot.NativeInterop.VariantUtils.CreateFrom<global::Godot.Rect2>(this.@viewportRect);
            return true;
        }
        if (name == PropertyName.@ENTITIES_PER_MESH) {
            value = global::Godot.NativeInterop.VariantUtils.CreateFrom<int>(this.@ENTITIES_PER_MESH);
            return true;
        }
        if (name == PropertyName.@EntityCount) {
            value = global::Godot.NativeInterop.VariantUtils.CreateFrom<int>(this.@EntityCount);
            return true;
        }
        if (name == PropertyName.@isPaused) {
            value = global::Godot.NativeInterop.VariantUtils.CreateFrom<bool>(this.@isPaused);
            return true;
        }
        if (name == PropertyName.@time) {
            value = global::Godot.NativeInterop.VariantUtils.CreateFrom<double>(this.@time);
            return true;
        }
        if (name == PropertyName.@time2) {
            value = global::Godot.NativeInterop.VariantUtils.CreateFrom<double>(this.@time2);
            return true;
        }
        if (name == PropertyName.@count) {
            value = global::Godot.NativeInterop.VariantUtils.CreateFrom<int>(this.@count);
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
        properties.Add(new(type: (global::Godot.Variant.Type)24, name: PropertyName.@MultiMeshgroup, hint: (global::Godot.PropertyHint)34, hintString: "Node", usage: (global::Godot.PropertyUsageFlags)4102, exported: true));
        properties.Add(new(type: (global::Godot.Variant.Type)24, name: PropertyName.@packedScene, hint: (global::Godot.PropertyHint)17, hintString: "PackedScene", usage: (global::Godot.PropertyUsageFlags)4102, exported: true));
        properties.Add(new(type: (global::Godot.Variant.Type)2, name: PropertyName.@SpawnMultiMeshCount, hint: (global::Godot.PropertyHint)0, hintString: "", usage: (global::Godot.PropertyUsageFlags)4096, exported: false));
        properties.Add(new(type: (global::Godot.Variant.Type)28, name: PropertyName.@multiMeshInstances, hint: (global::Godot.PropertyHint)0, hintString: "", usage: (global::Godot.PropertyUsageFlags)4096, exported: false));
        properties.Add(new(type: (global::Godot.Variant.Type)7, name: PropertyName.@viewportRect, hint: (global::Godot.PropertyHint)0, hintString: "", usage: (global::Godot.PropertyUsageFlags)4096, exported: false));
        properties.Add(new(type: (global::Godot.Variant.Type)2, name: PropertyName.@ENTITIES_PER_MESH, hint: (global::Godot.PropertyHint)0, hintString: "", usage: (global::Godot.PropertyUsageFlags)4096, exported: false));
        properties.Add(new(type: (global::Godot.Variant.Type)2, name: PropertyName.@EntityCount, hint: (global::Godot.PropertyHint)0, hintString: "", usage: (global::Godot.PropertyUsageFlags)4096, exported: false));
        properties.Add(new(type: (global::Godot.Variant.Type)1, name: PropertyName.@isPaused, hint: (global::Godot.PropertyHint)0, hintString: "", usage: (global::Godot.PropertyUsageFlags)4096, exported: false));
        properties.Add(new(type: (global::Godot.Variant.Type)3, name: PropertyName.@time, hint: (global::Godot.PropertyHint)0, hintString: "", usage: (global::Godot.PropertyUsageFlags)4096, exported: false));
        properties.Add(new(type: (global::Godot.Variant.Type)3, name: PropertyName.@time2, hint: (global::Godot.PropertyHint)0, hintString: "", usage: (global::Godot.PropertyUsageFlags)4096, exported: false));
        properties.Add(new(type: (global::Godot.Variant.Type)2, name: PropertyName.@count, hint: (global::Godot.PropertyHint)0, hintString: "", usage: (global::Godot.PropertyUsageFlags)4096, exported: false));
        return properties;
    }
#pragma warning restore CS0109
}
