using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using Nino.Core.CompilerServices;
using Nino.Core.Internal;

namespace Nino.Core
{
    /// <summary>
    /// Runs the generated registration of assemblies. Generated code calls this from a module initializer (and from
    /// Unity's load hooks); lookups that miss also call it lazily, so registration never depends on load order.
    /// </summary>
    public static class NinoRegistry
    {
        private static readonly object Lock = new object();

        /// <summary>Assemblies whose registration has completed (not merely started).</summary>
        private static readonly ConcurrentDictionary<Assembly, bool> Initialized =
            new ConcurrentDictionary<Assembly, bool>();

        /// <summary>
        /// Assemblies passed to <see cref="Remove"/> (weakly held): never registered again, although an unloading
        /// assembly stays in <c>AppDomain.GetAssemblies</c> until it is collected.
        /// </summary>
        private static readonly ConditionalWeakTable<Assembly, object> Removed =
            new ConditionalWeakTable<Assembly, object>();

        /// <summary>The assembly count and load count <see cref="EnsureAllInitialized"/> last scanned (-1: never).</summary>
        private static int _scannedAssemblyCount = -1, _scannedLoads = -1;

        /// <summary>Assemblies loaded since <see cref="EnsureAllInitialized"/> first ran (AppDomain.AssemblyLoad).</summary>
        private static int _loads;

        /// <summary>Bumped when what a read by type id can find may have grown or shrunk (<see cref="Changed"/>).</summary>
        private static int _epoch;

        /// <summary>See <see cref="Changed"/>.</summary>
        internal static int Epoch => Volatile.Read(ref _epoch);

        /// <summary>Assemblies loaded since the first <see cref="EnsureAllInitialized"/> (0 where AssemblyLoad is not raised).</summary>
        internal static int Loads => Volatile.Read(ref _loads);

        /// <summary>
        /// Called when a registry is added or removed, or a formatter published: an unknown type id may now be
        /// found (<see cref="NinoUnknownIds"/>).
        /// </summary>
        internal static void Changed() => Interlocked.Increment(ref _epoch);

        /// <summary>1 once <see cref="HookLoads"/> has subscribed.</summary>
        private static int _loadHooked;

        /// <summary>Whether <see cref="Loads"/> counts every load (the subscription succeeded).</summary>
        private static bool _loadsCounted;

        /// <summary>See <see cref="_loadsCounted"/>.</summary>
        internal static bool LoadsCounted => Volatile.Read(ref _loadsCounted);

        private static INinoRegistry[] _registries = Array.Empty<INinoRegistry>();

        /// <summary>Adds an assembly's generated registry. Called by its static constructor.</summary>
        [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
        public static void Add(INinoRegistry registry)
        {
            lock (Lock)
            {
                // Not marked initialized: its static constructor is still running (EnsureInitialized waits for it).
                var assembly = registry.GetType().Assembly;
                if (IsRemoved(assembly))
                    return;
                NinoFormatterResolver.AddAssembly(assembly);
                RemoveOnUnload(assembly);
                var current = _registries;
                if (Array.IndexOf(current, registry) >= 0)
                    return;
                var next = new INinoRegistry[current.Length + 1];
                current.CopyTo(next, 0);
                next[current.Length] = registry;
                Volatile.Write(ref _registries, next);
                Changed();
            }
        }

        /// <summary>
        /// Forgets everything Nino holds of <paramref name="assembly"/>: its generated registry, the formatters,
        /// type ids, layouts and polymorphic entries of its types (and of arrays and generics built from them), and
        /// its <c>[assembly: NinoFormatter]</c> registrations. Afterwards Nino.Core keeps no reference into the
        /// assembly, so a collectible <c>AssemblyLoadContext</c> that loaded it can unload.
        /// <para>
        /// In the .NET 8 and later builds of Nino.Core (what .NET 8+ apps get) this runs on its own when the
        /// collectible context of an assembly with generated Nino code starts unloading. The netstandard builds
        /// cannot see <c>AssemblyLoadContext</c>: there, and to be sure it has run, call it yourself before
        /// unloading. Call it once nothing serializes or deserializes the assembly's types any more: the assembly cannot
        /// be used with Nino afterwards. Formatters it registered for types declared outside it (an
        /// <c>[assembly: NinoFormatter]</c> for a type of another assembly that has already been used) stay.
        /// </para>
        /// </summary>
        public static void Remove(Assembly assembly)
        {
            if (assembly == null)
                throw new ArgumentNullException(nameof(assembly));
            lock (Lock)
            {
                Initialized.TryRemove(assembly, out _);
                if (!IsRemoved(assembly))
                    Removed.Add(assembly, Lock);
                var current = _registries;
                var kept = Array.FindAll(current, registry => registry.GetType().Assembly != assembly);
                if (kept.Length != current.Length)
                    Volatile.Write(ref _registries, kept);
                Volatile.Write(ref _scannedAssemblyCount, -1);
                Changed();
            }

            NinoUnload.Remove(assembly);
        }

        /// <summary>Whether <see cref="Remove"/> has been called for <paramref name="assembly"/>. Called under <see cref="Lock"/>.</summary>
        private static bool IsRemoved(Assembly assembly) => Removed.TryGetValue(assembly, out _);

        /// <summary>Removes a collectible assembly when its load context unloads (.NET 8 and later).</summary>
        private static void RemoveOnUnload(Assembly assembly)
        {
#if NET8_0_OR_GREATER
            if (!assembly.IsCollectible || Hooked.TryGetValue(assembly, out _))
                return;
            Hooked.Add(assembly, Lock);
            var context = System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(assembly);
            if (context != null)
                context.Unloading += _ => Remove(assembly);
#endif
        }

#if NET8_0_OR_GREATER
        /// <summary>Collectible assemblies whose unloading is hooked (weakly held: the context owns the hook).</summary>
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Assembly, object> Hooked =
            new System.Runtime.CompilerServices.ConditionalWeakTable<Assembly, object>();
#endif

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
            if (type.ContainsGenericParameters)
                return;
            // A registry can register the type by calling back into NinoPolymorphic.Register(Type) (a closed generic
            // whose formatter is in another assembly), which lands here again for the same type: that inner call
            // returns, and the outer one goes on to close the formatter itself.
            var registering = t_registering ??= new List<Type>();
            if (registering.Contains(type))
                return;
            registering.Add(type);
            try
            {
                // Keyed by the plain name hash (generated registries also list [NinoFormerName] types under it), which
                // needs no attribute reflection.
                RegisterId(NinoTypeId.Compute(NinoTypeId.GetName(type)));
            }
            finally
            {
                registering.Remove(type);
            }
        }

        /// <summary>The types this thread is registering (<see cref="RegisterType"/>).</summary>
        [ThreadStatic] private static List<Type> t_registering;

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
            // No lock: both are safe to read from any thread. A removed assembly is still listed while it unloads:
            // registering it again would pin it.
            if (Initialized.ContainsKey(assembly) || IsRemoved(assembly))
                return;

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
                Initialized.TryAdd(assembly, true);
                return;
            }

            lock (Lock)
            {
                // Remove may have run meanwhile (on another thread).
                if (IsRemoved(assembly))
                    return;
                NinoFormatterResolver.AddAssembly(assembly);
                RemoveOnUnload(assembly);
            }

            foreach (var attribute in attributes)
            {
                if (attribute.Registry != null)
                    RuntimeHelpers.RunClassConstructor(attribute.Registry.TypeHandle);
            }

            lock (Lock)
            {
                if (!IsRemoved(assembly))
                    Initialized.TryAdd(assembly, true);
            }
        }

        /// <summary>Runs the generated registration of every loaded assembly.</summary>
        public static void EnsureAllInitialized()
        {
            HookLoads();
            // The load count catches an unload followed by a load, which leaves the assembly count unchanged; the
            // assembly count catches loads on runtimes that do not raise AssemblyLoad.
            int loads = Volatile.Read(ref _loads);
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            if (Volatile.Read(ref _scannedAssemblyCount) == assemblies.Length &&
                Volatile.Read(ref _scannedLoads) == loads)
                return;

            // Every caller scans until one has finished: one that returned early could miss a registry still running.
            foreach (var assembly in assemblies)
            {
                if (!assembly.IsDynamic)
                    EnsureInitialized(assembly);
            }

            Volatile.Write(ref _scannedLoads, loads);
            Volatile.Write(ref _scannedAssemblyCount, assemblies.Length);
        }

        /// <summary>Counts assembly loads from now on (once).</summary>
        private static void HookLoads()
        {
            if (Volatile.Read(ref _loadHooked) != 0 || Interlocked.Exchange(ref _loadHooked, 1) != 0)
                return;
            try
            {
                AppDomain.CurrentDomain.AssemblyLoad += (_, _) => Interlocked.Increment(ref _loads);
                Volatile.Write(ref _loadsCounted, true);
            }
            catch
            {
                // Not supported (some AOT runtimes): the assembly count alone decides.
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
