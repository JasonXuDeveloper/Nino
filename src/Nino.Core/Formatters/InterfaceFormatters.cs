using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using Nino.Core.Internal;

namespace Nino.Core
{
    /// <summary>
    /// List-like collection interfaces (<c>IEnumerable&lt;T&gt;</c>, <c>IList&lt;T&gt;</c>, <c>ICollection&lt;T&gt;</c>,
    /// <c>IReadOnlyList&lt;T&gt;</c>, <c>IReadOnlyCollection&lt;T&gt;</c>): count (-1 = null), then the elements.
    /// Read into a <c>List&lt;T&gt;</c> (an existing one is reused). <c>ReadOnlyCollection&lt;T&gt;</c> and
    /// <c>ReadOnlyObservableCollection&lt;T&gt;</c> are written the same way and read as a new wrapper around the
    /// elements read.
    /// </summary>
    public struct InterfaceListFormatter<TInterface, T, TFormatter> : INinoFormatter<TInterface>
        where TInterface : class, IEnumerable<T>
        where TFormatter : struct, INinoFormatter<T>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(TInterface value, ref Writer writer) =>
            NinoCollections.WriteInterface<T, TFormatter>(value, ref writer);

        public void Deserialize(ref TInterface value, ref Reader reader)
        {
            // The read-only wrappers: a new one over what is read, never a List (an array wraps without a copy).
            if (typeof(TInterface) == typeof(ReadOnlyCollection<T>) ||
                typeof(TInterface) == typeof(ReadOnlyObservableCollection<T>))
            {
                NinoRecycler.Discard(ref value);
                var items = NinoCollections.ReadArray<T, TFormatter>(ref reader);
                if (items != null)
                    value = typeof(TInterface) == typeof(ReadOnlyCollection<T>)
                        ? (TInterface)(object)new ReadOnlyCollection<T>(items)
                        : (TInterface)(object)new ReadOnlyObservableCollection<T>(new ObservableCollection<T>(items));
                return;
            }

            // A List<T> is handed to its formatter (which reuses or drops it); another collection is recycled here.
            var list = NinoRecycler.Reuse<TInterface, List<T>>(ref value);
            default(ListFormatter<List<T>, T, TFormatter>).Deserialize(ref list, ref reader);
            value = (TInterface)(object)list;
        }
    }

    /// <summary>
    /// Set interfaces (<c>ISet&lt;T&gt;</c>, <c>IReadOnlySet&lt;T&gt;</c>): count (-1 = null), then the elements.
    /// Read into a <c>HashSet&lt;T&gt;</c> (an existing one is reused), exactly like a <c>HashSet&lt;T&gt;</c> member.
    /// </summary>
    public struct InterfaceSetFormatter<TInterface, T, TFormatter> : INinoFormatter<TInterface>
        where TInterface : class, IEnumerable<T>
        where TFormatter : struct, INinoFormatter<T>
    {
        public void Serialize(TInterface value, ref Writer writer)
        {
            if (value is HashSet<T> set)
            {
                default(HashSetFormatter<HashSet<T>, T, TFormatter>).Serialize(set, ref writer);
                return;
            }

            NinoCollections.WriteInterface<T, TFormatter>(value, ref writer);
        }

        public void Deserialize(ref TInterface value, ref Reader reader)
        {
            // A HashSet<T> is handed to its formatter (which reuses or drops it); another collection is recycled here.
            var set = NinoRecycler.Reuse<TInterface, HashSet<T>>(ref value);
            default(HashSetFormatter<HashSet<T>, T, TFormatter>).Deserialize(ref set, ref reader);
            value = (TInterface)(object)set;
        }
    }
}
