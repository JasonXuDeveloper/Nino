using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Nino.Core
{
    /// <summary>
    /// Writes and reads one registered type. Also the registry entry: the registry stores handler references, so
    /// a reader always sees a complete (type, id, code) triple. One handler serves every base type of its type.
    /// </summary>
    public abstract class NinoSubtypeHandler
    {
        public readonly Type Type;
        public readonly int Id;

        protected NinoSubtypeHandler(Type type, int id)
        {
            Type = type;
            Id = id;
        }

        /// <summary>Writes the type id and the body of <paramref name="value"/> (an instance of exactly <see cref="Type"/>).</summary>
        public abstract void Serialize(object value, ref Writer writer);

        /// <summary>
        /// Reads the body (the type id has been consumed) into <paramref name="value"/>, which is null or an
        /// instance of exactly <see cref="Type"/> to reuse.
        /// </summary>
        public abstract void Deserialize(ref object value, ref Reader reader);
    }

    internal sealed class SubtypeHandler<TSub, TFormatter> : NinoSubtypeHandler
        where TFormatter : struct, INinoBodyFormatter<TSub>
    {
        public SubtypeHandler(int id) : base(typeof(TSub), id)
        {
        }

        public override void Serialize(object value, ref Writer writer)
        {
            writer.UnsafeWrite(Id);
            default(TFormatter).WriteBody(As(ref value), ref writer);
        }

        public override void Deserialize(ref object value, ref Reader reader)
        {
            TSub sub = value == null ? default : As(ref value);
            default(TFormatter).ReadBody(ref sub, ref reader);
            value = sub;
        }

        // The caller guarantees the exact runtime type, so reference types skip the cast check.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static TSub As(ref object value) =>
            typeof(TSub).IsValueType ? (TSub)value : Unsafe.As<object, TSub>(ref value);
    }

    /// <summary>Registration of the types that can be written polymorphically.</summary>
    public static class NinoPolymorphic
    {
        /// <summary>
        /// Registers <typeparamref name="TSub"/> with its generated formatter, once. Every base type finds it
        /// on first use, so a type is not registered once per base type.
        /// </summary>
        public static void Register<TSub, TFormatter>(int id) where TFormatter : struct, INinoBodyFormatter<TSub>
        {
            NinoPolymorphic<object>.Register(new SubtypeHandler<TSub, TFormatter>(id));
        }
    }

    /// <summary>
    /// Registry of the subtypes of <typeparamref name="TBase"/> (a non-sealed class, an interface, or
    /// <see cref="object"/>). Generated code registers every <c>[NinoType]</c> once, in the
    /// <see cref="object"/> registry; the registry of another base copies a subtype from there the first time
    /// it is looked up (after checking that it derives from the base).
    /// <para>
    /// Lookups are lock free. The tables are open-addressing arrays of 16-byte entries (key inline next to the
    /// handler, four entries per 64-byte cache line), kept at most half full, so a lookup usually touches a
    /// single cache line. A slot is written once: the handler first, then the key with release semantics, so a
    /// reader that sees the key also sees the handler. Growing publishes a new array with one reference write.
    /// </para>
    /// </summary>
    public static class NinoPolymorphic<TBase>
    {
        private struct TypeEntry
        {
            public Type Key;
            public NinoSubtypeHandler Handler;
        }

        private struct IdEntry
        {
            public int Key; // 0 = empty (id 0 is reserved for null)
            public NinoSubtypeHandler Handler;
        }

        private static TypeEntry[] _byType = new TypeEntry[8];
        private static IdEntry[] _byId = new IdEntry[8];
        private static int _count;
        private static readonly object Lock = new object();

        /// <summary>Registers a handler. Idempotent for the same type and id.</summary>
        public static void Register(NinoSubtypeHandler handler)
        {
            if (handler.Id == 0)
                throw new ArgumentException("Type id 0 is reserved for null.");
            if (!typeof(TBase).IsAssignableFrom(handler.Type))
                throw new ArgumentException($"{handler.Type} does not derive from {typeof(TBase)}.");

            lock (Lock)
            {
                var existing = FindByType(_byType, handler.Type);
                if (existing != null)
                {
                    if (existing.Id != handler.Id)
                        throw new InvalidOperationException(
                            $"Nino: {handler.Type} is registered under {typeof(TBase)} with two different ids.");
                    return;
                }

                var clash = FindById(_byId, handler.Id);
                if (clash != null)
                    throw new InvalidOperationException(
                        $"Nino: type id collision between {clash.Type} and {handler.Type} (id {handler.Id}). " +
                        "Use [NinoFormerName] to give one of them a different id.");

                var byType = _byType;
                var byId = _byId;
                if ((_count + 1) * 2 > byType.Length)
                {
                    // Keep the load factor at or below 50%, so probes stay short.
                    byType = new TypeEntry[byType.Length * 2];
                    byId = new IdEntry[byType.Length];
                    foreach (var e in _byType)
                    {
                        if (e.Key != null)
                            Insert(byType, byId, e.Handler);
                    }

                    Insert(byType, byId, handler);
                    Volatile.Write(ref _byType, byType);
                    Volatile.Write(ref _byId, byId);
                }
                else
                {
                    Insert(byType, byId, handler);
                }

                _count++;
            }
        }

        private static void Insert(TypeEntry[] byType, IdEntry[] byId, NinoSubtypeHandler handler)
        {
            int mask = byType.Length - 1;
            int i = RuntimeHelpers.GetHashCode(handler.Type) & mask;
            while (byType[i].Key != null)
                i = (i + 1) & mask;
            byType[i].Handler = handler;
            Volatile.Write(ref byType[i].Key, handler.Type);

            i = handler.Id & mask;
            while (byId[i].Key != 0)
                i = (i + 1) & mask;
            byId[i].Handler = handler;
            Volatile.Write(ref byId[i].Key, handler.Id);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static NinoSubtypeHandler FindByType(TypeEntry[] table, Type type)
        {
            int mask = table.Length - 1;
            int i = RuntimeHelpers.GetHashCode(type) & mask;
            while (true)
            {
                ref var e = ref table[i];
                var key = Volatile.Read(ref e.Key);
                if (ReferenceEquals(key, type))
                    return e.Handler;
                if (key == null)
                    return null;
                i = (i + 1) & mask;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static NinoSubtypeHandler FindById(IdEntry[] table, int id)
        {
            int mask = table.Length - 1;
            int i = id & mask;
            while (true)
            {
                ref var e = ref table[i];
                int key = Volatile.Read(ref e.Key);
                if (key == id)
                    return e.Handler;
                if (key == 0)
                    return null;
                i = (i + 1) & mask;
            }
        }

        /// <summary>Returns the handler for the runtime type <paramref name="type"/>, or null.</summary>
        public static NinoSubtypeHandler Find(Type type)
        {
            return FindByType(Volatile.Read(ref _byType), type) ?? FindSlow(type);
        }

        /// <summary>Returns the handler for type id <paramref name="id"/>, or null.</summary>
        public static NinoSubtypeHandler Find(int id)
        {
            return FindById(Volatile.Read(ref _byId), id) ?? FindSlow(id);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static NinoSubtypeHandler FindSlow(Type type)
        {
            if (typeof(TBase) != typeof(object))
                return Adopt(NinoPolymorphic<object>.Find(type));

            // Registration is lazy (and the subtype's assembly may not have run its registry's static constructor
            // yet where there is no module initializer support).
            NinoRegistry.RegisterType(type);
            return FindByType(Volatile.Read(ref _byType), type);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static NinoSubtypeHandler FindSlow(int id)
        {
            if (typeof(TBase) != typeof(object))
                return Adopt(NinoPolymorphic<object>.Find(id));

            // Registries already loaded first; scanning every loaded assembly is the expensive last resort.
            NinoRegistry.RegisterId(id);
            var handler = FindById(Volatile.Read(ref _byId), id);
            if (handler != null)
                return handler;
            NinoRegistry.EnsureAllInitialized();
            NinoRegistry.RegisterId(id);
            return FindById(Volatile.Read(ref _byId), id);
        }

        /// <summary>Copies a handler from the global registry into this one if its type derives from TBase.</summary>
        private static NinoSubtypeHandler Adopt(NinoSubtypeHandler handler)
        {
            if (handler == null || !typeof(TBase).IsAssignableFrom(handler.Type))
                return null;
            Register(handler);
            return handler;
        }

        /// <summary>Writes a non-null value of a registered subtype: type id, then body.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Serialize(TBase value, ref Writer writer)
        {
            var type = value.GetType();
            var handler = Find(type);
            if (handler == null)
                NinoThrow.UnregisteredType(typeof(TBase), type);
            handler.Serialize(value, ref writer);
        }

        /// <summary>
        /// Reads the body of the type with id <paramref name="id"/> (already consumed). Id 0 means null: the
        /// current value is recycled.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Deserialize(int id, ref TBase value, ref Reader reader)
        {
            if (id == 0)
            {
                NinoRecycler.Discard(ref value);
                return;
            }

            var handler = Find(id);
            if (handler == null)
                NinoThrow.UnknownTypeId(typeof(TBase), id);

            TBase old = value;
            bool reuse = old != null && old.GetType() == handler.Type;
            object result = reuse ? old : null;
            handler.Deserialize(ref result, ref reader);
            // The handler's type derives from TBase (checked when it was registered here).
            value = typeof(TBase).IsValueType ? (TBase)result : Unsafe.As<object, TBase>(ref result);
            // ReadBody recycles an instance it was given and had to re-create; an instance of another type was
            // never passed in, so it is recycled here.
            if (old != null && !reuse)
                NinoRecycler<TBase>.Recycle(old);
        }
    }
}
