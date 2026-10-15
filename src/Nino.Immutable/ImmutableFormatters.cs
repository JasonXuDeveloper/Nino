using System.Collections.Immutable;
using Nino.Core;
using Nino.Immutable;

// Registered like Nino.Core's own BCL formatters: generated code and the runtime resolver both read these.
[assembly: NinoFormatter(typeof(ImmutableArrayFormatter<,>))]
[assembly: NinoFormatter(typeof(ImmutableListFormatter<,>))]

namespace Nino.Immutable
{
    /// <summary><c>ImmutableArray&lt;T&gt;</c>: length (-1 = default), then the elements.</summary>
    public struct ImmutableArrayFormatter<T, TFormatter> : INinoFormatter<ImmutableArray<T>>
        where TFormatter : struct, INinoFormatter<T>
    {
        public void Serialize(ImmutableArray<T> value, ref Writer writer)
        {
            if (value.IsDefault)
            {
                writer.WriteNullLength();
                return;
            }

            writer.WriteLength(value.Length);
            foreach (var item in value)
                default(TFormatter).Serialize(item, ref writer);
        }

        public void Deserialize(ref ImmutableArray<T> value, ref Reader reader)
        {
            int length = reader.ReadLength();
            if (length < 0)
            {
                value = default;
                return;
            }

            var builder = ImmutableArray.CreateBuilder<T>(length);
            for (int i = 0; i < length; i++)
            {
                T item = default;
                default(TFormatter).Deserialize(ref item, ref reader);
                builder.Add(item);
            }

            value = builder.MoveToImmutable();
        }
    }

    /// <summary><c>ImmutableList&lt;T&gt;</c>: count (-1 = null), then the elements.</summary>
    public struct ImmutableListFormatter<T, TFormatter> : INinoFormatter<ImmutableList<T>>
        where TFormatter : struct, INinoFormatter<T>
    {
        public void Serialize(ImmutableList<T> value, ref Writer writer)
        {
            if (value == null)
            {
                writer.WriteNullLength();
                return;
            }

            writer.WriteLength(value.Count);
            foreach (var item in value)
                default(TFormatter).Serialize(item, ref writer);
        }

        public void Deserialize(ref ImmutableList<T> value, ref Reader reader)
        {
            int length = reader.ReadLength();
            if (length < 0)
            {
                value = null;
                return;
            }

            var builder = ImmutableList.CreateBuilder<T>();
            for (int i = 0; i < length; i++)
            {
                T item = default;
                default(TFormatter).Deserialize(ref item, ref reader);
                builder.Add(item);
            }

            value = builder.ToImmutable();
        }
    }
}
