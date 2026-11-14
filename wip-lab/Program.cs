using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Nino.Core;

static class P
{
    static readonly System.Buffers.ArrayBufferWriter<byte> Buffer = new(1 << 20);

    static void Run<T>(string name, T value)
    {
        byte[] bytes = NinoSerializer.Serialize(value);
        T back = default;
        for (int i = 0; i < 30000; i++) { Buffer.ResetWrittenCount(); NinoSerializer.Serialize(value, Buffer); NinoDeserializer.Deserialize(bytes, ref back); NinoDeserializer.Deserialize<T>(bytes); }
        double best1 = double.MaxValue, best2 = double.MaxValue, best3 = double.MaxValue;
        for (int r = 0; r < 21; r++)
        {
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < 20000; i++) { Buffer.ResetWrittenCount(); NinoSerializer.Serialize(value, Buffer); }
            best1 = Math.Min(best1, sw.Elapsed.TotalMilliseconds * 1e6 / 20000);
            sw.Restart();
            for (int i = 0; i < 20000; i++) NinoDeserializer.Deserialize(bytes, ref back);
            best2 = Math.Min(best2, sw.Elapsed.TotalMilliseconds * 1e6 / 20000);
            sw.Restart();
            for (int i = 0; i < 20000; i++) NinoDeserializer.Deserialize<T>(bytes);
            best3 = Math.Min(best3, sw.Elapsed.TotalMilliseconds * 1e6 / 20000);
        }
        Console.WriteLine($"{name,-34} {bytes.Length,6} B  ser {best1,8:F1}  de-reuse {best2,8:F1}  de-new {best3,8:F1} ns");
    }

    static void Dump<T>(string name, T value)
    {
        byte[] bytes = NinoSerializer.Serialize(value);
        T back = NinoDeserializer.Deserialize<T>(bytes);
        byte[] again = NinoSerializer.Serialize(back);
        Console.WriteLine($"{name,-40} {bytes.Length,3} B {(bytes.AsSpan().SequenceEqual(again) ? "rt" : "RT-DIFF")}  {Convert.ToHexString(bytes)}");
    }

    static void DumpAll()
    {
        int?[] a = { 1, null };
        NinoNullable<int>[] n = { 1, default };
        Dump("int?", (int?)1);
        Dump("int?[]", a);
        Dump("List<int?>", a.ToList());
        Dump("Queue<int?>", new Queue<int?>(a));
        Dump("Stack<int?>", new Stack<int?>(a.Reverse()));
        Dump("HashSet<int?>", new HashSet<int?>(a));
        Dump("SortedSet<int?>", new SortedSet<int?>(a));
        Dump("LinkedList<int?>", new LinkedList<int?>(a));
        Dump("Memory<int?>", new Memory<int?>(a));
        Dump("ReadOnlyMemory<int?>", new ReadOnlyMemory<int?>(a));
        Dump("ArraySegment<int?>", new ArraySegment<int?>(a));
        Dump("ReadOnlySequence<int?>", new System.Buffers.ReadOnlySequence<int?>(a));
        Dump("int?[,]", new int?[,] { { 1, null } });
        Dump("int?[,,]", new int?[,,] { { { 1, null } } });
        Dump("IList<int?>", (IList<int?>)a.ToList());
        Dump("ICollection<int?>", (ICollection<int?>)a.ToList());
        Dump("IEnumerable<int?>", (IEnumerable<int?>)a.ToList());
        Dump("IReadOnlyList<int?>", (IReadOnlyList<int?>)a.ToList());
        Dump("ISet<int?>", (ISet<int?>)new HashSet<int?>(a));
        Dump("Collection<int?>", new System.Collections.ObjectModel.Collection<int?>(a.ToList()));
        Dump("ObservableCollection<int?>", new System.Collections.ObjectModel.ObservableCollection<int?>(a));
        Dump("ConcurrentQueue<int?>", new System.Collections.Concurrent.ConcurrentQueue<int?>(a));
        Dump("ConcurrentStack<int?>", new System.Collections.Concurrent.ConcurrentStack<int?>(a.Reverse()));
        Dump("ConcurrentBag<int?>", new System.Collections.Concurrent.ConcurrentBag<int?>(a));
        Dump("BlockingCollection<int?>", new System.Collections.Concurrent.BlockingCollection<int?> { 1, null });
        Dump("ILookup<int,int?>", a.ToLookup(x => 7));
        Dump("Dictionary<int,int?>", new Dictionary<int, int?> { { 1, 1 }, { 2, null } });
        Dump("SortedDictionary<int,int?>", new SortedDictionary<int, int?> { { 1, 1 }, { 2, null } });
        Dump("KeyValuePair<int,int?>", new KeyValuePair<int, int?>(1, null));
        Dump("KeyValuePair<int,int?>[]", new[] { new KeyValuePair<int, int?>(1, 1), new KeyValuePair<int, int?>(2, null) });
        Dump("(int,int?)", (1, (int?)null));
        Dump("List<(int,int?)>", new List<(int, int?)> { (1, 1), (2, null) });
        Dump("PriorityQueue<int?,int>", MakePq());
        Dump("int?[][]", new[] { a });
        Dump("List<List<int?>>", new List<List<int?>> { a.ToList() });
        Dump("List<Queue<int?>>", new List<Queue<int?>> { new Queue<int?>(a) });
        Dump("NinoNullable<int>", (NinoNullable<int>)1);
        Dump("NinoNullable<int>[]", n);
        Dump("List<NinoNullable<int>>", n.ToList());
        Dump("Queue<NinoNullable<int>>", new Queue<NinoNullable<int>>(n));
        Dump("HashSet<NinoNullable<int>>", new HashSet<NinoNullable<int>>(n));
        Dump("LinkedList<NinoNullable<int>>", new LinkedList<NinoNullable<int>>(n));
        Dump("Memory<NinoNullable<int>>", new Memory<NinoNullable<int>>(n));
        Dump("NinoNullable<int>[,]", new NinoNullable<int>[,] { { 1, default } });
        Dump("IList<NinoNullable<int>>", (IList<NinoNullable<int>>)n.ToList());
        Dump("Dictionary<int,NinoNullable<int>>", new Dictionary<int, NinoNullable<int>> { { 1, 1 }, { 2, default } });
        Dump("bool?[]", new bool?[] { true, null });
        Dump("Queue<bool?>", new Queue<bool?>(new bool?[] { true, null }));
        Dump("Queue<byte?>", new Queue<byte?>(new byte?[] { 1, null }));
        Dump("Queue<long?>", new Queue<long?>(new long?[] { 1, null }));
        Dump("Queue<decimal?>", new Queue<decimal?>(new decimal?[] { 1, null }));
        Dump("Queue<DateTimeOffset?>", new Queue<DateTimeOffset?>(new DateTimeOffset?[] { DateTimeOffset.UnixEpoch, null }));
        Dump("Queue<(int,int)?>", new Queue<(int, int)?>(new (int, int)?[] { (1, 2), null }));
        Dump("Queue<string>", new Queue<string>(new[] { "a", null }));
    }

    static PriorityQueue<int?, int> MakePq() { var q = new PriorityQueue<int?, int>(); q.Enqueue(1, 1); q.Enqueue(null, 2); return q; }

    static void Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "dump") { DumpAll(); return; }
        int n = 1000;
        int?[] ints = Enumerable.Range(0, n).Select(i => i % 5 == 0 ? (int?)null : i).ToArray();
        long?[] longs = ints.Select(i => (long?)i).ToArray();
        decimal?[] decs = ints.Select(i => (decimal?)i).ToArray();
        var nn = ints.Select(i => (NinoNullable<int>)i).ToArray();
        var nl = longs.Select(i => (NinoNullable<long>)i).ToArray();
        Run("int?[]", ints);
        Run("List<int?>", ints.ToList());
        Run("Queue<int?>", new Queue<int?>(ints));
        Run("Stack<int?>", new Stack<int?>(ints));
        Run("HashSet<int?>", new HashSet<int?>(Enumerable.Range(0, n).Select(i => i == 0 ? (int?)null : i)));
        Run("LinkedList<int?>", new LinkedList<int?>(ints));
        Run("Memory<int?>", new Memory<int?>(ints));
        Run("ArraySegment<int?>", new ArraySegment<int?>(ints));
        Run("int?[,]", To2D(ints));
        Run("IList<int?>", (IList<int?>)ints.ToList());
        Run("Dictionary<int,int?>", ints.Select((v, i) => (v, i)).ToDictionary(p => p.i, p => p.v));
        Run("long?[]", longs);
        Run("Queue<long?>", new Queue<long?>(longs));
        Run("decimal?[]", decs);
        Run("Queue<decimal?>", new Queue<decimal?>(decs));
        Run("NinoNullable<int>[]", nn);
        Run("Queue<NinoNullable<int>>", new Queue<NinoNullable<int>>(nn));
        Run("NinoNullable<long>[]", nl);
        Run("Queue<NinoNullable<long>>", new Queue<NinoNullable<long>>(nl));
        Run("Dictionary<int,NinoNullable<int>>", nn.Select((v, i) => (v, i)).ToDictionary(p => p.i, p => p.v));
        Run("HashSet<NinoNullable<int>>", new HashSet<NinoNullable<int>>(nn));
        Run("LinkedList<long?>", new LinkedList<long?>(longs));
        Run("SortedSet<int?>", new SortedSet<int?>(ints));
        Run("ConcurrentQueue<int?>", new System.Collections.Concurrent.ConcurrentQueue<int?>(ints));
        Run("ImmutableLikeCollection<int?>", new System.Collections.ObjectModel.Collection<int?>(ints.ToList()));
        Run("List<int?> x16 in List", Enumerable.Range(0, 60).Select(_ => ints.Take(16).ToList()).ToList());
        Run("Queue<int?> x16 in List", Enumerable.Range(0, 60).Select(_ => new Queue<int?>(ints.Take(16))).ToList());
        Run("HashSet<int?> x8 in List", Enumerable.Range(0, 60).Select(k => new HashSet<int?>(ints.Skip(k).Take(8))).ToList());
        Run("Queue<int?> all null", new Queue<int?>(new int?[1000]));
        Run("Queue<int> (control)", new Queue<int>(Enumerable.Range(0, 1000)));
        Run("HashSet<int> (control)", new HashSet<int>(Enumerable.Range(0, 1000)));
    }

    static int?[,] To2D(int?[] a) { var r = new int?[a.Length / 10, 10]; for (int i = 0; i < a.Length; i++) r[i / 10, i % 10] = a[i]; return r; }
}
