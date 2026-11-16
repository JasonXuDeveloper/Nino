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

            if (ImmutableCollections.Slots<T>.Possible)
            {
                ImmutableCollections.WriteEnumerated<T, TFormatter>(value, ref writer);
                return;
            }

            // No count: patched after the elements.
            int countPos = writer.ReserveInt32();
            int count = 0;
            foreach (var item in value)
            {
                default(TFormatter).Serialize(item, ref writer);
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
