using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Nino.Core
{
    /// <summary>
    /// List-like collection interfaces (<c>IEnumerable&lt;T&gt;</c>, <c>IList&lt;T&gt;</c>, <c>ICollection&lt;T&gt;</c>,
    /// <c>IReadOnlyList&lt;T&gt;</c>, <c>IReadOnlyCollection&lt;T&gt;</c>): count (-1 = null), then the elements.
    /// Read into a <c>List&lt;T&gt;</c> (an existing one is reused). <c>ReadOnlyCollection&lt;T&gt;</c> and
    /// <c>ReadOnlyObservableCollection&lt;T&gt;</c> are written the same way and read as a new wrapper around that list.
    /// </summary>
    public struct InterfaceListFormatter<TInterface, T, TFormatter> : INinoFormatter<TInterface>
        where TInterface : class, IEnumerable<T>
        where TFormatter : struct, INinoFormatter<T>
    {
        public void Serialize(TInterface value, ref Writer writer) =>
            NinoCollections.WriteInterface<T, TFormatter>(value, ref writer);

        public void Deserialize(ref TInterface value, ref Reader reader)
        {
            // A List<T> is handed to its formatter (which reuses or drops it); another collection is recycled here.
            var list = NinoRecycler.Reuse<TInterface, List<T>>(ref value);
            default(ListFormatter<List<T>, T, TFormatter>).Deserialize(ref list, ref reader);
            if (list == null)
                value = null;
            else if (typeof(TInterface) == typeof(ReadOnlyCollection<T>))
                value = (TInterface)(object)new ReadOnlyCollection<T>(list);
            else if (typeof(TInterface) == typeof(ReadOnlyObservableCollection<T>))
                value = (TInterface)(object)new ReadOnlyObservableCollection<T>(new ObservableCollection<T>(list));
            else
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

    internal static partial class NinoCollections
    {
        /// <summary>
        /// Writes a collection declared as an interface: count (-1 = null), then the elements. Lists and arrays
        /// take their own (block-copying) paths.
        /// </summary>
        public static void WriteInterface<T, TFormatter>(IEnumerable<T> value, ref Writer writer)
            where TFormatter : struct, INinoFormatter<T>
        {
            if (value == null)
            {
                writer.WriteNullLength();
                return;
            }

            if (value is List<T> list)
            {
                default(ListFormatter<List<T>, T, TFormatter>).Serialize(list, ref writer);
                return;
            }

            if (value is T[] array)
            {
                default(ArrayFormatter<T, TFormatter>).Serialize(array, ref writer);
                return;
            }

            WriteEnumerable<T, TFormatter>(value, ref writer);
        }
    }
}
