using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Nino.Core
{
    /// <summary>
    /// Marks an assembly that contains generated Nino code. <see cref="Registry"/> is a static class whose static
    /// constructor registers the assembly's formatters.
    /// </summary>
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public sealed class NinoGeneratedAttribute : Attribute
    {
        public readonly Type Registry;

        public NinoGeneratedAttribute(Type registry)
        {
            Registry = registry;
        }
    }

    /// <summary>
    /// Runs the generated registration of assemblies. Generated code calls this from a module initializer (and from
    /// Unity's load hooks); lookups that miss also call it lazily, so registration never depends on load order.
    /// </summary>
    public static class NinoRegistry
    {
        private static readonly object Lock = new object();
        private static readonly HashSet<Assembly> Initialized = new HashSet<Assembly>();
        private static readonly Dictionary<Type, Type> GenericFormatters = new Dictionary<Type, Type>();
        private static int _scannedAssemblyCount;

        /// <summary>Runs the generated registration of <paramref name="assembly"/> once.</summary>
        public static void EnsureInitialized(Assembly assembly)
        {
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

        /// <summary>
        /// Registers the generic formatter definition of a generic <c>[NinoType]</c>, e.g.
        /// <c>(typeof(Box&lt;&gt;), typeof(BoxFormatter&lt;&gt;))</c>. Closed formatters are built on first use.
        /// </summary>
        public static void RegisterGeneric(Type typeDefinition, Type formatterDefinition)
        {
            lock (Lock)
            {
                GenericFormatters[typeDefinition] = formatterDefinition;
            }
        }

        internal static Type FindGeneric(Type typeDefinition)
        {
            lock (Lock)
            {
                return GenericFormatters.TryGetValue(typeDefinition, out var formatter) ? formatter : null;
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
