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
                writer.UnsafeWrite(NinoWire.NullTypeId);
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

            // ReadBody takes null or exactly T: an instance of a subtype is recycled here.
            NinoRecycler.ReuseExact(ref value, typeof(T));
            default(TFormatter).ReadBody(ref value, ref reader);
        }

        /// <summary>
        /// Non-sealed class marked <c>trackReferences</c>, after its reference header said a new object follows:
        /// <c>int32</c> type id, then the body. Subtypes skip the reference header they write through a declared type
        /// that is not tracked.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void SerializeReferenced<T, TFormatter>(T value, ref Writer writer)
            where T : class
            where TFormatter : struct, INinoBodyFormatter<T>
        {
            if (value.GetType() != typeof(T))
            {
                NinoPolymorphic<T>.SerializeReferenced(value, ref writer);
                return;
            }

            writer.UnsafeWrite(default(TFormatter).TypeId);
            default(TFormatter).WriteBody(value, ref writer);
        }

        /// <summary>Reads what <see cref="SerializeReferenced{T,TFormatter}"/> wrote.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void DeserializeReferenced<T, TFormatter>(ref T value, ref Reader reader)
            where T : class
            where TFormatter : struct, INinoBodyFormatter<T>
        {
            int id = reader.UnsafeRead<int>();
            if (id != default(TFormatter).TypeId)
            {
                NinoPolymorphic<T>.DeserializeReferenced(id, ref value, ref reader);
                return;
            }

            NinoRecycler.ReuseExact(ref value, typeof(T));
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
                writer.UnsafeWrite(NinoWire.Null);
                return;
            }

            writer.UnsafeWrite(NinoWire.NotNull);
            default(TFormatter).WriteBody(value, ref writer);
        }

        /// <summary>Sealed class: reads what <see cref="SerializeSealed{T,TFormatter}"/> wrote.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void DeserializeSealed<T, TFormatter>(ref T value, ref Reader reader)
            where T : class
            where TFormatter : struct, INinoBodyFormatter<T>
        {
            if (reader.UnsafeRead<byte>() == NinoWire.Null)
            {
                NinoRecycler.Discard(ref value);
                return;
            }

            default(TFormatter).ReadBody(ref value, ref reader);
        }
    }
}
