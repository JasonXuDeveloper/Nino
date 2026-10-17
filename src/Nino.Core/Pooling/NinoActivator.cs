using System;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Nino.Core
{
    /// <summary>
    /// On a type parameter of a formatter (<c>struct F&lt;[NinoCreated] TCollection, ...&gt;</c>): the formatter creates
    /// instances of the parameter through <see cref="NinoActivator{T}.Create"/>, so the bound type must be a non-abstract
    /// class with a public static <see cref="NinoFactoryAttribute"/> factory or a public constructor that is
    /// callable without arguments (every parameter optional). The generator and the runtime resolver only bind such
    /// types, and the generator registers a creator for each one it binds.
    /// <para>
    /// Use it instead of the <c>new()</c> constraint, which admits only a parameterless constructor (not
    /// <c>MultiMap(int maxPoolCount = 0)</c>, not a pooled <c>static X Create()</c>) and which generic code compiles
    /// to <c>Activator.CreateInstance&lt;T&gt;()</c>. A plain <c>new()</c> constraint is still honored.
    /// </para>
    /// </summary>
    [AttributeUsage(AttributeTargets.GenericParameter)]
    public sealed class NinoCreatedAttribute : Attribute
    {
    }

    /// <summary>
    /// Creates the instances of <typeparamref name="T"/> that Nino needs (collections deserialized into a null slot).
    /// <para>
    /// The creator is one static field of the closed type, so <see cref="Create"/> is a field load and a delegate call.
    /// Generated code registers it for every closed collection type the generator binds to a
    /// <see cref="NinoCreatedAttribute"/> parameter, lazily (with the formatters of that type id), as
    /// <c>() =&gt; T.Factory()</c> for a public static <see cref="NinoFactoryAttribute"/> method callable
    /// without arguments declared on <typeparamref name="T"/>, else <c>() =&gt; new T()</c>. Types it never saw (built at
    /// runtime, nested elements of runtime-resolved formatters) are resolved by reflection on first use, in the same
    /// order, and cached as a delegate.
    /// </para>
    /// </summary>
    public static class NinoActivator<T>
    {
        private static Func<T> _create;

        /// <summary>A new instance of <typeparamref name="T"/>.</summary>
        /// <exception cref="InvalidOperationException">
        /// <typeparamref name="T"/> cannot be created, or its factory returned null.
        /// </exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T Create()
        {
            var value = (_create ?? Resolve())();
            if (value == null)
                NinoThrow.FactoryReturnedNull(typeof(T));
            return value;
        }

        /// <summary>Whether a creator has been registered or resolved.</summary>
        public static bool IsRegistered => Volatile.Read(ref _create) != null;

        /// <summary>Registers the creator unless one is already set. Used by generated code.</summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public static void TryRegister(Func<T> create)
        {
            if (create == null)
                throw new ArgumentNullException(nameof(create));
            if (Interlocked.CompareExchange(ref _create, create, null) == null)
                NinoActivator.OnRegistered(typeof(T));
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Func<T> Resolve()
        {
            // The generated registration of T's assembly (and of assemblies using T) may name a creator.
            NinoRegistry.RegisterType(typeof(T));
            var create = Volatile.Read(ref _create);
            if (create != null)
                return create;
            TryRegister(NinoActivator.Reflect<T>());
            return _create;
        }
    }

    internal static class NinoActivator
    {
        /// <summary>Why looking up the constructors and factories of a type by reflection is safe after trimming.</summary>
        private const string CreatorJustification =
            "The fallback for types no generated code registered a creator for. Generated code registers one " +
            "(() => new T() or the [NinoFactory] call) for every closed type it binds to a [NinoCreated] parameter, " +
            "which keeps that constructor or factory; what is left are types only built at run time. Their " +
            "constructors and factories are kept when the program uses them; one the trimmer removed is not found, " +
            "and the creator then throws Nino's 'cannot create' InvalidOperationException rather than misbehave. " +
            "Value types are created as Activator.CreateInstance<T>() always created them.";

        /// <summary>
        /// A creator of <typeparamref name="T"/> was set: pooled instances may now be dropped from slots of a base type,
        /// so collections walk the elements they drop (see <see cref="NinoRecycler{T}.MayRecycle"/>).
        /// </summary>
        public static void OnRegistered(Type type)
        {
            if (typeof(INinoPooled).IsAssignableFrom(type))
                NinoRecycler.PoolsInUse = true;
        }

        /// <summary>
        /// The creator found by reflection, preferring what the generator prefers: a public static
        /// <see cref="NinoFactoryAttribute"/> method of <typeparamref name="T"/> returning it and callable
        /// without arguments, then a public parameterless constructor, then a public constructor whose parameters are
        /// all optional (called with their default values).
        /// </summary>
        [UnconditionalSuppressMessage("Trimming", "IL2090", Justification = CreatorJustification)]
        [UnconditionalSuppressMessage("Trimming", "IL2091", Justification = CreatorJustification)]
        public static Func<T> Reflect<T>()
        {
            var type = typeof(T);
            if (type.IsValueType)
                return Activator.CreateInstance<T>;
            if (type.IsAbstract || type.IsInterface || type.ContainsGenericParameters)
                return () =>
                {
                    NinoThrow.CannotCreate(type, "it is abstract");
                    return default;
                };

            var factory = FindFactory(type);
            if (factory != null)
            {
                if (factory.GetParameters().Length == 0)
                    return (Func<T>)Delegate.CreateDelegate(typeof(Func<T>), factory);
                var args = DefaultArguments(factory);
                return () => (T)factory.Invoke(null, args);
            }

            var parameterless = type.GetConstructor(Type.EmptyTypes);
            if (parameterless != null && SetsRequiredMembers(type, parameterless))
                return Compile<T>(parameterless) ?? Activator.CreateInstance<T>;

            foreach (var constructor in type.GetConstructors())
            {
                if (!CallableWithoutArguments(constructor) || !SetsRequiredMembers(type, constructor))
                    continue;
                var args = DefaultArguments(constructor);
                return () => (T)constructor.Invoke(args);
            }

            return () =>
            {
                NinoThrow.CannotCreate(type, "it needs a public constructor callable without arguments (that sets " +
                                             "its required members) or a public static [NinoFactory] factory");
                return default;
            };
        }

        /// <summary>
        /// Whether calling <paramref name="constructor"/> alone is allowed: the type has no C# 11 <c>required</c>
        /// members, or the constructor is marked <c>[SetsRequiredMembers]</c> (as <c>new T()</c> requires).
        /// </summary>
        private static bool SetsRequiredMembers(Type type, ConstructorInfo constructor)
        {
            if (!HasRequiredMembers(type))
                return true;
            foreach (var data in constructor.CustomAttributes)
            {
                if (data.AttributeType.FullName == "System.Diagnostics.CodeAnalysis.SetsRequiredMembersAttribute")
                    return true;
            }

            return false;
        }

        private static bool HasRequiredMembers(Type type)
        {
            for (var t = type; t != null; t = t.BaseType)
            {
                foreach (var data in t.CustomAttributes)
                {
                    if (data.AttributeType.FullName == "System.Runtime.CompilerServices.RequiredMemberAttribute")
                        return true;
                }
            }

            return false;
        }

        /// <summary>
        /// <c>() =&gt; new T()</c> compiled, where the runtime compiles dynamic code (CoreCLR, Mono JIT): as fast as the
        /// generated creator. Null elsewhere (IL2CPP, NativeAOT, netstandard2.0), where
        /// <c>Activator.CreateInstance&lt;T&gt;</c> is what <c>new T()</c> in generic code always was.
        /// </summary>
        private static Func<T> Compile<T>(ConstructorInfo constructor)
        {
#if NETSTANDARD2_1 || NET8_0_OR_GREATER
            if (!RuntimeFeature.IsDynamicCodeCompiled)
                return null;
            try
            {
                return System.Linq.Expressions.Expression.Lambda<Func<T>>(
                    System.Linq.Expressions.Expression.New(constructor)).Compile();
            }
            catch
            {
                return null;
            }
#else
            return null;
#endif
        }

        /// <summary>A public static <see cref="NinoFactoryAttribute"/> factory declared on <paramref name="type"/>.</summary>
        [UnconditionalSuppressMessage("Trimming", "IL2070", Justification = CreatorJustification)]
        internal static MethodInfo FindFactory(Type type)
        {
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly))
            {
                if (method.ReturnType == type && !method.ContainsGenericParameters && CallableWithoutArguments(method) &&
                    method.IsDefined(typeof(NinoFactoryAttribute), false))
                    return method;
            }

            return null;
        }

        /// <summary>Whether a class has what a <see cref="NinoCreatedAttribute"/> parameter requires.</summary>
        [UnconditionalSuppressMessage("Trimming", "IL2070", Justification = CreatorJustification)]
        internal static bool IsCreatable(Type type)
        {
            if (type.IsValueType || type.IsAbstract || type.IsInterface || type.ContainsGenericParameters)
                return false;
            if (FindFactory(type) != null)
                return true;
            foreach (var constructor in type.GetConstructors())
            {
                if (CallableWithoutArguments(constructor) && SetsRequiredMembers(type, constructor))
                    return true;
            }

            return false;
        }

        private static bool CallableWithoutArguments(MethodBase method)
        {
            foreach (var parameter in method.GetParameters())
            {
                if (!parameter.IsOptional && !IsParams(parameter))
                    return false;
            }

            return true;
        }

        /// <summary>An empty array of <paramref name="arrayType"/> (a params parameter left out).</summary>
        private static Array EmptyArray(Type arrayType)
        {
#if NET9_0_OR_GREATER
            // The array type itself, so no array type is made at run time (NativeAOT cannot).
            return Array.CreateInstanceFromArrayType(arrayType, 0);
#elif NET8_0_OR_GREATER
            if (RuntimeFeature.IsDynamicCodeSupported)
                return Array.CreateInstance(arrayType.GetElementType()!, 0);
            // NativeAOT on .NET 8 cannot make the array type at run time: the parameter's own default (null) instead.
            return null;
#else
            return Array.CreateInstance(arrayType.GetElementType()!, 0);
#endif
        }

        private static bool IsParams(ParameterInfo parameter) =>
            parameter.IsDefined(typeof(ParamArrayAttribute), false);

        [UnconditionalSuppressMessage("Trimming", "IL2072", Justification =
            "Creates default(T) for a value-type parameter left out of a constructor or factory call: a value type " +
            "is creatable without a constructor, and an explicit parameterless one would not be what default(T) " +
            "means for an omitted argument anyway.")]
        private static object[] DefaultArguments(MethodBase method)
        {
            var parameters = method.GetParameters();
            var args = new object[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                var parameter = parameters[i];
                if (IsParams(parameter))
                    args[i] = EmptyArray(parameter.ParameterType);
                else if (parameter.HasDefaultValue && parameter.DefaultValue != null)
                    args[i] = parameter.DefaultValue;
                else
                    args[i] = parameter.ParameterType.IsValueType
                        ? Activator.CreateInstance(parameter.ParameterType)
                        : null;
            }

            return args;
        }
    }
}
