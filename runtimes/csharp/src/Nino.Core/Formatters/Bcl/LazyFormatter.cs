using System;
using System.Buffers;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Nino.Core.CompilerServices;
using Nino.Core.Internal;

namespace Nino.Core
{
    /// <summary>
    /// <c>Lazy&lt;T&gt;</c>: a presence byte (0 = null), then the value, which serializing creates if it was not yet.
    /// Read back as an already created <c>Lazy&lt;T&gt;</c>.
    /// </summary>
    public struct LazyFormatter<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] T, TFormatter> : INinoFormatter<Lazy<T>>
        where TFormatter : struct, INinoFormatter<T>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(Lazy<T> value, ref Writer writer)
        {
            if (writer.WritePresence(value))
                default(TFormatter).Serialize(value.Value, ref writer);
        }

        public void Deserialize(ref Reader reader, ref Lazy<T> value)
        {
            if (reader.TryReadPresent(ref value))
            {
                // A Lazy is immutable once created, as a tuple: the value is read into a local and the Lazy replaced.
                T item = default;
                default(TFormatter).Deserialize(ref reader, ref item);
                var created = new Lazy<T>(item);
                NinoRecycler.Replace(ref value, created);
            }
        }
    }
}
