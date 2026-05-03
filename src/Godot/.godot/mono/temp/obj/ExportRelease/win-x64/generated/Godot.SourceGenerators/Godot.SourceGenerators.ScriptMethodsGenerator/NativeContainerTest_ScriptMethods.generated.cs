using Godot;
using Godot.NativeInterop;

partial class NativeContainerTest
{
#pragma warning disable CS0109 // Disable warning about redundant 'new' keyword
    /// <summary>
    /// Cached StringNames for the methods contained in this class, for fast lookup.
    /// </summary>
    public new class MethodName : global::Godot.Node.MethodName {
        /// <summary>
        /// Cached name for the 'TestLeak' method.
        /// </summary>
        public new static readonly global::Godot.StringName @TestLeak = "TestLeak";
        /// <summary>
        /// Cached name for the 'TestSafety' method.
        /// </summary>
        public new static readonly global::Godot.StringName @TestSafety = "TestSafety";
        /// <summary>
        /// Cached name for the 'ResizeTest' method.
        /// </summary>
        public new static readonly global::Godot.StringName @ResizeTest = "ResizeTest";
        /// <summary>
        /// Cached name for the '_Ready' method.
        /// </summary>
        public new static readonly global::Godot.StringName @_Ready = "_Ready";
        /// <summary>
        /// Cached name for the '_Process' method.
        /// </summary>
        public new static readonly global::Godot.StringName @_Process = "_Process";
        /// <summary>
        /// Cached name for the 'CleanupTemp' method.
        /// </summary>
        public new static readonly global::Godot.StringName @CleanupTemp = "CleanupTemp";
    }
    /// <summary>
    /// Get the method information for all the methods declared in this class.
    /// This method is used by Godot to register the available methods in the editor.
    /// Do not call this method.
    /// </summary>
    [global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]
    internal new static global::System.Collections.Generic.List<global::Godot.Bridge.MethodInfo> GetGodotMethodList()
    {
        var methods = new global::System.Collections.Generic.List<global::Godot.Bridge.MethodInfo>(6);
        methods.Add(new(name: MethodName.@TestLeak, returnVal: new(type: (global::Godot.Variant.Type)0, name: "", hint: (global::Godot.PropertyHint)0, hintString: "", usage: (global::Godot.PropertyUsageFlags)6, exported: false), flags: (global::Godot.MethodFlags)1, arguments: null, defaultArguments: null));
        methods.Add(new(name: MethodName.@TestSafety, returnVal: new(type: (global::Godot.Variant.Type)0, name: "", hint: (global::Godot.PropertyHint)0, hintString: "", usage: (global::Godot.PropertyUsageFlags)6, exported: false), flags: (global::Godot.MethodFlags)1, arguments: null, defaultArguments: null));
        methods.Add(new(name: MethodName.@ResizeTest, returnVal: new(type: (global::Godot.Variant.Type)0, name: "", hint: (global::Godot.PropertyHint)0, hintString: "", usage: (global::Godot.PropertyUsageFlags)6, exported: false), flags: (global::Godot.MethodFlags)1, arguments: null, defaultArguments: null));
        methods.Add(new(name: MethodName.@_Ready, returnVal: new(type: (global::Godot.Variant.Type)0, name: "", hint: (global::Godot.PropertyHint)0, hintString: "", usage: (global::Godot.PropertyUsageFlags)6, exported: false), flags: (global::Godot.MethodFlags)1, arguments: null, defaultArguments: null));
        methods.Add(new(name: MethodName.@_Process, returnVal: new(type: (global::Godot.Variant.Type)0, name: "", hint: (global::Godot.PropertyHint)0, hintString: "", usage: (global::Godot.PropertyUsageFlags)6, exported: false), flags: (global::Godot.MethodFlags)1, arguments: new() { new(type: (global::Godot.Variant.Type)3, name: "delta", hint: (global::Godot.PropertyHint)0, hintString: "", usage: (global::Godot.PropertyUsageFlags)6, exported: false),  }, defaultArguments: null));
        methods.Add(new(name: MethodName.@CleanupTemp, returnVal: new(type: (global::Godot.Variant.Type)0, name: "", hint: (global::Godot.PropertyHint)0, hintString: "", usage: (global::Godot.PropertyUsageFlags)6, exported: false), flags: (global::Godot.MethodFlags)1, arguments: null, defaultArguments: null));
        return methods;
    }
#pragma warning restore CS0109
    /// <inheritdoc/>
    [global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]
    protected override bool InvokeGodotClassMethod(in godot_string_name method, NativeVariantPtrArgs args, out godot_variant ret)
    {
        if (method == MethodName.@TestLeak && args.Count == 0) {
            @TestLeak();
            ret = default;
            return true;
        }
        if (method == MethodName.@TestSafety && args.Count == 0) {
            @TestSafety();
            ret = default;
            return true;
        }
        if (method == MethodName.@ResizeTest && args.Count == 0) {
            @ResizeTest();
            ret = default;
            return true;
        }
        if (method == MethodName.@_Ready && args.Count == 0) {
            @_Ready();
            ret = default;
            return true;
        }
        if (method == MethodName.@_Process && args.Count == 1) {
            @_Process(global::Godot.NativeInterop.VariantUtils.ConvertTo<double>(args[0]));
            ret = default;
            return true;
        }
        if (method == MethodName.@CleanupTemp && args.Count == 0) {
            @CleanupTemp();
            ret = default;
            return true;
        }
        return base.InvokeGodotClassMethod(method, args, out ret);
    }
    /// <inheritdoc/>
    [global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]
    protected override bool HasGodotClassMethod(in godot_string_name method)
    {
        if (method == MethodName.@TestLeak) {
           return true;
        }
        if (method == MethodName.@TestSafety) {
           return true;
        }
        if (method == MethodName.@ResizeTest) {
           return true;
        }
        if (method == MethodName.@_Ready) {
           return true;
        }
        if (method == MethodName.@_Process) {
           return true;
        }
        if (method == MethodName.@CleanupTemp) {
           return true;
        }
        return base.HasGodotClassMethod(method);
    }
}
