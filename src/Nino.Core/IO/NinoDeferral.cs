using System;
using System.Runtime.CompilerServices;
using Nino.Core.Generated;

namespace Nino.Core
{
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

        /// <summary>
        /// Queues <paramref name="item"/>: in <see cref="Top"/> outside any region (<paramref name="regions"/> 0),
        /// else in <see cref="Region"/>.
        /// </summary>
        public void Add(in NinoDeferredItem item, int regions)
        {
            if (regions == 0)
                Top.Add(in item);
            else
                Region.Add(in item);
        }

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
}
