using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nino.Core.CompilerServices;
using Nino.Core.Internal;

namespace Nino.Core
{
    internal static class NinoCollectionViews
    {
        /// <summary>
        /// Reads <paramref name="length"/> elements into the start of the backing array of an empty Stack or Queue
        /// (<see cref="CollectionFields"/>), replacing it with a new one when it is too
        /// small, and returns the array. Each element is read as a new value, as Push and Enqueue of a default
        /// one would: slots of references are already default in an empty collection, unmanaged ones may hold
        /// what was removed and are cleared first, unless the read overwrites them
        /// (<see cref="NinoElements.ReadOverwrites{T,TFormatter}"/>).
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T[] ReadInto<T, TFormatter>(ref T[] array, int length, ref Reader reader)
            where TFormatter : struct, INinoFormatter<T>
        {
            var items = array;
            if (items.Length < length)
            {
                // Elements far larger in memory than on the wire: out of line, the array grown as they are read
                // where the data could not fill as many, from the one the collection has (created with a clamped
                // capacity), cleared as above.
                if (Unsafe.SizeOf<T>() > NinoElements.Amplification && NinoElements.HeavyElements<T, TFormatter>.Value)
                {
                    if (!NinoElements.ReadOverwrites<T, TFormatter>() && !NinoUnsafe.IsReferenceOrContainsReferences<T>())
                        Array.Clear(items, 0, items.Length);
                    reader = NinoElements.ReadGrowing<T, TFormatter>(ref items, length, reader);
                    return array = items;
                }

                array = items = new T[length];
            }

            var span = MemoryMarshal.CreateSpan(ref NinoUnsafe.GetArrayDataReference(items), length);
            if (!NinoElements.ReadOverwrites<T, TFormatter>() && !NinoUnsafe.IsReferenceOrContainsReferences<T>())
                span.Clear();
            NinoElements.Read<T, TFormatter>(span, ref reader, prechecked: true);
            return items;
        }
    }
}
