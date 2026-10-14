using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Nino.Core
{
    /// <summary>
    /// Writes and reads one registered subtype of <typeparamref name="TBase"/>. Also the registry entry: the
    /// registry stores handler references, so a reader always sees a complete (type, id, code) triple.
    /// </summary>
    public abstract class NinoSubtypeHandler<TBase>
    {
        public readonly Type Type;
        public readonly int Id;

        protected NinoSubtypeHandler(Type type, int id)
        {
            Type = type;
            Id = id;
        }

        /// <summary>Writes the type id and the body of <paramref name="value"/>.</summary>
        public abstract void Serialize(TBase value, ref Writer writer);

        /// <summary>Reads the body (the type id has been consumed) into <paramref name="value"/>.</summary>
        public abstract void Deserialize(ref TBase value, ref Reader reader);
    }

    internal sealed class SubtypeHandler<TBase, TSub, TFormatter> : NinoSubtypeHandler<TBase>
        where TFormatter : struct, INinoBodyFormatter<TSub>
    {
        public SubtypeHandler(int id) : base(typeof(TSub), id)
        {
        }

        public override void Serialize(TBase value, ref Writer writer)
        {
            writer.UnsafeWrite(Id);
            default(TFormatter).WriteBody((TSub)(object)value, ref writer);
        }

        public override void Deserialize(ref TBase value, ref Reader reader)
        {
            TBase old = value;
            bool reuse = old != null && old.GetType() == typeof(TSub);
            TSub sub = reuse ? (TSub)(object)old : default;
            // ReadBody recycles the instance it was given if it has to re-create it; here we only recycle an
            // instance of another type that was never passed in.
            default(TFormatter).ReadBody(ref sub, ref reader);
            value = (TBase)(object)sub;
            if (old != null && !reuse)
                NinoRecycler<TBase>.Recycle(old);
        }
    }

    /// <summary>
    /// Registry of the subtypes of <typeparamref name="TBase"/> (a non-sealed class, an interface, or
    /// <see cref="object"/>). Generated code registers every <c>[NinoType]</c> under each of its <c>[NinoType]</c>
    /// base types and interfaces, and under <see cref="object"/>.
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
            public NinoSubtypeHandler<TBase> Handler;
        }

        private struct IdEntry
        {
            public int Key; // 0 = empty (id 0 is reserved for null)
            public NinoSubtypeHandler<TBase> Handler;
        }

        private static TypeEntry[] _byType = new TypeEntry[8];
        private static IdEntry[] _byId = new IdEntry[8];
        private static int _count;
        private static readonly object Lock = new object();

        /// <summary>Registers <typeparamref name="TSub"/> with its generated formatter. Idempotent.</summary>
        public static void Register<TSub, TFormatter>(int id) where TFormatter : struct, INinoBodyFormatter<TSub>
        {
            if (!typeof(TBase).IsAssignableFrom(typeof(TSub)))
                throw new ArgumentException($"{typeof(TSub)} does not derive from {typeof(TBase)}.");
            Register(new SubtypeHandler<TBase, TSub, TFormatter>(id));
        }

        /// <summary>Registers a handler. Idempotent for the same type and id.</summary>
        public static void Register(NinoSubtypeHandler<TBase> handler)
        {
            if (handler.Id == 0)
                throw new ArgumentException("Type id 0 is reserved for null.");

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

        private static void Insert(TypeEntry[] byType, IdEntry[] byId, NinoSubtypeHandler<TBase> handler)
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
        private static NinoSubtypeHandler<TBase> FindByType(TypeEntry[] table, Type type)
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
        private static NinoSubtypeHandler<TBase> FindById(IdEntry[] table, int id)
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
        public static NinoSubtypeHandler<TBase> Find(Type type)
        {
            return FindByType(Volatile.Read(ref _byType), type) ?? FindSlow(type);
        }

        /// <summary>Returns the handler for type id <paramref name="id"/>, or null.</summary>
        public static NinoSubtypeHandler<TBase> Find(int id)
        {
            return FindById(Volatile.Read(ref _byId), id) ?? FindSlow(id);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static NinoSubtypeHandler<TBase> FindSlow(Type type)
        {
            // The subtype's assembly may not have run its registration yet (no module initializer support).
            NinoRegistry.EnsureInitialized(type.Assembly);
            return FindByType(Volatile.Read(ref _byType), type);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static NinoSubtypeHandler<TBase> FindSlow(int id)
        {
            NinoRegistry.EnsureAllInitialized();
            return FindById(Volatile.Read(ref _byId), id);
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
            handler.Deserialize(ref value, ref reader);
        }
    }
}
