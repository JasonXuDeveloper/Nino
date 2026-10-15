using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Nino.Core
{
    /// <summary>
    /// The formatter used for <typeparamref name="T"/> by the top-level <see cref="NinoSerializer"/> /
    /// <see cref="NinoDeserializer"/> APIs and by <see cref="CachedFormatter{T}"/>.
    /// <para>
    /// Generated code registers the formatters of <c>[NinoType]</c>s. Anything else (collections, nullables,
    /// tuples, generic <c>[NinoType]</c>s...) is built on first use. Reading the instance is a static field load.
    /// </para>
    /// </summary>
    public static class NinoFormatterCache<T>
    {
        private static NinoFormatter<T> _instance;

        /// <summary>The formatter for <typeparamref name="T"/>; resolved on first access.</summary>
        public static NinoFormatter<T> Instance
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => _instance ?? Resolve();
        }

        /// <summary>Whether a formatter has been registered or resolved.</summary>
        public static bool IsRegistered => Volatile.Read(ref _instance) != null;

        /// <summary>Registers (or replaces) the formatter for <typeparamref name="T"/>.</summary>
        public static void Register(NinoFormatter<T> formatter)
        {
            Volatile.Write(ref _instance, formatter ?? throw new ArgumentNullException(nameof(formatter)));
        }

        /// <summary>Registers (or replaces) the formatter for <typeparamref name="T"/>.</summary>
        public static void Register<TFormatter>() where TFormatter : struct, INinoFormatter<T>
        {
            Register(new StructFormatter<T, TFormatter>());
        }

        /// <summary>Registers the formatter unless one is already registered. Used by generated code.</summary>
        public static void TryRegister<TFormatter>() where TFormatter : struct, INinoFormatter<T>
        {
            if (Volatile.Read(ref _instance) == null)
                Interlocked.CompareExchange(ref _instance, new StructFormatter<T, TFormatter>(), null);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static NinoFormatter<T> Resolve()
        {
            NinoRegistry.RegisterType(typeof(T));
            var instance = Volatile.Read(ref _instance);
            if (instance != null)
                return instance;

            var formatterType = NinoFormatterResolver.GetFormatterType(typeof(T), topLevel: true);
            if (formatterType == null)
                NinoThrow.NoFormatter(typeof(T));

            var created = (NinoFormatter<T>)Activator.CreateInstance(
                typeof(StructFormatter<,>).MakeGenericType(typeof(T), formatterType));
            Interlocked.CompareExchange(ref _instance, created, null);
            return _instance;
        }
    }

    /// <summary>
    /// Whether the registered formatter of <typeparamref name="T"/> writes its raw memory. Evaluated once, on the first
    /// top-level call for a value type; as a <c>static readonly</c> field it is a JIT-time constant afterwards, so the
    /// top-level APIs copy raw values directly instead of calling the formatter. Register custom formatters for
    /// such types before first use.
    /// </summary>
    internal static class NinoRaw<T>
    {
        public static readonly bool IsRaw = Compute();

        private static bool Compute()
        {
            if (NinoUnsafe.IsReferenceOrContainsReferences<T>())
                return false;
            try
            {
                return NinoFormatterCache<T>.Instance.IsRaw;
            }
            catch
            {
                return false; // no formatter: the regular path reports it
            }
        }
    }

    /// <summary>
    /// Builds struct formatter types for types without a registered formatter. Uses reflection, so it only runs on
    /// first use of a type. Ahead-of-time platforms (IL2CPP) need the generated registrations for value-type
    /// instantiations; the generator emits them for every type it sees used.
    /// </summary>
    public static class NinoFormatterResolver
    {
        private static readonly ConcurrentDictionary<Type, Type> Cache = new ConcurrentDictionary<Type, Type>();

        private static readonly Dictionary<Type, Type> InterfaceCollections = new Dictionary<Type, Type>
        {
            { typeof(IEnumerable<>), typeof(List<>) },
            { typeof(ICollection<>), typeof(List<>) },
            { typeof(IList<>), typeof(List<>) },
            { typeof(IReadOnlyCollection<>), typeof(List<>) },
            { typeof(IReadOnlyList<>), typeof(List<>) },
            { typeof(ISet<>), typeof(HashSet<>) },
#if NET8_0_OR_GREATER
            { typeof(IReadOnlySet<>), typeof(HashSet<>) },
#endif
        };

        private static readonly Type[] ValueTupleDefinitions =
        {
            typeof(ValueTuple<>), typeof(ValueTuple<,>), typeof(ValueTuple<,,>), typeof(ValueTuple<,,,>),
            typeof(ValueTuple<,,,,>), typeof(ValueTuple<,,,,,>), typeof(ValueTuple<,,,,,,>)
        };

        private static readonly Type[] TupleDefinitions =
        {
            typeof(Tuple<>), typeof(Tuple<,>), typeof(Tuple<,,>), typeof(Tuple<,,,>),
            typeof(Tuple<,,,,>), typeof(Tuple<,,,,,>), typeof(Tuple<,,,,,,>)
        };

        private static readonly Type[] ValueTupleFormatters =
        {
            typeof(ValueTupleFormatter<,>), typeof(ValueTupleFormatter<,,,>), typeof(ValueTupleFormatter<,,,,,>),
            typeof(ValueTupleFormatter<,,,,,,,>), typeof(ValueTupleFormatter<,,,,,,,,,>),
            typeof(ValueTupleFormatter<,,,,,,,,,,,>), typeof(ValueTupleFormatter<,,,,,,,,,,,,,>)
        };

        private static readonly Type[] TupleFormatters =
        {
            typeof(TupleFormatter<,>), typeof(TupleFormatter<,,,>), typeof(TupleFormatter<,,,,,>),
            typeof(TupleFormatter<,,,,,,,>), typeof(TupleFormatter<,,,,,,,,,>),
            typeof(TupleFormatter<,,,,,,,,,,,>), typeof(TupleFormatter<,,,,,,,,,,,,,>)
        };

        /// <summary>Whether <paramref name="type"/> or one of its base types / interfaces is a <c>[NinoType]</c>.</summary>
        public static bool IsNinoType(Type type)
        {
            if (type.IsDefined(typeof(NinoTypeAttribute), false))
                return true;
            for (var t = type.BaseType; t != null; t = t.BaseType)
            {
                if (t.IsDefined(typeof(NinoTypeAttribute), false))
                    return true;
            }

            foreach (var i in type.GetInterfaces())
            {
                if (i.IsDefined(typeof(NinoTypeAttribute), false))
                    return true;
            }

            return false;
        }

        /// <summary>
        /// Returns a struct type implementing <c>INinoFormatter&lt;type&gt;</c>, or null if Nino cannot format the
        /// type.
        /// </summary>
        public static Type GetFormatterType(Type type, bool topLevel = false)
        {
            if (!topLevel)
                return Cache.GetOrAdd(type, t => Build(t, false));
            return Build(type, true);
        }

        private static Type ElementFormatter(Type type)
        {
            NinoRegistry.RegisterType(type);
            if (IsRegistered(type) || IsNinoType(type))
                return typeof(CachedFormatter<>).MakeGenericType(type);
            return GetFormatterType(type) ?? typeof(CachedFormatter<>).MakeGenericType(type);
        }

        private static bool IsRegistered(Type type)
        {
            return (bool)typeof(NinoFormatterCache<>).MakeGenericType(type)
                .GetProperty(nameof(NinoFormatterCache<int>.IsRegistered))!.GetValue(null);
        }

        private static bool IsRawUnmanaged(Type type)
        {
            if (!NinoUnsafe.IsUnmanaged(type) || IsNinoType(type) || type == typeof(IntPtr) ||
                type == typeof(UIntPtr) || type == typeof(DateTimeOffset) || type.IsPointer)
                return false;
            if (type.IsGenericType)
            {
                var definition = type.GetGenericTypeDefinition();
                if (definition == typeof(Nullable<>) || definition == typeof(KeyValuePair<,>) ||
                    Array.IndexOf(ValueTupleDefinitions, definition) >= 0)
                    return false;
            }

            return true;
        }

        private static Type Build(Type type, bool topLevel)
        {
            if (type == typeof(string))
                return typeof(StringFormatter);
            if (type == typeof(object))
                return typeof(PolymorphicFormatter<object>);
            if (type == typeof(IntPtr))
                return typeof(IntPtrFormatter);
            if (type == typeof(UIntPtr))
                return typeof(UIntPtrFormatter);
            if (type == typeof(DateTimeOffset))
                return typeof(DateTimeOffsetFormatter);

            if (type.IsArray)
            {
                var element = type.GetElementType()!;
                switch (type.GetArrayRank())
                {
                    case 1 when type == element.MakeArrayType():
                        if (IsRawUnmanaged(element))
                            return typeof(UnmanagedArrayFormatter<>).MakeGenericType(element);
                        if (Nullable.GetUnderlyingType(element) is { } underlying && IsRawUnmanaged(underlying))
                            return typeof(NullableArrayFormatter<>).MakeGenericType(underlying);
                        return typeof(ArrayFormatter<,>).MakeGenericType(element, ElementFormatter(element));
                    case 2:
                        return typeof(Array2DFormatter<,>).MakeGenericType(element, ElementFormatter(element));
                    case 3:
                        return typeof(Array3DFormatter<,>).MakeGenericType(element, ElementFormatter(element));
                    case 4:
                        return typeof(Array4DFormatter<,>).MakeGenericType(element, ElementFormatter(element));
                    default:
                        return null;
                }
            }

            if (!topLevel && IsNinoType(type))
                return null; // use the registered formatter through CachedFormatter<T>

            if (IsRawUnmanaged(type))
                return typeof(UnmanagedFormatter<>).MakeGenericType(type);

            if (type.IsGenericType)
            {
                var result = BuildGeneric(type, type.GetGenericTypeDefinition(), type.GetGenericArguments());
                if (result != null)
                    return result;
            }

            // User collections derived from List<T>, Dictionary<TKey, TValue> or HashSet<T>.
            if (!type.IsAbstract && type.GetConstructor(Type.EmptyTypes) != null)
            {
                for (var b = type.BaseType; b != null; b = b.BaseType)
                {
                    if (!b.IsGenericType)
                        continue;
                    var definition = b.GetGenericTypeDefinition();
                    var args = b.GetGenericArguments();
                    if (definition == typeof(List<>))
                        return ListFormatter(type, args[0]);
                    if (definition == typeof(HashSet<>))
                        return typeof(HashSetFormatter<,,>).MakeGenericType(type, args[0], ElementFormatter(args[0]));
                    if (definition == typeof(Dictionary<,>))
                        return typeof(DictionaryFormatter<,,,,>).MakeGenericType(type, args[0], args[1],
                            ElementFormatter(args[0]), ElementFormatter(args[1]));
                }

                foreach (var i in type.GetInterfaces())
                {
                    if (!i.IsGenericType)
                        continue;
                    var definition = i.GetGenericTypeDefinition();
                    var args = i.GetGenericArguments();
                    if (definition == typeof(IDictionary<,>))
                        return typeof(DictionaryLikeFormatter<,,,,>).MakeGenericType(type, args[0], args[1],
                            ElementFormatter(args[0]), ElementFormatter(args[1]));
                }

                foreach (var i in type.GetInterfaces())
                {
                    if (!i.IsGenericType)
                        continue;
                    var definition = i.GetGenericTypeDefinition();
                    var args = i.GetGenericArguments();
                    if (definition == typeof(System.Collections.Concurrent.IProducerConsumerCollection<>))
                        return typeof(ProducerConsumerFormatter<,,>).MakeGenericType(type, args[0],
                            ElementFormatter(args[0]));
                    if (definition == typeof(ICollection<>))
                        return typeof(CollectionFormatter<,,>).MakeGenericType(type, args[0], ElementFormatter(args[0]));
                }
            }

            return null;
        }

        private static Type ListFormatter(Type listType, Type element)
        {
            if (IsRawUnmanaged(element))
                return typeof(UnmanagedListFormatter<,>).MakeGenericType(listType, element);
            if (Nullable.GetUnderlyingType(element) is { } underlying && IsRawUnmanaged(underlying))
                return typeof(NullableListFormatter<,>).MakeGenericType(listType, underlying);
            return typeof(ListFormatter<,,>).MakeGenericType(listType, element, ElementFormatter(element));
        }

        private static Type BuildGeneric(Type type, Type definition, Type[] args)
        {
            var generic = NinoRegistry.FindGeneric(definition);
            if (generic != null)
                return generic.MakeGenericType(args);

            if (definition == typeof(Nullable<>))
                return typeof(NullableFormatter<,>).MakeGenericType(args[0], ElementFormatter(args[0]));
            if (definition == typeof(List<>))
                return ListFormatter(type, args[0]);
            if (definition == typeof(Dictionary<,>))
                return typeof(DictionaryFormatter<,,,>).MakeGenericType(args[0], args[1],
                    ElementFormatter(args[0]), ElementFormatter(args[1]));
            if (definition == typeof(HashSet<>))
                return typeof(HashSetFormatter<,,>).MakeGenericType(type, args[0], ElementFormatter(args[0]));
            if (definition == typeof(Queue<>))
                return typeof(QueueFormatter<,>).MakeGenericType(args[0], ElementFormatter(args[0]));
            if (definition == typeof(Stack<>))
                return typeof(StackFormatter<,>).MakeGenericType(args[0], ElementFormatter(args[0]));
            if (definition == typeof(KeyValuePair<,>))
                return typeof(KeyValuePairFormatter<,,,>).MakeGenericType(args[0], args[1],
                    ElementFormatter(args[0]), ElementFormatter(args[1]));
            if (definition == typeof(ArraySegment<>))
                return typeof(ArraySegmentFormatter<,>).MakeGenericType(args[0], ElementFormatter(args[0]));

            if (InterfaceCollections.TryGetValue(definition, out var concrete))
                return typeof(InterfaceCollectionFormatter<,,,>).MakeGenericType(type,
                    concrete.MakeGenericType(args[0]), args[0], ElementFormatter(args[0]));

            if (definition == typeof(IDictionary<,>) || definition == typeof(IReadOnlyDictionary<,>) ||
                definition == typeof(ReadOnlyDictionary<,>))
                return typeof(InterfaceDictionaryFormatter<,,,,>).MakeGenericType(type, args[0], args[1],
                    ElementFormatter(args[0]), ElementFormatter(args[1]));

            int tuple = Array.IndexOf(ValueTupleDefinitions, definition);
            if (tuple >= 0)
                return TupleFormatter(ValueTupleFormatters[tuple], args);
            tuple = Array.IndexOf(TupleDefinitions, definition);
            if (tuple >= 0)
                return TupleFormatter(TupleFormatters[tuple], args);

            return null;
        }

        private static Type TupleFormatter(Type formatterDefinition, Type[] args)
        {
            var typeArgs = new Type[args.Length * 2];
            for (int i = 0; i < args.Length; i++)
            {
                typeArgs[i] = args[i];
                typeArgs[args.Length + i] = ElementFormatter(args[i]);
            }

            return formatterDefinition.MakeGenericType(typeArgs);
        }
    }
}
