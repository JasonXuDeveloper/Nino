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
    /// Lookups are lock free: the tables are arrays of immutable handler references, published with a single
    /// reference write. Each 64-byte cache line holds eight slots and the first probe almost always hits.
    /// </para>
    /// </summary>
    public static class NinoPolymorphic<TBase>
    {
        private static NinoSubtypeHandler<TBase>[] _byType = new NinoSubtypeHandler<TBase>[8];
        private static NinoSubtypeHandler<TBase>[] _byId = new NinoSubtypeHandler<TBase>[8];
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
                    byType = new NinoSubtypeHandler<TBase>[byType.Length * 2];
                    byId = new NinoSubtypeHandler<TBase>[byType.Length];
                    foreach (var h in _byType)
                    {
                        if (h != null)
                            Insert(byType, byId, h);
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

        private static void Insert(NinoSubtypeHandler<TBase>[] byType, NinoSubtypeHandler<TBase>[] byId,
            NinoSubtypeHandler<TBase> handler)
        {
            int mask = byType.Length - 1;
            int i = RuntimeHelpers.GetHashCode(handler.Type) & mask;
            while (byType[i] != null)
                i = (i + 1) & mask;
            Volatile.Write(ref byType[i], handler);

            i = handler.Id & mask;
            while (byId[i] != null)
                i = (i + 1) & mask;
            Volatile.Write(ref byId[i], handler);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static NinoSubtypeHandler<TBase> FindByType(NinoSubtypeHandler<TBase>[] table, Type type)
        {
            int mask = table.Length - 1;
            int i = RuntimeHelpers.GetHashCode(type) & mask;
            while (true)
            {
                var h = Volatile.Read(ref table[i]);
                if (h == null || ReferenceEquals(h.Type, type))
                    return h;
                i = (i + 1) & mask;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static NinoSubtypeHandler<TBase> FindById(NinoSubtypeHandler<TBase>[] table, int id)
        {
            int mask = table.Length - 1;
            int i = id & mask;
            while (true)
            {
                var h = Volatile.Read(ref table[i]);
                if (h == null || h.Id == id)
                    return h;
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
