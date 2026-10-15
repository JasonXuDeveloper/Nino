using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Nino.Core
{
    /// <summary>
    /// Marks an assembly that contains generated Nino code. <see cref="Registry"/> is a static class whose static
    /// constructor registers the assembly's formatters.
    /// </summary>
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public sealed class NinoGeneratedAttribute : Attribute
    {
        /// <summary>The generated registry type; its static constructor registers the assembly.</summary>
        public Type Registry { get; }

        public NinoGeneratedAttribute(Type registry)
        {
            Registry = registry;
        }
    }

    /// <summary>
    /// The generated registry of one assembly. Registration is lazy: a lookup that misses asks every registry to
    /// register the type with that id (a switch in generated code), so startup does not pay for types never used.
    /// </summary>
    public interface INinoRegistry
    {
        /// <summary>Registers the formatters of the types (and built-in closed generics) whose type id is <paramref name="id"/>.</summary>
        void Register(int id);
    }

    /// <summary>
    /// Runs the generated registration of assemblies. Generated code calls this from a module initializer (and from
    /// Unity's load hooks); lookups that miss also call it lazily, so registration never depends on load order.
    /// </summary>
    public static class NinoRegistry
    {
        private static readonly object Lock = new object();
        private static readonly HashSet<Assembly> Initialized = new HashSet<Assembly>();
        private static int _scannedAssemblyCount;
        private static INinoRegistry[] _registries = Array.Empty<INinoRegistry>();

        /// <summary>Adds an assembly's generated registry. Called by its static constructor.</summary>
        public static void Add(INinoRegistry registry)
        {
            lock (Lock)
            {
                // Its assembly's registration has run: lookups need not look for its attribute.
                Initialized.Add(registry.GetType().Assembly);
                NinoFormatterResolver.AddAssembly(registry.GetType().Assembly);
                var current = _registries;
                if (Array.IndexOf(current, registry) >= 0)
                    return;
                var next = new INinoRegistry[current.Length + 1];
                current.CopyTo(next, 0);
                next[current.Length] = registry;
                Volatile.Write(ref _registries, next);
            }
        }

        /// <summary>Asks every generated registry to register the types with type id <paramref name="id"/>.</summary>
        internal static void RegisterId(int id)
        {
            foreach (var registry in Volatile.Read(ref _registries))
                registry.Register(id);
        }

        /// <summary>
        /// Makes sure the generated formatter of <paramref name="type"/> (and of its element and argument types)
        /// is registered, if there is one.
        /// </summary>
        internal static void RegisterType(Type type)
        {
            EnsureInitializedFor(type);
            // Keyed by the plain name hash (generated registries also list [NinoFormerName] types under it), which
            // needs no attribute reflection.
            if (!type.ContainsGenericParameters)
                RegisterId(NinoTypeId.Compute(NinoTypeId.GetName(type)));
        }

        /// <summary>Runs the generated registration of <paramref name="assembly"/> once.</summary>
        public static void EnsureInitialized(Assembly assembly)
        {
            // The core library and Nino.Core never contain generated code.
            if (assembly == typeof(object).Assembly || assembly == typeof(NinoRegistry).Assembly)
                return;
            lock (Lock)
            {
                if (!Initialized.Add(assembly))
                    return;
            }

            NinoGeneratedAttribute[] attributes;
            try
            {
                attributes = (NinoGeneratedAttribute[])assembly.GetCustomAttributes(typeof(NinoGeneratedAttribute), false);
            }
            catch
            {
                return;
            }

            NinoFormatterResolver.AddAssembly(assembly);
            foreach (var attribute in attributes)
            {
                if (attribute.Registry != null)
                    RuntimeHelpers.RunClassConstructor(attribute.Registry.TypeHandle);
            }
        }

        /// <summary>Runs the generated registration of every loaded assembly.</summary>
        public static void EnsureAllInitialized()
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            lock (Lock)
            {
                if (assemblies.Length == _scannedAssemblyCount)
                    return;
                _scannedAssemblyCount = assemblies.Length;
            }

            foreach (var assembly in assemblies)
            {
                if (!assembly.IsDynamic)
                    EnsureInitialized(assembly);
            }
        }

        internal static void EnsureInitializedFor(Type type)
        {
            EnsureInitialized(type.Assembly);
            if (type.IsArray)
            {
                EnsureInitializedFor(type.GetElementType());
                return;
            }

            if (type.IsGenericType)
            {
                foreach (var arg in type.GetGenericArguments())
                    EnsureInitializedFor(arg);
            }
        }
    }
}
