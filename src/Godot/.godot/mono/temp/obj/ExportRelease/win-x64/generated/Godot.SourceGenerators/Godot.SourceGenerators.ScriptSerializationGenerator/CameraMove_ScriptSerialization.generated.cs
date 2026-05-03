using Godot;
using Godot.NativeInterop;

partial class CameraMove
{
    /// <inheritdoc/>
    [global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]
    protected override void SaveGodotObjectData(global::Godot.Bridge.GodotSerializationInfo info)
    {
        base.SaveGodotObjectData(info);
        info.AddProperty(PropertyName.@view_zoom, global::Godot.Variant.From<float>(this.@view_zoom));
        info.AddProperty(PropertyName.@camera_draging, global::Godot.Variant.From<bool>(this.@camera_draging));
        info.AddProperty(PropertyName.@drag_start_camera_pos, global::Godot.Variant.From<global::Godot.Vector2>(this.@drag_start_camera_pos));
        info.AddProperty(PropertyName.@drag_start_mouse_pos, global::Godot.Variant.From<global::Godot.Vector2>(this.@drag_start_mouse_pos));
        info.AddProperty(PropertyName.@camera_drag, global::Godot.Variant.From<bool>(this.@camera_drag));
        info.AddProperty(PropertyName.@camera_old_pos, global::Godot.Variant.From<global::Godot.Vector2>(this.@camera_old_pos));
        info.AddProperty(PropertyName.@mouse_pos, global::Godot.Variant.From<global::Godot.Vector2>(this.@mouse_pos));
        info.AddProperty(PropertyName.@mouse_screen_pos, global::Godot.Variant.From<global::Godot.Vector2>(this.@mouse_screen_pos));
        info.AddProperty(PropertyName.@mouse_screen_old_pos, global::Godot.Variant.From<global::Godot.Vector2>(this.@mouse_screen_old_pos));
    }
    /// <inheritdoc/>
    [global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]
    protected override void RestoreGodotObjectData(global::Godot.Bridge.GodotSerializationInfo info)
    {
        base.RestoreGodotObjectData(info);
        if (info.TryGetProperty(PropertyName.@view_zoom, out var _value_view_zoom))
            this.@view_zoom = _value_view_zoom.As<float>();
        if (info.TryGetProperty(PropertyName.@camera_draging, out var _value_camera_draging))
            this.@camera_draging = _value_camera_draging.As<bool>();
        if (info.TryGetProperty(PropertyName.@drag_start_camera_pos, out var _value_drag_start_camera_pos))
            this.@drag_start_camera_pos = _value_drag_start_camera_pos.As<global::Godot.Vector2>();
        if (info.TryGetProperty(PropertyName.@drag_start_mouse_pos, out var _value_drag_start_mouse_pos))
            this.@drag_start_mouse_pos = _value_drag_start_mouse_pos.As<global::Godot.Vector2>();
        if (info.TryGetProperty(PropertyName.@camera_drag, out var _value_camera_drag))
            this.@camera_drag = _value_camera_drag.As<bool>();
        if (info.TryGetProperty(PropertyName.@camera_old_pos, out var _value_camera_old_pos))
            this.@camera_old_pos = _value_camera_old_pos.As<global::Godot.Vector2>();
        if (info.TryGetProperty(PropertyName.@mouse_pos, out var _value_mouse_pos))
            this.@mouse_pos = _value_mouse_pos.As<global::Godot.Vector2>();
        if (info.TryGetProperty(PropertyName.@mouse_screen_pos, out var _value_mouse_screen_pos))
            this.@mouse_screen_pos = _value_mouse_screen_pos.As<global::Godot.Vector2>();
        if (info.TryGetProperty(PropertyName.@mouse_screen_old_pos, out var _value_mouse_screen_old_pos))
            this.@mouse_screen_old_pos = _value_mouse_screen_old_pos.As<global::Godot.Vector2>();
    }
}
