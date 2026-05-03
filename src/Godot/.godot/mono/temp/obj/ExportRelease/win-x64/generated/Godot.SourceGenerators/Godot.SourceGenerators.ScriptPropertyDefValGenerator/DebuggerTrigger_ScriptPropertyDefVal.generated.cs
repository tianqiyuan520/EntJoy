partial class DebuggerTrigger
{
#pragma warning disable CS0109 // Disable warning about redundant 'new' keyword
#if TOOLS
    /// <summary>
    /// Get the default values for all properties declared in this class.
    /// This method is used by Godot to determine the value that will be
    /// used by the inspector when resetting properties.
    /// Do not call this method.
    /// </summary>
    [global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]
    internal new static global::System.Collections.Generic.Dictionary<global::Godot.StringName, global::Godot.Variant> GetGodotPropertyDefaultValues()
    {
        var values = new global::System.Collections.Generic.Dictionary<global::Godot.StringName, global::Godot.Variant>(2);
        global::Godot.PackedScene __debuggerPanelPackedScene_default_value = default;
        values.Add(PropertyName.@debuggerPanelPackedScene, global::Godot.Variant.From<global::Godot.PackedScene>(__debuggerPanelPackedScene_default_value));
        global::Godot.Node __Pos_default_value = default;
        values.Add(PropertyName.@Pos, global::Godot.Variant.From<global::Godot.Node>(__Pos_default_value));
        return values;
    }
#endif // TOOLS
#pragma warning restore CS0109
}
