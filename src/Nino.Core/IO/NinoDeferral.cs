using System;
using System.Runtime.CompilerServices;

namespace Nino.Core
{
    /// <summary>
    /// Bounds on how deep class bodies nest on the stack. A body that would open below <see cref="Inline"/> open
    /// bodies is deferred: its slot gets the instance now and its members are written (and read) later, from a
    /// shallow point, so a long linked list or a deep tree costs a bounded stack however deep it is, and data
    /// cannot exhaust the reader's stack.
    /// <para>
    /// Where a deferred body goes depends on what encloses it. Outside any length-prefixed region, deferred bodies
    /// follow the top-level object they belong to, once the bodies open around it have all closed. Inside a region
    /// (a version-tolerant body, a keyed body or member), they go into a section at the end of the outermost
    /// region, keyed by where the deferral happened (relative to the region's start): a reader that skips part of
    /// the region (members it does not know) skips the sections' entries for it too. A region holding a section
    /// has <see cref="SectionFlag"/> set in its length; the section is followed by its offset in the region.
    /// </para>
    /// </summary>
    internal static class NinoDepth
    {
        /// <summary>Open class bodies after which the next one is deferred.</summary>
        public const int Inline = 128;

        /// <summary>
        /// Open class bodies after which reading or writing fails, for types that cannot be deferred: types built by
        /// their constructor (a slot cannot hold them before their members are read) and types compared by value
        /// (hashed or sorted collections would see them before their members are read).
        /// </summary>
        public const int Max = 512;

        /// <summary>Set in the length of a region followed by a section of deferred bodies.</summary>
        public const int SectionFlag = int.MinValue;
    }

    /// <summary>Writes and reads the body of a deferred object; one instance per (type, formatter).</summary>
    internal abstract class NinoDeferredBody
    {
        public abstract void Write(object value, ref Writer writer);

        public abstract void Read(object value, ref Reader reader);
    }

    internal sealed class NinoDeferredBody<T, TFormatter> : NinoDeferredBody
        where TFormatter : struct, INinoBodyFormatter<T>
    {
        public static readonly NinoDeferredBody Instance = new NinoDeferredBody<T, TFormatter>();

        public override void Write(object value, ref Writer writer) =>
            default(TFormatter).WriteBody((T)value, ref writer);

        public override void Read(object value, ref Reader reader)
        {
            // The instance exists: the body fills it in place.
            var v = (T)value;
            default(TFormatter).ReadBody(ref v, ref reader);
        }
    }

    internal struct NinoDeferredItem
    {
        public object Value;
        public NinoDeferredBody Body;

        /// <summary>Absolute position of the deferral: keys the entry when the item is in a region's section.</summary>
        public int Position;

        // Writer only, to find cycles (see Writer.Defer).
        public object Anchor;
        public int Generation;
    }

    internal struct NinoDeferredQueue
    {
        public NinoDeferredItem[] Items;
        public int Head;
        public int Count;

        public bool IsEmpty
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Head == Count;
        }

        public void Add(in NinoDeferredItem item)
        {
            if (Items == null)
                Items = new NinoDeferredItem[16];
            else if (Count == Items.Length)
                Array.Resize(ref Items, Count * 2);
            Items[Count++] = item;
        }

        public void Clear()
        {
            if (Count != 0)
                Array.Clear(Items, 0, Count);
            Head = Count = 0;
        }
    }

    /// <summary>
    /// The deferred bodies of one serialization or deserialization call. Rented on the first deferral (one cached
    /// instance per thread), so calls that never nest deep touch nothing.
    /// </summary>
    internal sealed class NinoDeferrals
    {
        [ThreadStatic] private static NinoDeferrals t_cached;

        /// <summary>Bodies deferred outside any region.</summary>
        public NinoDeferredQueue Top;

        /// <summary>Bodies deferred inside the outermost open region.</summary>
        public NinoDeferredQueue Region;

        /// <summary>Whether <see cref="Top"/> is being written or read.</summary>
        public bool TopActive;

        // Writer only: the deferred object whose body is being written, for cycle detection.
        public object Current;
        public object CurrentAnchor;
        public int CurrentGeneration;

        public static NinoDeferrals Rent() => NinoThreadCache.Take(ref t_cached) ?? new NinoDeferrals();

        public static void Return(NinoDeferrals deferrals)
        {
            if (deferrals.Top.Items?.Length > NinoThreadCache.MaxRetainedTableCapacity ||
                deferrals.Region.Items?.Length > NinoThreadCache.MaxRetainedTableCapacity)
                return;
            deferrals.Top.Clear();
            deferrals.Region.Clear();
            deferrals.TopActive = false;
            deferrals.Current = deferrals.CurrentAnchor = null;
            deferrals.CurrentGeneration = 0;
            t_cached = deferrals;
        }
    }

    /// <summary>
    /// What a <see cref="Reader"/> and the readers made from it share beyond their position: the back references
    /// and the deferred bodies of one deserialization call. Rented on first use (one cached instance per thread),
    /// so calls that track nothing and never nest deep touch nothing.
    /// </summary>
    internal sealed class NinoReadContext
    {
        [ThreadStatic] private static NinoReadContext t_cached;

        public NinoReferenceReadTable Refs;
        public NinoDeferrals Deferrals;

        public static NinoReadContext Rent() => NinoThreadCache.Take(ref t_cached) ?? new NinoReadContext();

        public static void Return(NinoReadContext context)
        {
            if (context.Deferrals != null)
            {
                NinoDeferrals.Return(context.Deferrals);
                context.Deferrals = null;
            }

            if (context.Refs != null)
            {
                NinoReferenceReadTable.Return(context.Refs);
                context.Refs = null;
            }

            t_cached = context;
        }
    }

    /// <summary>
    /// What a <see cref="Writer"/> keeps beyond its buffer and position: the back references and the deferred
    /// bodies of one serialization call. Rented on first use (one cached instance per thread), so calls that track
    /// nothing and never nest deep touch nothing.
    /// </summary>
    internal sealed class NinoWriteContext
    {
        [ThreadStatic] private static NinoWriteContext t_cached;

        public NinoReferenceWriteTable Refs;
        public NinoDeferrals Deferrals;

        public static NinoWriteContext Rent() => NinoThreadCache.Take(ref t_cached) ?? new NinoWriteContext();

        public static void Return(NinoWriteContext context)
        {
            if (context.Deferrals != null)
            {
                NinoDeferrals.Return(context.Deferrals);
                context.Deferrals = null;
            }

            if (context.Refs != null)
            {
                NinoReferenceWriteTable.Return(context.Refs);
                context.Refs = null;
            }

            t_cached = context;
        }
    }
}
