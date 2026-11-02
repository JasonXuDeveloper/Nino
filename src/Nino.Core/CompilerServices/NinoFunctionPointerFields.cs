using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Reflection;
using Nino.Core.Internal;

namespace Nino.Core.CompilerServices
{
    /// <summary>
    /// The function pointer (<c>delegate*</c>) fields of the structs generated code names, as the generator sees them,
    /// for <see cref="NinoLayout"/>: a function pointer is a pointer on the wire, 8 bytes zero-extended like <c>T*</c>
    /// (spec §5.2), while an <c>IntPtr</c> is sign-extended. Reflection cannot be relied on to tell them apart: Mono,
    /// IL2CPP and .NET Framework report the field as an <c>IntPtr</c>, .NET 8 and later as a function pointer type, and
    /// NativeAOT keeps only the metadata it is asked for. So the generator, which sees <c>IFunctionPointerTypeSymbol</c>,
    /// writes the fields into each assembly's registry, whose static constructor calls <see cref="Register"/>, and
    /// every runtime lays them out from that one source. On a 32-bit host an <c>IntPtr</c>-typed field is converted
    /// sign-extended, a registered one zero-extended; on 64-bit hosts both are copied as they are.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static class NinoFunctionPointerFields
    {
        /// <summary>
        /// The registered fields, keyed by the assembly name and runtime name (<see cref="NinoTypeId.GetRuntimeName"/>)
        /// of their struct's definition: names, not types, so registering loads no type (Mono 6.8 overflows its stack
        /// loading a struct with a <c>delegate*</c> field) and pins no assembly. A removed assembly's entries are dropped.
        /// </summary>
        private static readonly ConcurrentDictionary<string, string[]> Fields =
            NinoUnload.With(new ConcurrentDictionary<string, string[]>(), Forget);

        /// <summary>
        /// Registers <paramref name="fields"/>, the function pointer fields of the struct whose definition is named
        /// <paramref name="type"/> (its runtime name: <c>Ns.Outer+S&lt;T&gt;</c>) in <paramref name="assembly"/> (its
        /// simple name). Called by generated code.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public static void Register(string assembly, string type, params string[] fields)
        {
            if (assembly == null)
                throw new ArgumentNullException(nameof(assembly));
            if (type == null)
                throw new ArgumentNullException(nameof(type));
            Fields[Key(assembly, type)] = fields ?? throw new ArgumentNullException(nameof(fields));
        }

        /// <summary>
        /// Whether generated code registered <paramref name="field"/> as a function pointer. Every registry is run
        /// first when the field is not found (lookups never depend on load order): called once per field when a
        /// struct's layout is computed, never on a read or write.
        /// </summary>
        internal static bool Holds(FieldInfo field)
        {
            var declaring = field.DeclaringType;
            if (declaring == null)
                return false;
            if (declaring.IsGenericType)
                declaring = declaring.GetGenericTypeDefinition();
            string key = Key(declaring.Assembly.GetName().Name, NinoTypeId.GetRuntimeName(declaring));
            if (Contains(key, field.Name))
                return true;
            NinoRegistry.EnsureAllInitialized();
            return Contains(key, field.Name);
        }

        private static bool Contains(string key, string field) =>
            Fields.TryGetValue(key, out var fields) && Array.IndexOf(fields, field) >= 0;

        private static string Key(string assembly, string type) => assembly + "|" + type;

        private static void Forget(Assembly assembly)
        {
            string prefix = assembly.GetName().Name + "|";
            foreach (var key in Fields.Keys)
            {
                if (key.StartsWith(prefix, StringComparison.Ordinal))
                    Fields.TryRemove(key, out _);
            }
        }
    }
}
