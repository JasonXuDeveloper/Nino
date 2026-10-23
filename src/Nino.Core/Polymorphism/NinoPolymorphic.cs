using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Nino.Core
{
    /// <summary>
    /// Writes and reads one registered type. Also the registry entry: the registry stores handler references, so
    /// a reader always sees a complete (type, id, code) triple. One handler serves every base type of its type.
    /// Only Nino.Core creates handlers (from generated registrations): a read trusts that a handler's type is what it
    /// reads.
    /// </summary>
    public abstract class NinoSubtypeHandler
    {
        /// <summary>The exact runtime type this handler writes and reads.</summary>
        public Type Type { get; }

        /// <summary>The type id written before the body.</summary>
        public int Id { get; }

        private protected NinoSubtypeHandler(Type type, int id)
        {
            Type = type;
            Id = id;
        }

        /// <summary>
        /// Writes the type id and the body of <paramref name="value"/> (an instance of exactly <see cref="Type"/>).
        /// For a type marked <c>trackReferences</c>, its reference header comes between the two, since the declared
        /// type (<c>object</c>, or a base class or interface that is not tracked) wrote none.
        /// </summary>
        public abstract void Serialize(object value, ref Writer writer);

        /// <summary>
        /// Reads what <see cref="Serialize"/> wrote after the type id (which has been consumed) into
        /// <paramref name="value"/>, which is null or an instance of exactly <see cref="Type"/> to reuse.
        /// </summary>
        public abstract void Deserialize(ref object value, ref Reader reader);

        /// <summary>
        /// Writes the type id and the body without a reference header: called through a tracked declared type,
        /// whose formatter wrote the header before the type id.
        /// </summary>
        internal virtual void SerializeReferenced(object value, ref Writer writer) => Serialize(value, ref writer);

        /// <summary>Reads what <see cref="SerializeReferenced"/> wrote after the type id.</summary>
        internal virtual void DeserializeReferenced(ref object value, ref Reader reader) =>
            Deserialize(ref value, ref reader);
    }

    internal sealed class SubtypeHandler<TSub, TFormatter> : NinoSubtypeHandler
        where TFormatter : struct, INinoBodyFormatter<TSub>
    {
        public SubtypeHandler(int id) : base(typeof(TSub), id)
        {
        }

        // For a class, NinoObject.WriteBody/ReadBody spelled out (as in NinoObject.WriteExact): this is shared code
        // (TSub is a reference type), where the inlined generic method would look up its generic context on every
        // call, and ReadBody's copy of the reader for a deep body (in memory here anyway) would be zeroed in the
        // prolog. Only the deep path goes through ClassBody, out of line.
        public override void Serialize(object value, ref Writer writer)
        {
            writer.UnsafeWrite(Id);
            // Only class bodies count toward the depth (NinoDepth).
            if (typeof(TSub).IsValueType)
            {
                default(TFormatter).WriteBody(As(ref value), ref writer);
                return;
            }

            int depth = writer.Depth;
            if (depth >= NinoDepth.Inline)
            {
                WriteDeep(value, ref writer);
                return;
            }

            if ((default(TFormatter).Traits & NinoBody.Nests) == 0)
            {
                default(TFormatter).WriteBody(As(ref value), ref writer);
                return;
            }

            writer.Depth = depth + 1;
            default(TFormatter).WriteBody(As(ref value), ref writer);
            writer.ExitBody(depth);
        }

        public override void Deserialize(ref object value, ref Reader reader)
        {
            if (!typeof(TSub).IsValueType)
            {
                int depth = reader.Depth;
                if (depth >= NinoDepth.Inline)
                {
                    ReadDeep(ref value, ref reader);
                    return;
                }

                // value is null or exactly a TSub, and the body stores a TSub: the slot is read as a TSub slot.
                if ((default(TFormatter).Traits & NinoBody.Nests) == 0)
                {
                    default(TFormatter).ReadBody(ref Unsafe.As<object, TSub>(ref value), ref reader);
                    return;
                }

                reader.Depth = depth + 1;
                default(TFormatter).ReadBody(ref Unsafe.As<object, TSub>(ref value), ref reader);
                reader.ExitBody(depth);
                return;
            }

            TSub sub = value == null ? default : As(ref value);
            default(TFormatter).ReadBody(ref sub, ref reader);
            value = sub;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void WriteDeep(object value, ref Writer writer) =>
            NinoObject.WriteBody<object, ClassBody>(value, ref writer);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ReadDeep(ref object value, ref Reader reader) =>
            NinoObject.ReadBody<object, ClassBody>(ref value, ref reader);

        /// <summary><typeparamref name="TFormatter"/> over <c>object</c>, for a class <typeparamref name="TSub"/>.</summary>
        private struct ClassBody : INinoBodyFormatter<object>
        {
            public int TypeId => default(TFormatter).TypeId;

            public int Traits => default(TFormatter).Traits;

            public object Create() => default(TFormatter).Create();

            public void WriteBody(object value, ref Writer writer) =>
                default(TFormatter).WriteBody(Unsafe.As<object, TSub>(ref value), ref writer);

            public void ReadBody(ref object value, ref Reader reader)
            {
                TSub sub = value == null ? default : Unsafe.As<object, TSub>(ref value);
                default(TFormatter).ReadBody(ref sub, ref reader);
                value = sub;
            }

            public void Serialize(object value, ref Writer writer) => WriteBody(value, ref writer);

            public void Deserialize(ref object value, ref Reader reader) => ReadBody(ref value, ref reader);
        }

        // The caller guarantees the exact runtime type, so reference types skip the cast check.
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static TSub As(ref object value) =>
            typeof(TSub).IsValueType ? (TSub)value : Unsafe.As<object, TSub>(ref value);
    }

    /// <summary>
    /// Handler of a class marked <c>trackReferences</c> (or deriving from one). Through a declared type that writes
    /// no reference header (<c>object</c>, a base class or interface that is not tracked) it writes
    /// <c>type id | reference header | body</c>, so the instance keeps its identity whatever slot holds it; a back
    /// reference is the type id and the index. Through a tracked declared type the header comes first
    /// (<c>reference header | type id | body</c>, <see cref="SerializeReferenced"/>). Untracked types keep
    /// <see cref="SubtypeHandler{TSub,TFormatter}"/>, so their dispatch does not test for tracking.
    /// </summary>
    internal sealed class TrackedSubtypeHandler<TSub, TFormatter> : NinoSubtypeHandler
        where TSub : class
        where TFormatter : struct, INinoBodyFormatter<TSub>
    {
        public TrackedSubtypeHandler(int id) : base(typeof(TSub), id)
        {
        }

        public override void Serialize(object value, ref Writer writer)
        {
            writer.UnsafeWrite(Id);
            if (writer.WriteReference(value))
                NinoObject.WriteBody<TSub, TFormatter>(Unsafe.As<TSub>(value), ref writer);
        }

        public override void Deserialize(ref object value, ref Reader reader)
        {
            var sub = Unsafe.As<TSub>(value);
            // A back reference must be a TSub (ReadReference checks it); the old instance is not recycled, since
            // it may be referenced elsewhere in the graph.
            if (reader.ReadReference(ref sub, out int index))
            {
                NinoObject.ReadBody<TSub, TFormatter>(ref sub, ref reader);
                reader.EndReference(index, sub);
            }

            value = sub;
        }

        internal override void SerializeReferenced(object value, ref Writer writer)
        {
            writer.UnsafeWrite(Id);
            NinoObject.WriteBody<TSub, TFormatter>(Unsafe.As<TSub>(value), ref writer);
        }

        internal override void DeserializeReferenced(ref object value, ref Reader reader)
        {
            var sub = Unsafe.As<TSub>(value);
            NinoObject.ReadBody<TSub, TFormatter>(ref sub, ref reader);
            value = sub;
        }
    }

    /// <summary>Registration of the types that can be written polymorphically.</summary>
    public static class NinoPolymorphic
    {
        /// <summary>
        /// Registers <typeparamref name="TSub"/> with its generated formatter, once. Every base type finds it
        /// on first use, so a type is not registered once per base type.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public static void Register<TSub, TFormatter>(int id) where TFormatter : struct, INinoBodyFormatter<TSub>
        {
            NinoPolymorphic<object>.Register(new SubtypeHandler<TSub, TFormatter>(id));
        }

        /// <summary>
        /// Registers a class marked <c>trackReferences</c> (or deriving from one): written through a declared type
        /// that is not tracked, its reference header follows the type id.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public static void RegisterTracked<TSub, TFormatter>(int id)
            where TSub : class
            where TFormatter : struct, INinoBodyFormatter<TSub>
        {
            NinoPolymorphic<object>.Register(new TrackedSubtypeHandler<TSub, TFormatter>(id));
        }

        /// <summary>Registers <typeparamref name="T"/> for polymorphic use; see <see cref="Register(Type)"/>.</summary>
        public static bool Register<T>() => Register(typeof(T));

        /// <summary>
        /// Registers a <c>[NinoType]</c>, typically a closed generic such as <c>Box&lt;int&gt;</c>, so that it can be
        /// written through a base type and read back from its type id.
        /// <para>
        /// Generated code registers the closed generic <c>[NinoType]</c>s its compilation names (member types, base
        /// types, type arguments of <c>NinoSerializer</c>/<c>NinoDeserializer</c> calls, and the closed types of
        /// arguments of those), and writing an instance registers its type. Call this at startup for an
        /// instantiation that a process reads but never names nor writes (for example one only created by another
        /// assembly or through reflection). Generic types are closed with <c>MakeGenericType</c>, which needs dynamic
        /// code: without it (NativeAOT), only the generated registrations are found.
        /// </para>
        /// Returns false when the type cannot be written polymorphically (abstract, open, or not a <c>[NinoType]</c>).
        /// </summary>
        public static bool Register(Type type)
        {
            if (type == null)
                throw new ArgumentNullException(nameof(type));
            if (type.IsAbstract || type.IsInterface || type.ContainsGenericParameters)
                return false;
            if (NinoPolymorphic<object>.FindRegistered(type) != null)
                return true;

            // Non-generic [NinoType]s, and the closed generics the generator saw, are registered by generated code.
            NinoRegistry.RegisterType(type);
            if (NinoPolymorphic<object>.FindRegistered(type) != null)
                return true;
            if (!type.IsGenericType)
                return false;
#if NET8_0_OR_GREATER
            if (!RuntimeFeature.IsDynamicCodeSupported)
                return false;
#endif
            return RegisterGeneric(type);
        }

        /// <summary>Registers a closed generic <c>[NinoType]</c> from its formatter definition.</summary>
        [RequiresDynamicCode(NinoFormatterResolver.DynamicCodeMessage)]
        // IL2076; IL2075 from the .NET 8 analyzer, which reports the same thing on MakeGenericType itself.
        [UnconditionalSuppressMessage("Trimming", "IL2076",
            Justification = NinoFormatterResolver.ValueTypeConstraintJustification)]
        [UnconditionalSuppressMessage("Trimming", "IL2075",
            Justification = NinoFormatterResolver.ValueTypeConstraintJustification)]
        private static bool RegisterGeneric(Type type)
        {
            if (!NinoFormatterResolver.IsNinoType(type))
                return false;

            // The formatter definition of a generic [NinoType] is an [assembly: NinoFormatter] registration.
            var formatter = NinoFormatterResolver.GetFormatterType(type);
            if (formatter == null || !typeof(INinoBodyFormatter<>).MakeGenericType(type).IsAssignableFrom(formatter))
                return false;
            var handler = !type.IsValueType && IsTracked(type)
                ? typeof(TrackedSubtypeHandler<,>)
                : typeof(SubtypeHandler<,>);
            NinoPolymorphic<object>.Register((NinoSubtypeHandler)Activator.CreateInstance(
                handler.MakeGenericType(type, formatter), NinoTypeId.Of(type)));
            return true;
        }

        /// <summary>
        /// Registers the closed generic <c>[NinoType]</c> with type id <paramref name="id"/> among the types whose
        /// formatter this process has resolved; false if there is none. The last resort of a read by type id.
        /// </summary>
        internal static bool RegisterResolvedGeneric(int id)
        {
            foreach (var type in NinoFormatterCache.RegisteredTypes)
            {
                if (type.IsGenericType && !type.IsAbstract && !type.IsInterface &&
                    NinoTypeId.Of(type) == id && NinoFormatterResolver.IsNinoType(type))
                    return Register(type);
            }

            return false;
        }

        /// <summary>Whether the type, a base class or an interface is marked <c>trackReferences</c> (as in the generator).</summary>
        [UnconditionalSuppressMessage("Trimming", "IL2070", Justification = NinoFormatterResolver.InterfacesJustification)]
        private static bool IsTracked(Type type)
        {
            for (var t = type; t != null; t = t.BaseType)
            {
                if (TrackReferences(t))
                    return true;
            }

            foreach (var i in type.GetInterfaces())
            {
                if (TrackReferences(i))
                    return true;
            }

            return false;
        }

        private static bool TrackReferences(Type type) =>
            System.Attribute.GetCustomAttribute(type, typeof(NinoTypeAttribute), false) is NinoTypeAttribute
            {
                TrackReferences: true
            };
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
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static class NinoPolymorphic<TBase> where TBase : class
    {
        private struct TypeEntry
        {
            public Type Key;
            public NinoSubtypeHandler Handler;
        }

        private struct IdEntry
        {
            public int Key; // NinoWire.NullTypeId = empty (that id is never registered)
            public NinoSubtypeHandler Handler;
        }

        private static TypeEntry[] _byType = new TypeEntry[8];
        private static IdEntry[] _byId = new IdEntry[8];
        private static int _count;
        private static bool _tracked;
        private static readonly object Lock = new object();

        /// <summary>Registers a handler. Idempotent for the same type and id.</summary>
        internal static void Register(NinoSubtypeHandler handler)
        {
            if (handler.Id == NinoWire.NullTypeId)
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

                if (!_tracked)
                {
                    // A lambda rather than a method group: older compilers (Unity's) allocate a delegate per use. The
                    // hook goes with TBase's assembly.
                    NinoUnload.OnRemove(assembly => Remove(assembly), typeof(TBase));
                    _tracked = true;
                }

                if ((_count + 1) * 2 > _byType.Length)
                {
                    // Keep the load factor at or below 50%, so probes stay short.
                    var handlers = Handlers(null);
                    handlers.Add(handler);
                    Rebuild(_byType.Length * 2, handlers);
                }
                else
                {
                    Insert(_byType, _byId, handler);
                }

                _count++;
            }
        }

        /// <summary>
        /// Drops the handlers of types that mention <paramref name="assembly"/>: the tables are rebuilt without them
        /// and published like a growth, so lookups running meanwhile see the old or the new tables.
        /// </summary>
        private static void Remove(System.Reflection.Assembly assembly)
        {
            lock (Lock)
            {
                var kept = Handlers(assembly);
                if (kept.Count == _count)
                    return;
                Rebuild(_byType.Length, kept);
                _count = kept.Count;
            }
        }

        /// <summary>
        /// The registered handlers, except those of types that mention <paramref name="removed"/> (null: none).
        /// Called under <see cref="Lock"/>.
        /// </summary>
        private static List<NinoSubtypeHandler> Handlers(System.Reflection.Assembly removed)
        {
            var handlers = new List<NinoSubtypeHandler>(_count + 1);
            foreach (var e in _byType)
            {
                if (e.Key != null && (removed == null || !NinoUnload.Mentions(e.Key, removed)))
                    handlers.Add(e.Handler);
            }

            return handlers;
        }

        /// <summary>
        /// Publishes new tables of <paramref name="capacity"/> entries holding <paramref name="handlers"/>, one
        /// reference write each: lookups running meanwhile see the old or the new tables. Called under
        /// <see cref="Lock"/>.
        /// </summary>
        private static void Rebuild(int capacity, List<NinoSubtypeHandler> handlers)
        {
            var byType = new TypeEntry[capacity];
            var byId = new IdEntry[capacity];
            foreach (var handler in handlers)
                Insert(byType, byId, handler);
            Volatile.Write(ref _byType, byType);
            Volatile.Write(ref _byId, byId);
        }

        private static void Insert(TypeEntry[] byType, IdEntry[] byId, NinoSubtypeHandler handler)
        {
            int mask = byType.Length - 1;
            int i = TypeHash(handler.Type) & mask;
            while (byType[i].Key != null)
                i = (i + 1) & mask;
            byType[i].Handler = handler;
            Volatile.Write(ref byType[i].Key, handler.Type);

            i = handler.Id & mask;
            while (byId[i].Key != NinoWire.NullTypeId)
                i = (i + 1) & mask;
            byId[i].Handler = handler;
            Volatile.Write(ref byId[i].Key, handler.Id);
        }

        /// <summary>
        /// Hash of a runtime type: its type handle (the method table, vtable or class pointer, which every runtime
        /// keeps unique per type), a couple of loads rather than a call into the runtime for the object hash.
        /// Mixed so the low bits (aligned pointers) spread.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int TypeHash(Type type)
        {
            ulong h = (ulong)type.TypeHandle.Value.ToInt64();
            return (int)((h * 0x9E3779B97F4A7C15UL) >> 32);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static NinoSubtypeHandler FindByType(TypeEntry[] table, Type type)
        {
            int mask = table.Length - 1;
            int i = TypeHash(type) & mask;
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
                if (key == NinoWire.NullTypeId)
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
            // yet where there is no module initializer support). A closed generic [NinoType] the generator did not
            // see is registered from its formatter definition.
            NinoPolymorphic.Register(type);
            return FindByType(Volatile.Read(ref _byType), type);
        }

        /// <summary>Returns the handler registered for <paramref name="type"/>, or null, without registering anything.</summary>
        internal static NinoSubtypeHandler FindRegistered(Type type) => FindByType(Volatile.Read(ref _byType), type);

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static NinoSubtypeHandler FindSlow(int id)
        {
            if (typeof(TBase) != typeof(object))
                return Adopt(NinoPolymorphic<object>.Find(id));

            // An id searched for in vain before, with nothing registered or loaded since, is not searched again.
            var before = NinoUnknownIds.Now();
            if (NinoUnknownIds.Contains(id, before))
                return null;

            // Registries already loaded first; scanning every loaded assembly is the expensive last resort.
            NinoRegistry.RegisterId(id);
            var handler = FindById(Volatile.Read(ref _byId), id);
            if (handler != null)
                return handler;
            NinoRegistry.EnsureAllInitialized();
            NinoRegistry.RegisterId(id);
            handler = FindById(Volatile.Read(ref _byId), id);
            if (handler != null)
                return handler;
            // A closed generic [NinoType] that no generated registry names, but whose formatter this process has
            // resolved (it was read or written as itself, or as a member or element).
            NinoPolymorphic.RegisterResolvedGeneric(id);
            handler = FindById(Volatile.Read(ref _byId), id);
            if (handler == null)
                NinoUnknownIds.Add(id, before);
            return handler;
        }

        /// <summary>Copies a handler from the global registry into this one if its type derives from TBase.</summary>
        private static NinoSubtypeHandler Adopt(NinoSubtypeHandler handler)
        {
            if (handler == null || !typeof(TBase).IsAssignableFrom(handler.Type))
                return null;
            Register(handler);
            return handler;
        }

        /// <summary>
        /// Writes a non-null value of a registered subtype: type id, then body (with the reference header of a tracked
        /// type between them).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Serialize(TBase value, ref Writer writer) => FindForWrite(value).Serialize(value, ref writer);

        /// <summary>
        /// Writes a non-null value whose reference header has been written (by the formatter of a tracked
        /// <typeparamref name="TBase"/>): type id, then body.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SerializeReferenced(TBase value, ref Writer writer) =>
            FindForWrite(value).SerializeReferenced(value, ref writer);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static NinoSubtypeHandler FindForWrite(TBase value)
        {
            var type = value.GetType();
            var handler = Find(type);
            if (handler == null)
                NinoThrow.UnregisteredType(typeof(TBase), type);
            return handler;
        }

        /// <summary>
        /// <see cref="Deserialize"/> out of line, for the generated subtype dispatch: inlined, its object local (whose
        /// address reaches the handler) would be zeroed in the dispatch's prolog on every call and keep the dispatch
        /// from ending each case with a tail call.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static void DeserializeOther(int id, ref TBase value, ref Reader reader) =>
            Deserialize(id, ref value, ref reader);

        /// <summary>
        /// Reads the body of the type with id <paramref name="id"/> (already consumed). Id 0 means null: the
        /// current value is recycled.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Deserialize(int id, ref TBase value, ref Reader reader) =>
            Read(id, ref value, ref reader, referenced: false);

        /// <summary>
        /// Reads what <see cref="SerializeReferenced"/> wrote after the reference header, given the type id (already
        /// consumed).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void DeserializeReferenced(int id, ref TBase value, ref Reader reader) =>
            Read(id, ref value, ref reader, referenced: true);

        /// <summary>
        /// <see cref="Deserialize"/>, or <see cref="DeserializeReferenced"/> when <paramref name="referenced"/> (a
        /// constant in both).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void Read(int id, ref TBase value, ref Reader reader, bool referenced)
        {
            if (id == NinoWire.NullTypeId)
            {
                NinoRecycler.Discard(ref value);
                return;
            }

            var handler = Find(id);
            if (handler == null)
                NinoThrow.UnknownTypeId(typeof(TBase), id);

            // The handler owns an instance of its exact type (it reuses it, or recycles it when it has to re-create
            // the type); an instance of another type is recycled here.
            NinoRecycler.ReuseExact(ref value, handler.Type);
            object result = value;
            if (referenced)
                handler.DeserializeReferenced(ref result, ref reader);
            else
                handler.Deserialize(ref result, ref reader);
            // The handler's type derives from TBase (checked when it was registered here), and so does a back reference.
            value = Unsafe.As<TBase>(result);
        }
    }
}
