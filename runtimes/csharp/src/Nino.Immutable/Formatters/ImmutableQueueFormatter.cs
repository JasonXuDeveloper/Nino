using System.Buffers;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Nino.Core;

namespace Nino.Immutable
{
    /// <summary><c>ImmutableQueue&lt;T&gt;</c>: count (-1 = null), then the elements front to back, as a <c>Queue&lt;T&gt;</c>.</summary>
    public struct ImmutableQueueFormatter<T, TFormatter> : INinoFormatter<ImmutableQueue<T>>
        where TFormatter : struct, INinoFormatter<T>
    {
        public void Serialize(ImmutableQueue<T> value, ref Writer writer)
        {
            if (value == null)
            {
                writer.WriteNullLength();
                return;
            }

            // Its nodes through Nino.Core's write of an enumerable, a chunk at a time: each element as a collection of
            // them writes it (raw ones as one block, a raw T? as a slot), the count patched after them.
            writer = ImmutableCollections.WriteEnumerated<T, TFormatter>(value, writer);
        }

        public void Deserialize(ref Reader reader, ref ImmutableQueue<T> value)
        {
            var array = ImmutableCollections.Read<T, TFormatter>(ref reader);
            value = array == null ? null : ImmutableQueue.CreateRange(array);
        }
    }
}
