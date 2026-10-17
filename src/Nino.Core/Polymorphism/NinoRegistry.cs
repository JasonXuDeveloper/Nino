using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
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
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public sealed class NinoGeneratedAttribute : Attribute
    {
        /// <summary>The generated registry type; its static constructor registers the assembly.</summary>
        // NonPublicConstructors keeps the static constructor that NinoRegistry.EnsureInitialized runs.
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.NonPublicConstructors)]
        public Type Registry { get; }

        public NinoGeneratedAttribute(
            [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.NonPublicConstructors)] Type registry)
        {
            Registry = registry;
        }
    }

    /// <summary>
    /// The generated registry of one assembly. Registration is lazy: a lookup that misses asks every registry to
    /// register the type with that id (a switch in generated code), so startup does not pay for types never used.
    /// </summary>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
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

        /// <summary>Assemblies whose registration has completed (not merely started).</summary>
        private static readonly HashSet<Assembly> Initialized = new HashSet<Assembly>();
        private static int _scannedAssemblyCount;
        private static INinoRegistry[] _registries = Array.Empty<INinoRegistry>();

        /// <summary>Adds an assembly's generated registry. Called by its static constructor.</summary>
        [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
        public static void Add(INinoRegistry registry)
        {
            lock (Lock)
            {
                // Not marked initialized: its static constructor is still running (EnsureInitialized waits for it).
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

        /// <summary>
        /// Runs the generated registration of <paramref name="assembly"/> once, and returns when it has completed,
        /// also when another thread is running it.
        /// </summary>
        [UnconditionalSuppressMessage("Trimming", "IL2059", Justification =
            "Runs the static constructor of NinoGeneratedAttribute.Registry, which is annotated NonPublicConstructors " +
            "(the trimmer keeps the static constructor; the generated module initializer runs it too). The .NET 9 " +
            "and later analyzers read that annotation here; the .NET 8 one does not.")]
        public static void EnsureInitialized(Assembly assembly)
        {
            // The core library and Nino.Core never contain generated code.
            if (assembly == typeof(object).Assembly || assembly == typeof(NinoRegistry).Assembly)
                return;
            lock (Lock)
            {
                if (Initialized.Contains(assembly))
                    return;
            }

            // Threads that get here at the same time all run the steps below, which are idempotent: the assembly is
            // only published as initialized once its registries' static constructors have finished, so no thread
            // returns while another is still registering. RunClassConstructor waits for a static constructor running
            // on another thread (and returns at once on the thread running it, where registries reference each other).
            NinoGeneratedAttribute[] attributes;
            try
            {
                attributes = (NinoGeneratedAttribute[])assembly.GetCustomAttributes(typeof(NinoGeneratedAttribute), false);
            }
            catch
            {
                lock (Lock)
                    Initialized.Add(assembly);
                return;
            }

            NinoFormatterResolver.AddAssembly(assembly);
            foreach (var attribute in attributes)
            {
                if (attribute.Registry != null)
                    RuntimeHelpers.RunClassConstructor(attribute.Registry.TypeHandle);
            }

            lock (Lock)
                Initialized.Add(assembly);
        }

        /// <summary>Runs the generated registration of every loaded assembly.</summary>
        public static void EnsureAllInitialized()
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            if (Volatile.Read(ref _scannedAssemblyCount) == assemblies.Length)
                return;

            // Every caller scans until one has finished: one that returned early could miss a registry still running.
            foreach (var assembly in assemblies)
            {
                if (!assembly.IsDynamic)
                    EnsureInitialized(assembly);
            }

            Volatile.Write(ref _scannedAssemblyCount, assemblies.Length);
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
