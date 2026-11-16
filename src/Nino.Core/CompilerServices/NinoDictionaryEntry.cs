using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Nino.Core.Internal;

namespace Nino.Core.CompilerServices
{
    /// <summary>
    /// An entry of <see cref="Dictionary{TKey,TValue}"/> as the runtime lays it out (hashCode, next, key, value; the
    /// startup checks of <c>CollectionViews</c> compare it with the runtime's), read in place of the enumerator.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public struct NinoDictionaryEntry<TKey, TValue>
    {
#pragma warning disable CS0649, CA1051 // read through Unsafe.As; fields, as the runtime lays out the entry
        /// <summary>The key's hash code.</summary>
        public int HashCode;

        /// <summary>The next entry in the bucket's chain.</summary>
        public int Next;

        /// <summary>The key.</summary>
        public TKey Key;

        /// <summary>The value.</summary>
        public TValue Value;
#pragma warning restore CS0649, CA1051
    }

    /// <summary>The entries of a dictionary, for generated loops over its pairs.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static class NinoDictionaryEntries
    {
        /// <summary>
        /// The entries of <paramref name="dictionary"/>, whose first <paramref name="count"/> (its Count) are its pairs
        /// in enumeration order, when all of those are in use (none removed since) and the runtime's layout was found;
        /// else null. A loop over them keeps no enumerator in memory and checks no version per pair.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static NinoDictionaryEntry<TKey, TValue>[] InUse<TKey, TValue>(Dictionary<TKey, TValue> dictionary,
            out int count)
        {
            count = dictionary.Count;
            return CollectionFields.Entries(dictionary, count);
        }

        /// <summary>The entry after <paramref name="entry"/>, for a loop over <see cref="InUse"/>'s entries.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref NinoDictionaryEntry<TKey, TValue> Next<TKey, TValue>(
            ref NinoDictionaryEntry<TKey, TValue> entry) => ref Unsafe.Add(ref entry, 1);
    }
}
