using System.Buffers;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using Nino.Core;
using Nino.Core.CompilerServices;

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

            // No count: patched after the elements, each as a collection of them writes it (NinoElement: a raw T? as a
            // slot), walked with its own (struct) enumerator: through IEnumerable<T> each step is two interface calls,
            // and gathered into a rented array first 1,000 ints were written in 1.2 times the time (local quick runs).
            int countPos = writer.ReserveInt32();
            int count = 0;
            foreach (var item in value)
            {
                NinoElement.Write<T, TFormatter>(in item, ref writer);
                count++;
            }

            writer.PatchInt32(countPos, count);
        }

        public void Deserialize(ref Reader reader, ref ImmutableQueue<T> value)
        {
            var array = ImmutableCollections.Read<T, TFormatter>(ref reader);
            value = array == null ? null : ImmutableQueue.CreateRange(array);
        }
    }
}
