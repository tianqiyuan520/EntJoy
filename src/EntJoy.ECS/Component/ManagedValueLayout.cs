using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace EntJoy.ECS
{
    // ECS stores CLR value bytes, not interop-marshalled structures. Reflection
    // closes this factory once per type; ordinary reads use the cached delegate.
    internal static unsafe class ManagedValueLayout
    {
        private readonly struct Layout
        {
            public readonly int Size;
            public readonly Func<IntPtr, object> Read;
            public Layout(int size, Func<IntPtr, object> read) { Size = size; Read = read; }
        }

        private static readonly ConcurrentDictionary<Type, Layout> Layouts = new();
        private static readonly MethodInfo Factory = typeof(ManagedValueLayout)
            .GetMethod(nameof(Create), BindingFlags.Static | BindingFlags.NonPublic);

        internal static int SizeOf(Type type) => Get(type).Size;
        internal static object ReadBoxed(Type type, IntPtr address) => Get(type).Read(address);

        internal static bool IsUnmanaged(Type type)
        {
            if (!type.IsValueType || type.IsByRefLike || type.ContainsGenericParameters) return false;
            try { _ = Get(type); return true; }
            catch (ArgumentException) { return false; }
        }

        private static Layout Get(Type type) => Layouts.GetOrAdd(type, static value =>
        {
            if (!value.IsValueType || value.IsByRefLike || value.ContainsGenericParameters)
                throw new ArgumentException($"Type {value} must be a closed unmanaged value type.", nameof(type));
            try { return (Layout)Factory.MakeGenericMethod(value).Invoke(null, null); }
            catch (TargetInvocationException error) when (error.InnerException is ArgumentException)
            {
                throw new ArgumentException(error.InnerException.Message, nameof(type), error.InnerException);
            }
        });

        private static Layout Create<T>() where T : struct
        {
            if (RuntimeHelpers.IsReferenceOrContainsReferences<T>())
                throw new ArgumentException($"Type {typeof(T)} contains managed references.");
            return new Layout(Unsafe.SizeOf<T>(), Read<T>);
        }

        private static object Read<T>(IntPtr address) where T : struct
            => Unsafe.ReadUnaligned<T>((void*)address);
    }
}
