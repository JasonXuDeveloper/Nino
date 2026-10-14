using System.Runtime.CompilerServices;

namespace Nino.Core
{
    /// <summary>
    /// Header handling for generated class formatters. A generated formatter implements
    /// <see cref="INinoBodyFormatter{T}"/> and forwards <c>Serialize</c>/<c>Deserialize</c> to one of these.
    /// </summary>
    public static class NinoObject
    {
        /// <summary>Non-sealed class: <c>int32</c> type id (0 = null), then the body.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Serialize<T, TFormatter>(T value, ref Writer writer)
            where T : class
            where TFormatter : struct, INinoBodyFormatter<T>
        {
            if (value == null)
            {
                writer.UnsafeWrite(0);
                return;
            }

            if (value.GetType() != typeof(T))
            {
                NinoPolymorphic<T>.Serialize(value, ref writer);
                return;
            }

            writer.UnsafeWrite(default(TFormatter).TypeId);
            default(TFormatter).WriteBody(value, ref writer);
        }

        /// <summary>Non-sealed class: reads what <see cref="Serialize{T,TFormatter}"/> wrote.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Deserialize<T, TFormatter>(ref T value, ref Reader reader)
            where T : class
            where TFormatter : struct, INinoBodyFormatter<T>
        {
            int id = reader.UnsafeRead<int>();
            if (id != default(TFormatter).TypeId)
            {
                NinoPolymorphic<T>.Deserialize(id, ref value, ref reader);
                return;
            }

            if (value != null && value.GetType() != typeof(T))
            {
                // An instance of a subtype is being replaced by an instance of exactly T.
                var old = value;
                value = null;
                default(TFormatter).ReadBody(ref value, ref reader);
                NinoRecycler<T>.Recycle(old);
                return;
            }

            default(TFormatter).ReadBody(ref value, ref reader);
        }

        /// <summary>Sealed class: 1-byte null flag, then the body.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SerializeSealed<T, TFormatter>(T value, ref Writer writer)
            where T : class
            where TFormatter : struct, INinoBodyFormatter<T>
        {
            if (value == null)
            {
                writer.UnsafeWrite((byte)0);
                return;
            }

            writer.UnsafeWrite((byte)1);
            default(TFormatter).WriteBody(value, ref writer);
        }

        /// <summary>Sealed class: reads what <see cref="SerializeSealed{T,TFormatter}"/> wrote.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void DeserializeSealed<T, TFormatter>(ref T value, ref Reader reader)
            where T : class
            where TFormatter : struct, INinoBodyFormatter<T>
        {
            if (reader.UnsafeRead<byte>() == 0)
            {
                NinoRecycler.Discard(ref value);
                return;
            }

            default(TFormatter).ReadBody(ref value, ref reader);
        }
    }
}
