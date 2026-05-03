using Godot;
using Godot.NativeInterop;

partial class SpritesRandomMove
{
#pragma warning disable CS0109 // Disable warning about redundant 'new' keyword
    /// <summary>
    /// Cached StringNames for the methods contained in this class, for fast lookup.
    /// </summary>
    public new class MethodName : global::Godot.Node2D.MethodName {
        /// <summary>
        /// Cached name for the 'GenerateMultiMesh' method.
        /// </summary>
        public new static readonly global::Godot.StringName @GenerateMultiMesh = "GenerateMultiMesh";
        /// <summary>
        /// Cached name for the '_Ready' method.
        /// </summary>
        public new static readonly global::Godot.StringName @_Ready = "_Ready";
        /// <summary>
        /// Cached name for the 'CreateWorld' method.
        /// </summary>
        public new static readonly global::Godot.StringName @CreateWorld = "CreateWorld";
        /// <summary>
        /// Cached name for the 'NewEntity' method.
        /// </summary>
        public new static readonly global::Godot.StringName @NewEntity = "NewEntity";
        /// <summary>
        /// Cached name for the '_PhysicsProcess' method.
        /// </summary>
        public new static readonly global::Godot.StringName @_PhysicsProcess = "_PhysicsProcess";
        /// <summary>
        /// Cached name for the 'RefreshMoveChunks' method.
        /// </summary>
        public new static readonly global::Godot.StringName @RefreshMoveChunks = "RefreshMoveChunks";
        /// <summary>
        /// Cached name for the 'TickLoop' method.
        /// </summary>
        public new static readonly global::Godot.StringName @TickLoop = "TickLoop";
        /// <summary>
        /// Cached name for the 'Display' method.
        /// </summary>
        public new static readonly global::Godot.StringName @Display = "Display";
        /// <summary>
        /// Cached name for the 'Report' method.
        /// </summary>
        public new static readonly global::Godot.StringName @Report = "Report";
        /// <summary>
        /// Cached name for the 'Pause' method.
        /// </summary>
        public new static readonly global::Godot.StringName @Pause = "Pause";
    }
    /// <summary>
    /// Get the method information for all the methods declared in this class.
    /// This method is used by Godot to register the available methods in the editor.
    /// Do not call this method.
    /// </summary>
    [global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]
    internal new static global::System.Collections.Generic.List<global::Godot.Bridge.MethodInfo> GetGodotMethodList()
    {
        var methods = new global::System.Collections.Generic.List<global::Godot.Bridge.MethodInfo>(10);
        methods.Add(new(name: MethodName.@GenerateMultiMesh, returnVal: new(type: (global::Godot.Variant.Type)24, name: "", hint: (global::Godot.PropertyHint)0, hintString: "", usage: (global::Godot.PropertyUsageFlags)6, exported: false, className: new global::Godot.StringName("MultiMeshInstance2D")), flags: (global::Godot.MethodFlags)1, arguments: null, defaultArguments: null));
        methods.Add(new(name: MethodName.@_Ready, returnVal: new(type: (global::Godot.Variant.Type)0, name: "", hint: (global::Godot.PropertyHint)0, hintString: "", usage: (global::Godot.PropertyUsageFlags)6, exported: false), flags: (global::Godot.MethodFlags)1, arguments: null, defaultArguments: null));
        methods.Add(new(name: MethodName.@CreateWorld, returnVal: new(type: (global::Godot.Variant.Type)0, name: "", hint: (global::Godot.PropertyHint)0, hintString: "", usage: (global::Godot.PropertyUsageFlags)6, exported: false), flags: (global::Godot.MethodFlags)1, arguments: null, defaultArguments: null));
        methods.Add(new(name: MethodName.@NewEntity, returnVal: new(type: (global::Godot.Variant.Type)0, name: "", hint: (global::Godot.PropertyHint)0, hintString: "", usage: (global::Godot.PropertyUsageFlags)6, exported: false), flags: (global::Godot.MethodFlags)1, arguments: null, defaultArguments: null));
        methods.Add(new(name: MethodName.@_PhysicsProcess, returnVal: new(type: (global::Godot.Variant.Type)0, name: "", hint: (global::Godot.PropertyHint)0, hintString: "", usage: (global::Godot.PropertyUsageFlags)6, exported: false), flags: (global::Godot.MethodFlags)1, arguments: new() { new(type: (global::Godot.Variant.Type)3, name: "delta", hint: (global::Godot.PropertyHint)0, hintString: "", usage: (global::Godot.PropertyUsageFlags)6, exported: false),  }, defaultArguments: null));
        methods.Add(new(name: MethodName.@RefreshMoveChunks, returnVal: new(type: (global::Godot.Variant.Type)0, name: "", hint: (global::Godot.PropertyHint)0, hintString: "", usage: (global::Godot.PropertyUsageFlags)6, exported: false), flags: (global::Godot.MethodFlags)1, arguments: null, defaultArguments: null));
        methods.Add(new(name: MethodName.@TickLoop, returnVal: new(type: (global::Godot.Variant.Type)0, name: "", hint: (global::Godot.PropertyHint)0, hintString: "", usage: (global::Godot.PropertyUsageFlags)6, exported: false), flags: (global::Godot.MethodFlags)1, arguments: new() { new(type: (global::Godot.Variant.Type)3, name: "delta", hint: (global::Godot.PropertyHint)0, hintString: "", usage: (global::Godot.PropertyUsageFlags)6, exported: false),  }, defaultArguments: null));
        methods.Add(new(name: MethodName.@Display, returnVal: new(type: (global::Godot.Variant.Type)0, name: "", hint: (global::Godot.PropertyHint)0, hintString: "", usage: (global::Godot.PropertyUsageFlags)6, exported: false), flags: (global::Godot.MethodFlags)1, arguments: null, defaultArguments: null));
        methods.Add(new(name: MethodName.@Report, returnVal: new(type: (global::Godot.Variant.Type)0, name: "", hint: (global::Godot.PropertyHint)0, hintString: "", usage: (global::Godot.PropertyUsageFlags)6, exported: false), flags: (global::Godot.MethodFlags)1, arguments: null, defaultArguments: null));
        methods.Add(new(name: MethodName.@Pause, returnVal: new(type: (global::Godot.Variant.Type)0, name: "", hint: (global::Godot.PropertyHint)0, hintString: "", usage: (global::Godot.PropertyUsageFlags)6, exported: false), flags: (global::Godot.MethodFlags)1, arguments: null, defaultArguments: null));
        return methods;
    }
#pragma warning restore CS0109
    /// <inheritdoc/>
    [global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]
    protected override bool InvokeGodotClassMethod(in godot_string_name method, NativeVariantPtrArgs args, out godot_variant ret)
    {
        if (method == MethodName.@GenerateMultiMesh && args.Count == 0) {
            var callRet = @GenerateMultiMesh();
            ret = global::Godot.NativeInterop.VariantUtils.CreateFrom<global::Godot.MultiMeshInstance2D>(callRet);
            return true;
        }
        if (method == MethodName.@_Ready && args.Count == 0) {
            @_Ready();
            ret = default;
            return true;
        }
        if (method == MethodName.@CreateWorld && args.Count == 0) {
            @CreateWorld();
            ret = default;
            return true;
        }
        if (method == MethodName.@NewEntity && args.Count == 0) {
            @NewEntity();
            ret = default;
            return true;
        }
        if (method == MethodName.@_PhysicsProcess && args.Count == 1) {
            @_PhysicsProcess(global::Godot.NativeInterop.VariantUtils.ConvertTo<double>(args[0]));
            ret = default;
            return true;
        }
        if (method == MethodName.@RefreshMoveChunks && args.Count == 0) {
            @RefreshMoveChunks();
            ret = default;
            return true;
        }
        if (method == MethodName.@TickLoop && args.Count == 1) {
            @TickLoop(global::Godot.NativeInterop.VariantUtils.ConvertTo<double>(args[0]));
            ret = default;
            return true;
        }
        if (method == MethodName.@Display && args.Count == 0) {
            @Display();
            ret = default;
            return true;
        }
        if (method == MethodName.@Report && args.Count == 0) {
            @Report();
            ret = default;
            return true;
        }
        if (method == MethodName.@Pause && args.Count == 0) {
            @Pause();
            ret = default;
            return true;
        }
        return base.InvokeGodotClassMethod(method, args, out ret);
    }
    /// <inheritdoc/>
    [global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]
    protected override bool HasGodotClassMethod(in godot_string_name method)
    {
        if (method == MethodName.@GenerateMultiMesh) {
           return true;
        }
        if (method == MethodName.@_Ready) {
           return true;
        }
        if (method == MethodName.@CreateWorld) {
           return true;
        }
        if (method == MethodName.@NewEntity) {
           return true;
        }
        if (method == MethodName.@_PhysicsProcess) {
           return true;
        }
        if (method == MethodName.@RefreshMoveChunks) {
           return true;
        }
        if (method == MethodName.@TickLoop) {
           return true;
        }
        if (method == MethodName.@Display) {
           return true;
        }
        if (method == MethodName.@Report) {
           return true;
        }
        if (method == MethodName.@Pause) {
           return true;
        }
        return base.HasGodotClassMethod(method);
    }
}
