using System;
using System.Runtime.CompilerServices;

namespace Nino.Core
{
    /// <summary><c>ValueTuple&lt;T1&gt;</c>: the items in order.</summary>
    public struct ValueTupleFormatter<T1, TF1> : INinoFormatter<ValueTuple<T1>>
        where TF1 : struct, INinoFormatter<T1>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(ValueTuple<T1> value, ref Writer writer)
        {
            default(TF1).Serialize(value.Item1, ref writer);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Deserialize(ref ValueTuple<T1> value, ref Reader reader)
        {
            default(TF1).Deserialize(ref value.Item1, ref reader);
        }
    }

    /// <summary><c>Tuple&lt;T1&gt;</c>: 1-byte null flag, then the items in order.</summary>
    public struct TupleFormatter<T1, TF1> : INinoFormatter<Tuple<T1>>
        where TF1 : struct, INinoFormatter<T1>
    {
        public void Serialize(Tuple<T1> value, ref Writer writer)
        {
            if (value == null)
            {
                writer.UnsafeWrite((byte)0);
                return;
            }

            writer.UnsafeWrite((byte)1);
            default(TF1).Serialize(value.Item1, ref writer);
        }

        public void Deserialize(ref Tuple<T1> value, ref Reader reader)
        {
            if (reader.UnsafeRead<byte>() == 0)
            {
                NinoRecycler.Discard(ref value);
                return;
            }

            T1 item1 = default;
            default(TF1).Deserialize(ref item1, ref reader);
            var old = value;
            value = new Tuple<T1>(item1);
            NinoRecycler<Tuple<T1>>.Recycle(old);
        }
    }

    /// <summary><c>ValueTuple&lt;T1, T2&gt;</c>: the items in order.</summary>
    public struct ValueTupleFormatter<T1, T2, TF1, TF2> : INinoFormatter<ValueTuple<T1, T2>>
        where TF1 : struct, INinoFormatter<T1>
        where TF2 : struct, INinoFormatter<T2>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(ValueTuple<T1, T2> value, ref Writer writer)
        {
            default(TF1).Serialize(value.Item1, ref writer);
            default(TF2).Serialize(value.Item2, ref writer);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Deserialize(ref ValueTuple<T1, T2> value, ref Reader reader)
        {
            default(TF1).Deserialize(ref value.Item1, ref reader);
            default(TF2).Deserialize(ref value.Item2, ref reader);
        }
    }

    /// <summary><c>Tuple&lt;T1, T2&gt;</c>: 1-byte null flag, then the items in order.</summary>
    public struct TupleFormatter<T1, T2, TF1, TF2> : INinoFormatter<Tuple<T1, T2>>
        where TF1 : struct, INinoFormatter<T1>
        where TF2 : struct, INinoFormatter<T2>
    {
        public void Serialize(Tuple<T1, T2> value, ref Writer writer)
        {
            if (value == null)
            {
                writer.UnsafeWrite((byte)0);
                return;
            }

            writer.UnsafeWrite((byte)1);
            default(TF1).Serialize(value.Item1, ref writer);
            default(TF2).Serialize(value.Item2, ref writer);
        }

        public void Deserialize(ref Tuple<T1, T2> value, ref Reader reader)
        {
            if (reader.UnsafeRead<byte>() == 0)
            {
                NinoRecycler.Discard(ref value);
                return;
            }

            T1 item1 = default;
            default(TF1).Deserialize(ref item1, ref reader);
            T2 item2 = default;
            default(TF2).Deserialize(ref item2, ref reader);
            var old = value;
            value = new Tuple<T1, T2>(item1, item2);
            NinoRecycler<Tuple<T1, T2>>.Recycle(old);
        }
    }

    /// <summary><c>ValueTuple&lt;T1, T2, T3&gt;</c>: the items in order.</summary>
    public struct ValueTupleFormatter<T1, T2, T3, TF1, TF2, TF3> : INinoFormatter<ValueTuple<T1, T2, T3>>
        where TF1 : struct, INinoFormatter<T1>
        where TF2 : struct, INinoFormatter<T2>
        where TF3 : struct, INinoFormatter<T3>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(ValueTuple<T1, T2, T3> value, ref Writer writer)
        {
            default(TF1).Serialize(value.Item1, ref writer);
            default(TF2).Serialize(value.Item2, ref writer);
            default(TF3).Serialize(value.Item3, ref writer);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Deserialize(ref ValueTuple<T1, T2, T3> value, ref Reader reader)
        {
            default(TF1).Deserialize(ref value.Item1, ref reader);
            default(TF2).Deserialize(ref value.Item2, ref reader);
            default(TF3).Deserialize(ref value.Item3, ref reader);
        }
    }

    /// <summary><c>Tuple&lt;T1, T2, T3&gt;</c>: 1-byte null flag, then the items in order.</summary>
    public struct TupleFormatter<T1, T2, T3, TF1, TF2, TF3> : INinoFormatter<Tuple<T1, T2, T3>>
        where TF1 : struct, INinoFormatter<T1>
        where TF2 : struct, INinoFormatter<T2>
        where TF3 : struct, INinoFormatter<T3>
    {
        public void Serialize(Tuple<T1, T2, T3> value, ref Writer writer)
        {
            if (value == null)
            {
                writer.UnsafeWrite((byte)0);
                return;
            }

            writer.UnsafeWrite((byte)1);
            default(TF1).Serialize(value.Item1, ref writer);
            default(TF2).Serialize(value.Item2, ref writer);
            default(TF3).Serialize(value.Item3, ref writer);
        }

        public void Deserialize(ref Tuple<T1, T2, T3> value, ref Reader reader)
        {
            if (reader.UnsafeRead<byte>() == 0)
            {
                NinoRecycler.Discard(ref value);
                return;
            }

            T1 item1 = default;
            default(TF1).Deserialize(ref item1, ref reader);
            T2 item2 = default;
            default(TF2).Deserialize(ref item2, ref reader);
            T3 item3 = default;
            default(TF3).Deserialize(ref item3, ref reader);
            var old = value;
            value = new Tuple<T1, T2, T3>(item1, item2, item3);
            NinoRecycler<Tuple<T1, T2, T3>>.Recycle(old);
        }
    }

    /// <summary><c>ValueTuple&lt;T1, T2, T3, T4&gt;</c>: the items in order.</summary>
    public struct ValueTupleFormatter<T1, T2, T3, T4, TF1, TF2, TF3, TF4> : INinoFormatter<ValueTuple<T1, T2, T3, T4>>
        where TF1 : struct, INinoFormatter<T1>
        where TF2 : struct, INinoFormatter<T2>
        where TF3 : struct, INinoFormatter<T3>
        where TF4 : struct, INinoFormatter<T4>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(ValueTuple<T1, T2, T3, T4> value, ref Writer writer)
        {
            default(TF1).Serialize(value.Item1, ref writer);
            default(TF2).Serialize(value.Item2, ref writer);
            default(TF3).Serialize(value.Item3, ref writer);
            default(TF4).Serialize(value.Item4, ref writer);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Deserialize(ref ValueTuple<T1, T2, T3, T4> value, ref Reader reader)
        {
            default(TF1).Deserialize(ref value.Item1, ref reader);
            default(TF2).Deserialize(ref value.Item2, ref reader);
            default(TF3).Deserialize(ref value.Item3, ref reader);
            default(TF4).Deserialize(ref value.Item4, ref reader);
        }
    }

    /// <summary><c>Tuple&lt;T1, T2, T3, T4&gt;</c>: 1-byte null flag, then the items in order.</summary>
    public struct TupleFormatter<T1, T2, T3, T4, TF1, TF2, TF3, TF4> : INinoFormatter<Tuple<T1, T2, T3, T4>>
        where TF1 : struct, INinoFormatter<T1>
        where TF2 : struct, INinoFormatter<T2>
        where TF3 : struct, INinoFormatter<T3>
        where TF4 : struct, INinoFormatter<T4>
    {
        public void Serialize(Tuple<T1, T2, T3, T4> value, ref Writer writer)
        {
            if (value == null)
            {
                writer.UnsafeWrite((byte)0);
                return;
            }

            writer.UnsafeWrite((byte)1);
            default(TF1).Serialize(value.Item1, ref writer);
            default(TF2).Serialize(value.Item2, ref writer);
            default(TF3).Serialize(value.Item3, ref writer);
            default(TF4).Serialize(value.Item4, ref writer);
        }

        public void Deserialize(ref Tuple<T1, T2, T3, T4> value, ref Reader reader)
        {
            if (reader.UnsafeRead<byte>() == 0)
            {
                NinoRecycler.Discard(ref value);
                return;
            }

            T1 item1 = default;
            default(TF1).Deserialize(ref item1, ref reader);
            T2 item2 = default;
            default(TF2).Deserialize(ref item2, ref reader);
            T3 item3 = default;
            default(TF3).Deserialize(ref item3, ref reader);
            T4 item4 = default;
            default(TF4).Deserialize(ref item4, ref reader);
            var old = value;
            value = new Tuple<T1, T2, T3, T4>(item1, item2, item3, item4);
            NinoRecycler<Tuple<T1, T2, T3, T4>>.Recycle(old);
        }
    }

    /// <summary><c>ValueTuple&lt;T1, T2, T3, T4, T5&gt;</c>: the items in order.</summary>
    public struct ValueTupleFormatter<T1, T2, T3, T4, T5, TF1, TF2, TF3, TF4, TF5> : INinoFormatter<ValueTuple<T1, T2, T3, T4, T5>>
        where TF1 : struct, INinoFormatter<T1>
        where TF2 : struct, INinoFormatter<T2>
        where TF3 : struct, INinoFormatter<T3>
        where TF4 : struct, INinoFormatter<T4>
        where TF5 : struct, INinoFormatter<T5>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(ValueTuple<T1, T2, T3, T4, T5> value, ref Writer writer)
        {
            default(TF1).Serialize(value.Item1, ref writer);
            default(TF2).Serialize(value.Item2, ref writer);
            default(TF3).Serialize(value.Item3, ref writer);
            default(TF4).Serialize(value.Item4, ref writer);
            default(TF5).Serialize(value.Item5, ref writer);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Deserialize(ref ValueTuple<T1, T2, T3, T4, T5> value, ref Reader reader)
        {
            default(TF1).Deserialize(ref value.Item1, ref reader);
            default(TF2).Deserialize(ref value.Item2, ref reader);
            default(TF3).Deserialize(ref value.Item3, ref reader);
            default(TF4).Deserialize(ref value.Item4, ref reader);
            default(TF5).Deserialize(ref value.Item5, ref reader);
        }
    }

    /// <summary><c>Tuple&lt;T1, T2, T3, T4, T5&gt;</c>: 1-byte null flag, then the items in order.</summary>
    public struct TupleFormatter<T1, T2, T3, T4, T5, TF1, TF2, TF3, TF4, TF5> : INinoFormatter<Tuple<T1, T2, T3, T4, T5>>
        where TF1 : struct, INinoFormatter<T1>
        where TF2 : struct, INinoFormatter<T2>
        where TF3 : struct, INinoFormatter<T3>
        where TF4 : struct, INinoFormatter<T4>
        where TF5 : struct, INinoFormatter<T5>
    {
        public void Serialize(Tuple<T1, T2, T3, T4, T5> value, ref Writer writer)
        {
            if (value == null)
            {
                writer.UnsafeWrite((byte)0);
                return;
            }

            writer.UnsafeWrite((byte)1);
            default(TF1).Serialize(value.Item1, ref writer);
            default(TF2).Serialize(value.Item2, ref writer);
            default(TF3).Serialize(value.Item3, ref writer);
            default(TF4).Serialize(value.Item4, ref writer);
            default(TF5).Serialize(value.Item5, ref writer);
        }

        public void Deserialize(ref Tuple<T1, T2, T3, T4, T5> value, ref Reader reader)
        {
            if (reader.UnsafeRead<byte>() == 0)
            {
                NinoRecycler.Discard(ref value);
                return;
            }

            T1 item1 = default;
            default(TF1).Deserialize(ref item1, ref reader);
            T2 item2 = default;
            default(TF2).Deserialize(ref item2, ref reader);
            T3 item3 = default;
            default(TF3).Deserialize(ref item3, ref reader);
            T4 item4 = default;
            default(TF4).Deserialize(ref item4, ref reader);
            T5 item5 = default;
            default(TF5).Deserialize(ref item5, ref reader);
            var old = value;
            value = new Tuple<T1, T2, T3, T4, T5>(item1, item2, item3, item4, item5);
            NinoRecycler<Tuple<T1, T2, T3, T4, T5>>.Recycle(old);
        }
    }

    /// <summary><c>ValueTuple&lt;T1, T2, T3, T4, T5, T6&gt;</c>: the items in order.</summary>
    public struct ValueTupleFormatter<T1, T2, T3, T4, T5, T6, TF1, TF2, TF3, TF4, TF5, TF6> : INinoFormatter<ValueTuple<T1, T2, T3, T4, T5, T6>>
        where TF1 : struct, INinoFormatter<T1>
        where TF2 : struct, INinoFormatter<T2>
        where TF3 : struct, INinoFormatter<T3>
        where TF4 : struct, INinoFormatter<T4>
        where TF5 : struct, INinoFormatter<T5>
        where TF6 : struct, INinoFormatter<T6>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(ValueTuple<T1, T2, T3, T4, T5, T6> value, ref Writer writer)
        {
            default(TF1).Serialize(value.Item1, ref writer);
            default(TF2).Serialize(value.Item2, ref writer);
            default(TF3).Serialize(value.Item3, ref writer);
            default(TF4).Serialize(value.Item4, ref writer);
            default(TF5).Serialize(value.Item5, ref writer);
            default(TF6).Serialize(value.Item6, ref writer);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Deserialize(ref ValueTuple<T1, T2, T3, T4, T5, T6> value, ref Reader reader)
        {
            default(TF1).Deserialize(ref value.Item1, ref reader);
            default(TF2).Deserialize(ref value.Item2, ref reader);
            default(TF3).Deserialize(ref value.Item3, ref reader);
            default(TF4).Deserialize(ref value.Item4, ref reader);
            default(TF5).Deserialize(ref value.Item5, ref reader);
            default(TF6).Deserialize(ref value.Item6, ref reader);
        }
    }

    /// <summary><c>Tuple&lt;T1, T2, T3, T4, T5, T6&gt;</c>: 1-byte null flag, then the items in order.</summary>
    public struct TupleFormatter<T1, T2, T3, T4, T5, T6, TF1, TF2, TF3, TF4, TF5, TF6> : INinoFormatter<Tuple<T1, T2, T3, T4, T5, T6>>
        where TF1 : struct, INinoFormatter<T1>
        where TF2 : struct, INinoFormatter<T2>
        where TF3 : struct, INinoFormatter<T3>
        where TF4 : struct, INinoFormatter<T4>
        where TF5 : struct, INinoFormatter<T5>
        where TF6 : struct, INinoFormatter<T6>
    {
        public void Serialize(Tuple<T1, T2, T3, T4, T5, T6> value, ref Writer writer)
        {
            if (value == null)
            {
                writer.UnsafeWrite((byte)0);
                return;
            }

            writer.UnsafeWrite((byte)1);
            default(TF1).Serialize(value.Item1, ref writer);
            default(TF2).Serialize(value.Item2, ref writer);
            default(TF3).Serialize(value.Item3, ref writer);
            default(TF4).Serialize(value.Item4, ref writer);
            default(TF5).Serialize(value.Item5, ref writer);
            default(TF6).Serialize(value.Item6, ref writer);
        }

        public void Deserialize(ref Tuple<T1, T2, T3, T4, T5, T6> value, ref Reader reader)
        {
            if (reader.UnsafeRead<byte>() == 0)
            {
                NinoRecycler.Discard(ref value);
                return;
            }

            T1 item1 = default;
            default(TF1).Deserialize(ref item1, ref reader);
            T2 item2 = default;
            default(TF2).Deserialize(ref item2, ref reader);
            T3 item3 = default;
            default(TF3).Deserialize(ref item3, ref reader);
            T4 item4 = default;
            default(TF4).Deserialize(ref item4, ref reader);
            T5 item5 = default;
            default(TF5).Deserialize(ref item5, ref reader);
            T6 item6 = default;
            default(TF6).Deserialize(ref item6, ref reader);
            var old = value;
            value = new Tuple<T1, T2, T3, T4, T5, T6>(item1, item2, item3, item4, item5, item6);
            NinoRecycler<Tuple<T1, T2, T3, T4, T5, T6>>.Recycle(old);
        }
    }

    /// <summary><c>ValueTuple&lt;T1, T2, T3, T4, T5, T6, T7&gt;</c>: the items in order.</summary>
    public struct ValueTupleFormatter<T1, T2, T3, T4, T5, T6, T7, TF1, TF2, TF3, TF4, TF5, TF6, TF7> : INinoFormatter<ValueTuple<T1, T2, T3, T4, T5, T6, T7>>
        where TF1 : struct, INinoFormatter<T1>
        where TF2 : struct, INinoFormatter<T2>
        where TF3 : struct, INinoFormatter<T3>
        where TF4 : struct, INinoFormatter<T4>
        where TF5 : struct, INinoFormatter<T5>
        where TF6 : struct, INinoFormatter<T6>
        where TF7 : struct, INinoFormatter<T7>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(ValueTuple<T1, T2, T3, T4, T5, T6, T7> value, ref Writer writer)
        {
            default(TF1).Serialize(value.Item1, ref writer);
            default(TF2).Serialize(value.Item2, ref writer);
            default(TF3).Serialize(value.Item3, ref writer);
            default(TF4).Serialize(value.Item4, ref writer);
            default(TF5).Serialize(value.Item5, ref writer);
            default(TF6).Serialize(value.Item6, ref writer);
            default(TF7).Serialize(value.Item7, ref writer);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Deserialize(ref ValueTuple<T1, T2, T3, T4, T5, T6, T7> value, ref Reader reader)
        {
            default(TF1).Deserialize(ref value.Item1, ref reader);
            default(TF2).Deserialize(ref value.Item2, ref reader);
            default(TF3).Deserialize(ref value.Item3, ref reader);
            default(TF4).Deserialize(ref value.Item4, ref reader);
            default(TF5).Deserialize(ref value.Item5, ref reader);
            default(TF6).Deserialize(ref value.Item6, ref reader);
            default(TF7).Deserialize(ref value.Item7, ref reader);
        }
    }

    /// <summary><c>Tuple&lt;T1, T2, T3, T4, T5, T6, T7&gt;</c>: 1-byte null flag, then the items in order.</summary>
    public struct TupleFormatter<T1, T2, T3, T4, T5, T6, T7, TF1, TF2, TF3, TF4, TF5, TF6, TF7> : INinoFormatter<Tuple<T1, T2, T3, T4, T5, T6, T7>>
        where TF1 : struct, INinoFormatter<T1>
        where TF2 : struct, INinoFormatter<T2>
        where TF3 : struct, INinoFormatter<T3>
        where TF4 : struct, INinoFormatter<T4>
        where TF5 : struct, INinoFormatter<T5>
        where TF6 : struct, INinoFormatter<T6>
        where TF7 : struct, INinoFormatter<T7>
    {
        public void Serialize(Tuple<T1, T2, T3, T4, T5, T6, T7> value, ref Writer writer)
        {
            if (value == null)
            {
                writer.UnsafeWrite((byte)0);
                return;
            }

            writer.UnsafeWrite((byte)1);
            default(TF1).Serialize(value.Item1, ref writer);
            default(TF2).Serialize(value.Item2, ref writer);
            default(TF3).Serialize(value.Item3, ref writer);
            default(TF4).Serialize(value.Item4, ref writer);
            default(TF5).Serialize(value.Item5, ref writer);
            default(TF6).Serialize(value.Item6, ref writer);
            default(TF7).Serialize(value.Item7, ref writer);
        }

        public void Deserialize(ref Tuple<T1, T2, T3, T4, T5, T6, T7> value, ref Reader reader)
        {
            if (reader.UnsafeRead<byte>() == 0)
            {
                NinoRecycler.Discard(ref value);
                return;
            }

            T1 item1 = default;
            default(TF1).Deserialize(ref item1, ref reader);
            T2 item2 = default;
            default(TF2).Deserialize(ref item2, ref reader);
            T3 item3 = default;
            default(TF3).Deserialize(ref item3, ref reader);
            T4 item4 = default;
            default(TF4).Deserialize(ref item4, ref reader);
            T5 item5 = default;
            default(TF5).Deserialize(ref item5, ref reader);
            T6 item6 = default;
            default(TF6).Deserialize(ref item6, ref reader);
            T7 item7 = default;
            default(TF7).Deserialize(ref item7, ref reader);
            var old = value;
            value = new Tuple<T1, T2, T3, T4, T5, T6, T7>(item1, item2, item3, item4, item5, item6, item7);
            NinoRecycler<Tuple<T1, T2, T3, T4, T5, T6, T7>>.Recycle(old);
        }
    }

}
