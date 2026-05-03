using Godot;
using Godot.NativeInterop;

partial class CameraMove
{
#pragma warning disable CS0109 // Disable warning about redundant 'new' keyword
    /// <summary>
    /// Cached StringNames for the properties and fields contained in this class, for fast lookup.
    /// </summary>
    public new class PropertyName : global::Godot.Camera2D.PropertyName {
        /// <summary>
        /// Cached name for the 'view_zoom' field.
        /// </summary>
        public new static readonly global::Godot.StringName @view_zoom = "view_zoom";
        /// <summary>
        /// Cached name for the 'camera_draging' field.
        /// </summary>
        public new static readonly global::Godot.StringName @camera_draging = "camera_draging";
        /// <summary>
        /// Cached name for the 'drag_start_camera_pos' field.
        /// </summary>
        public new static readonly global::Godot.StringName @drag_start_camera_pos = "drag_start_camera_pos";
        /// <summary>
        /// Cached name for the 'drag_start_mouse_pos' field.
        /// </summary>
        public new static readonly global::Godot.StringName @drag_start_mouse_pos = "drag_start_mouse_pos";
        /// <summary>
        /// Cached name for the 'camera_drag' field.
        /// </summary>
        public new static readonly global::Godot.StringName @camera_drag = "camera_drag";
        /// <summary>
        /// Cached name for the 'camera_old_pos' field.
        /// </summary>
        public new static readonly global::Godot.StringName @camera_old_pos = "camera_old_pos";
        /// <summary>
        /// Cached name for the 'mouse_pos' field.
        /// </summary>
        public new static readonly global::Godot.StringName @mouse_pos = "mouse_pos";
        /// <summary>
        /// Cached name for the 'mouse_screen_pos' field.
        /// </summary>
        public new static readonly global::Godot.StringName @mouse_screen_pos = "mouse_screen_pos";
        /// <summary>
        /// Cached name for the 'mouse_screen_old_pos' field.
        /// </summary>
        public new static readonly global::Godot.StringName @mouse_screen_old_pos = "mouse_screen_old_pos";
    }
    /// <inheritdoc/>
    [global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]
    protected override bool SetGodotClassPropertyValue(in godot_string_name name, in godot_variant value)
    {
        if (name == PropertyName.@view_zoom) {
            this.@view_zoom = global::Godot.NativeInterop.VariantUtils.ConvertTo<float>(value);
            return true;
        }
        if (name == PropertyName.@camera_draging) {
            this.@camera_draging = global::Godot.NativeInterop.VariantUtils.ConvertTo<bool>(value);
            return true;
        }
        if (name == PropertyName.@drag_start_camera_pos) {
            this.@drag_start_camera_pos = global::Godot.NativeInterop.VariantUtils.ConvertTo<global::Godot.Vector2>(value);
            return true;
        }
        if (name == PropertyName.@drag_start_mouse_pos) {
            this.@drag_start_mouse_pos = global::Godot.NativeInterop.VariantUtils.ConvertTo<global::Godot.Vector2>(value);
            return true;
        }
        if (name == PropertyName.@camera_drag) {
            this.@camera_drag = global::Godot.NativeInterop.VariantUtils.ConvertTo<bool>(value);
            return true;
        }
        if (name == PropertyName.@camera_old_pos) {
            this.@camera_old_pos = global::Godot.NativeInterop.VariantUtils.ConvertTo<global::Godot.Vector2>(value);
            return true;
        }
        if (name == PropertyName.@mouse_pos) {
            this.@mouse_pos = global::Godot.NativeInterop.VariantUtils.ConvertTo<global::Godot.Vector2>(value);
            return true;
        }
        if (name == PropertyName.@mouse_screen_pos) {
            this.@mouse_screen_pos = global::Godot.NativeInterop.VariantUtils.ConvertTo<global::Godot.Vector2>(value);
            return true;
        }
        if (name == PropertyName.@mouse_screen_old_pos) {
            this.@mouse_screen_old_pos = global::Godot.NativeInterop.VariantUtils.ConvertTo<global::Godot.Vector2>(value);
            return true;
        }
        return base.SetGodotClassPropertyValue(name, value);
    }
    /// <inheritdoc/>
    [global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]
    protected override bool GetGodotClassPropertyValue(in godot_string_name name, out godot_variant value)
    {
        if (name == PropertyName.@view_zoom) {
            value = global::Godot.NativeInterop.VariantUtils.CreateFrom<float>(this.@view_zoom);
            return true;
        }
        if (name == PropertyName.@camera_draging) {
            value = global::Godot.NativeInterop.VariantUtils.CreateFrom<bool>(this.@camera_draging);
            return true;
        }
        if (name == PropertyName.@drag_start_camera_pos) {
            value = global::Godot.NativeInterop.VariantUtils.CreateFrom<global::Godot.Vector2>(this.@drag_start_camera_pos);
            return true;
        }
        if (name == PropertyName.@drag_start_mouse_pos) {
            value = global::Godot.NativeInterop.VariantUtils.CreateFrom<global::Godot.Vector2>(this.@drag_start_mouse_pos);
            return true;
        }
        if (name == PropertyName.@camera_drag) {
            value = global::Godot.NativeInterop.VariantUtils.CreateFrom<bool>(this.@camera_drag);
            return true;
        }
        if (name == PropertyName.@camera_old_pos) {
            value = global::Godot.NativeInterop.VariantUtils.CreateFrom<global::Godot.Vector2>(this.@camera_old_pos);
            return true;
        }
        if (name == PropertyName.@mouse_pos) {
            value = global::Godot.NativeInterop.VariantUtils.CreateFrom<global::Godot.Vector2>(this.@mouse_pos);
            return true;
        }
        if (name == PropertyName.@mouse_screen_pos) {
            value = global::Godot.NativeInterop.VariantUtils.CreateFrom<global::Godot.Vector2>(this.@mouse_screen_pos);
            return true;
        }
        if (name == PropertyName.@mouse_screen_old_pos) {
            value = global::Godot.NativeInterop.VariantUtils.CreateFrom<global::Godot.Vector2>(this.@mouse_screen_old_pos);
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
        properties.Add(new(type: (global::Godot.Variant.Type)3, name: PropertyName.@view_zoom, hint: (global::Godot.PropertyHint)0, hintString: "", usage: (global::Godot.PropertyUsageFlags)4096, exported: false));
        properties.Add(new(type: (global::Godot.Variant.Type)1, name: PropertyName.@camera_draging, hint: (global::Godot.PropertyHint)0, hintString: "", usage: (global::Godot.PropertyUsageFlags)4096, exported: false));
        properties.Add(new(type: (global::Godot.Variant.Type)5, name: PropertyName.@drag_start_camera_pos, hint: (global::Godot.PropertyHint)0, hintString: "", usage: (global::Godot.PropertyUsageFlags)4096, exported: false));
        properties.Add(new(type: (global::Godot.Variant.Type)5, name: PropertyName.@drag_start_mouse_pos, hint: (global::Godot.PropertyHint)0, hintString: "", usage: (global::Godot.PropertyUsageFlags)4096, exported: false));
        properties.Add(new(type: (global::Godot.Variant.Type)1, name: PropertyName.@camera_drag, hint: (global::Godot.PropertyHint)0, hintString: "", usage: (global::Godot.PropertyUsageFlags)4096, exported: false));
        properties.Add(new(type: (global::Godot.Variant.Type)5, name: PropertyName.@camera_old_pos, hint: (global::Godot.PropertyHint)0, hintString: "", usage: (global::Godot.PropertyUsageFlags)4096, exported: false));
        properties.Add(new(type: (global::Godot.Variant.Type)5, name: PropertyName.@mouse_pos, hint: (global::Godot.PropertyHint)0, hintString: "", usage: (global::Godot.PropertyUsageFlags)4096, exported: false));
        properties.Add(new(type: (global::Godot.Variant.Type)5, name: PropertyName.@mouse_screen_pos, hint: (global::Godot.PropertyHint)0, hintString: "", usage: (global::Godot.PropertyUsageFlags)4096, exported: false));
        properties.Add(new(type: (global::Godot.Variant.Type)5, name: PropertyName.@mouse_screen_old_pos, hint: (global::Godot.PropertyHint)0, hintString: "", usage: (global::Godot.PropertyUsageFlags)4096, exported: false));
        return properties;
    }
#pragma warning restore CS0109
}
