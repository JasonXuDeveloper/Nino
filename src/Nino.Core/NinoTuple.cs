using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nino.Core
{
    public static class NinoTuple
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static NinoTuple<T1, T2> Create<T1, T2>(T1 item1, T2 item2)
        {
            return new NinoTuple<T1, T2>(item1, item2);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static NinoTuple<T1, T2, T3> Create<T1, T2, T3>(T1 item1, T2 item2, T3 item3)
        {
            return new NinoTuple<T1, T2, T3>(item1, item2, item3);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static NinoTuple<T1, T2, T3, T4> Create<T1, T2, T3, T4>(T1 item1, T2 item2, T3 item3,
            T4 item4)
        {
            return new NinoTuple<T1, T2, T3, T4>(item1, item2, item3, item4);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static NinoTuple<T1, T2, T3, T4, T5> Create<T1, T2, T3, T4, T5>(T1 item1, T2 item2,
            T3 item3, T4 item4, T5 item5)
        {
            return new NinoTuple<T1, T2, T3, T4, T5>(item1, item2, item3, item4, item5);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static NinoTuple<T1, T2, T3, T4, T5, T6> Create<T1, T2, T3, T4, T5, T6>(T1 item1,
            T2 item2, T3 item3, T4 item4, T5 item5, T6 item6)
        {
            return new NinoTuple<T1, T2, T3, T4, T5, T6>(item1, item2, item3, item4, item5, item6);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static NinoTuple<T1, T2, T3, T4, T5, T6, T7> Create<T1, T2, T3, T4, T5, T6, T7>(T1 item1,
            T2 item2, T3 item3, T4 item4, T5 item5, T6 item6, T7 item7)
        {
            return new NinoTuple<T1, T2, T3, T4, T5, T6, T7>(item1, item2, item3, item4, item5, item6,
                item7);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static NinoTuple<T1, T2, T3, T4, T5, T6, T7, T8> Create<T1, T2, T3, T4, T5, T6, T7,
            T8>(T1 item1,
            T2 item2, T3 item3, T4 item4, T5 item5, T6 item6, T7 item7, T8 item8)
        {
            return new NinoTuple<T1, T2, T3, T4, T5, T6, T7, T8>(item1, item2, item3, item4,
                item5,
                item6,
                item7,
                item8);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NinoTuple<T1, T2>
    {
        public T1 Item1;
        public T2 Item2;

        public NinoTuple(T1 item1, T2 item2)
        {
            Item1 = item1;
            Item2 = item2;
        }

        public void Deconstruct(out T1 item1, out T2 item2)
        {
            item1 = Item1;
            item2 = Item2;
        }

        public override string ToString()
        {
            return $"({Item1}, {Item2})";
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NinoTuple<T1, T2, T3>
    {
        public T1 Item1;
        public T2 Item2;
        public T3 Item3;

        public NinoTuple(T1 item1, T2 item2, T3 item3)
        {
            Item1 = item1;
            Item2 = item2;
            Item3 = item3;
        }

        public void Deconstruct(out T1 item1, out T2 item2, out T3 item3)
        {
            item1 = Item1;
            item2 = Item2;
            item3 = Item3;
        }

        public override string ToString()
        {
            return $"({Item1}, {Item2}, {Item3})";
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NinoTuple<T1, T2, T3, T4>
    {
        public T1 Item1;
        public T2 Item2;
        public T3 Item3;
        public T4 Item4;

        public NinoTuple(T1 item1, T2 item2, T3 item3, T4 item4)
        {
            Item1 = item1;
            Item2 = item2;
            Item3 = item3;
            Item4 = item4;
        }

        public void Deconstruct(out T1 item1, out T2 item2, out T3 item3, out T4 item4)
        {
            item1 = Item1;
            item2 = Item2;
            item3 = Item3;
            item4 = Item4;
        }

        public override string ToString()
        {
            return $"({Item1}, {Item2}, {Item3}, {Item4})";
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NinoTuple<T1, T2, T3, T4, T5>
    {
        public T1 Item1;
        public T2 Item2;
        public T3 Item3;
        public T4 Item4;
        public T5 Item5;

        public NinoTuple(T1 item1, T2 item2, T3 item3, T4 item4, T5 item5)
        {
            Item1 = item1;
            Item2 = item2;
            Item3 = item3;
            Item4 = item4;
            Item5 = item5;
        }

        public void Deconstruct(out T1 item1, out T2 item2, out T3 item3, out T4 item4, out T5 item5)
        {
            item1 = Item1;
            item2 = Item2;
            item3 = Item3;
            item4 = Item4;
            item5 = Item5;
        }

        public override string ToString()
        {
            return $"({Item1}, {Item2}, {Item3}, {Item4}, {Item5})";
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NinoTuple<T1, T2, T3, T4, T5, T6>
    {
        public T1 Item1;
        public T2 Item2;
        public T3 Item3;
        public T4 Item4;
        public T5 Item5;
        public T6 Item6;

        public NinoTuple(T1 item1, T2 item2, T3 item3, T4 item4, T5 item5, T6 item6)
        {
            Item1 = item1;
            Item2 = item2;
            Item3 = item3;
            Item4 = item4;
            Item5 = item5;
            Item6 = item6;
        }

        public void Deconstruct(out T1 item1, out T2 item2, out T3 item3, out T4 item4, out T5 item5, out T6 item6)
        {
            item1 = Item1;
            item2 = Item2;
            item3 = Item3;
            item4 = Item4;
            item5 = Item5;
            item6 = Item6;
        }

        public override string ToString()
        {
            return $"({Item1}, {Item2}, {Item3}, {Item4}, {Item5}, {Item6})";
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NinoTuple<T1, T2, T3, T4, T5, T6, T7>
    {
        public T1 Item1;
        public T2 Item2;
        public T3 Item3;
        public T4 Item4;
        public T5 Item5;
        public T6 Item6;
        public T7 Item7;

        public NinoTuple(T1 item1, T2 item2, T3 item3, T4 item4, T5 item5, T6 item6, T7 item7)
        {
            Item1 = item1;
            Item2 = item2;
            Item3 = item3;
            Item4 = item4;
            Item5 = item5;
            Item6 = item6;
            Item7 = item7;
        }

        public void Deconstruct(out T1 item1, out T2 item2, out T3 item3, out T4 item4, out T5 item5, out T6 item6,
            out T7 item7)
        {
            item1 = Item1;
            item2 = Item2;
            item3 = Item3;
            item4 = Item4;
            item5 = Item5;
            item6 = Item6;
            item7 = Item7;
        }

        public override string ToString()
        {
            return $"({Item1}, {Item2}, {Item3}, {Item4}, {Item5}, {Item6}, {Item7})";
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NinoTuple<T1, T2, T3, T4, T5, T6, T7, T8>
    {
        public T1 Item1;
        public T2 Item2;
        public T3 Item3;
        public T4 Item4;
        public T5 Item5;
        public T6 Item6;
        public T7 Item7;
        public T8 Item8;

        public NinoTuple(T1 item1, T2 item2, T3 item3, T4 item4, T5 item5, T6 item6, T7 item7, T8 item8)
        {
            Item1 = item1;
            Item2 = item2;
            Item3 = item3;
            Item4 = item4;
            Item5 = item5;
            Item6 = item6;
            Item7 = item7;
            Item8 = item8;
        }

        public void Deconstruct(out T1 item1, out T2 item2, out T3 item3, out T4 item4, out T5 item5, out T6 item6,
            out T7 item7, out T8 item8)
        {
            item1 = Item1;
            item2 = Item2;
            item3 = Item3;
            item4 = Item4;
            item5 = Item5;
            item6 = Item6;
            item7 = Item7;
            item8 = Item8;
        }

        public override string ToString()
        {
            return $"({Item1}, {Item2}, {Item3}, {Item4}, {Item5}, {Item6}, {Item7}, {Item8})";
        }
    }
}