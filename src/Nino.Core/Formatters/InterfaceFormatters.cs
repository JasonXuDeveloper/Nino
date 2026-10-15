using System.Collections.Generic;

namespace Nino.Core
{
    /// <summary>
    /// List-like collection interfaces (<c>IEnumerable&lt;T&gt;</c>, <c>IList&lt;T&gt;</c>, <c>ICollection&lt;T&gt;</c>,
    /// <c>IReadOnlyList&lt;T&gt;</c>, <c>IReadOnlyCollection&lt;T&gt;</c>): count (-1 = null), then the elements.
    /// Read into a <c>List&lt;T&gt;</c> (an existing one is reused).
    /// </summary>
    public struct InterfaceListFormatter<TInterface, T, TFormatter> : INinoFormatter<TInterface>
        where TInterface : class, IEnumerable<T>
        where TFormatter : struct, INinoFormatter<T>
    {
        public void Serialize(TInterface value, ref Writer writer) =>
            NinoCollections.WriteInterface<T, TFormatter>(value, ref writer);

        public void Deserialize(ref TInterface value, ref Reader reader)
        {
            var old = value;
            var list = old as List<T>;
            default(ListFormatter<List<T>, T, TFormatter>).Deserialize(ref list, ref reader);
            value = (TInterface)(object)list;
            // A List<T> was handed to the formatter, which reused or recycled it; another collection is dropped.
            if (old != null && !(old is List<T>))
                NinoRecycler<TInterface>.Recycle(old);
        }
    }

    /// <summary>
    /// Set interfaces (<c>ISet&lt;T&gt;</c>, <c>IReadOnlySet&lt;T&gt;</c>): count (-1 = null), then the elements.
    /// Read into a <c>HashSet&lt;T&gt;</c> (an existing one is reused).
    /// </summary>
    public struct InterfaceSetFormatter<TInterface, T, TFormatter> : INinoFormatter<TInterface>
        where TInterface : class, IEnumerable<T>
        where TFormatter : struct, INinoFormatter<T>
    {
        public void Serialize(TInterface value, ref Writer writer) =>
            NinoCollections.WriteInterface<T, TFormatter>(value, ref writer);

        public void Deserialize(ref TInterface value, ref Reader reader)
        {
            var old = value;
            var set = old as HashSet<T>;
            default(CollectionFormatter<HashSet<T>, T, TFormatter>).Deserialize(ref set, ref reader);
            value = (TInterface)(object)set;
            // A HashSet<T> was handed to the formatter, which reused or recycled it; another collection is dropped.
            if (old != null && !(old is HashSet<T>))
                NinoRecycler<TInterface>.Recycle(old);
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
