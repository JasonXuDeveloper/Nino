using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using Nino.Core;

[NinoType]
public struct Padded { public int A; public short B; public bool C; public long D; }

// 8 bytes in memory, 7 as a block: eight to a 64-byte permute.
[NinoType]
public struct Small { public int A; public short B; public bool C; }

// Of fixed size, not raw (a value tuple, a pair, auto-properties): written as blocks (INinoBlockFormatter).
[NinoType]
public struct Waypoint { public float X; public (int Tick, short Zone) Stamp; public bool Visited; }

[NinoType]
public struct Keyed { public KeyValuePair<int, short> K { get; set; } public bool On { get; set; } }

[NinoType]
public class Item
{
    public int Id;
    public string Name;
    public List<(int, long)> Pairs;
    public SortedDictionary<string, Padded> ByName;
    public DateTimeOffset At;
}

/// <summary>
/// Round trips of the built-in collections and types whose fields Nino reads directly on some runtimes
/// (<c>CollectionViews</c>), printed as one line per case: name, size, hash of the bytes, and whether writing what was
/// read gives the same bytes (also when read into the instance read before).
/// </summary>
public static class Program
{
    private static int _failed;

    private static void Case<T>(string name, T value)
    {
        try
        {
            byte[] bytes = NinoSerializer.Serialize(value);
            T back = NinoDeserializer.Deserialize<T>(bytes);
            byte[] again = NinoSerializer.Serialize(back);
            bool same = bytes.SequenceEqual(again);
            // Read into an existing instance too (reuse paths).
            T into = back;
            NinoDeserializer.Deserialize(bytes, ref into);
            same &= bytes.SequenceEqual(NinoSerializer.Serialize(into));
            string hash;
            using (var sha = SHA256.Create())
                hash = BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").Substring(0, 16);
            if (!same) _failed++;
            Console.WriteLine($"{name,-24} {bytes.Length,6} {hash} {(same ? "ok" : "MISMATCH")}");
        }
        catch (Exception e)
        {
            _failed++;
            Console.WriteLine($"{name,-24} EXCEPTION {e.GetType().Name}: {e.Message}");
        }
    }

    public static int Main()
    {
        var views = typeof(NinoSerializer).Assembly.GetType("Nino.Core.Internal.CollectionViews");
        // Informational, not compared: where this runtime keeps the fields.
        if (views != null)
            Console.WriteLine("# views: " + string.Join(" ", views.GetFields(BindingFlags.Public | BindingFlags.Static)
                .Select(f => f.Name + "=" + f.GetValue(null))));
        Console.WriteLine("# 64-bit: " + Environment.Is64BitProcess + ", runtime: " +
                          System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);

        var ints = Enumerable.Range(0, 300).Select(i => i * 7919 % 1000 - 500).ToArray();
        var strs = Enumerable.Range(0, 50).Select(i => i % 9 == 0 ? null : "s" + i + new string('x', i % 20)).ToArray();
        var padded = ints.Select(i => new Padded { A = i, B = (short)i, C = i % 2 == 0, D = (long)i << 33 }).ToArray();
        Case("int", 12345);
        Case("string", "héllo wörld");
        Case("string[]", strs);
        Case("decimal[]", ints.Select(i => i / 7m).ToArray());
        Case("DateTime[]", ints.Select(i => new DateTime(637000000000000000L + i * 1000L, DateTimeKind.Utc)).ToArray());
        Case("DateTimeOffset[]", ints.Select(i => new DateTimeOffset(637000000000000000L + i, TimeSpan.FromMinutes(i % 60))).ToList());
        Case("Guid[]", ints.Select(i => new Guid(i, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10)).ToArray());
        Case("int?[]", ints.Select(i => i % 3 == 0 ? (int?)null : i).ToArray());
        Case("List<int?>", ints.Select(i => i % 3 == 0 ? (int?)null : i).ToList());
        Case("List<int>", ints.ToList());
        Case("int[,]", new int[7, 9]);
        Case("Padded[]", padded);
        Case("List<Padded>", padded.ToList());
        // Lengths around the 16- and 64-byte vector paths and their tails.
        foreach (int n in Enumerable.Range(0, 20).Concat(new[] { 31, 63, 64, 65, 100, 1000 }))
        {
            Case("Small[" + n + "]", Enumerable.Range(0, n).Select(i => new Small { A = i * 7, B = (short)-i, C = i % 3 == 0 }).ToArray());
            Case("Waypoint[" + n + "]", Enumerable.Range(0, n).Select(i => new Waypoint { X = i / 3f, Stamp = (i * 5, (short)-i), Visited = i % 3 == 0 }).ToArray());
            Case("List<Keyed>[" + n + "]", Enumerable.Range(0, n).Select(i => new Keyed { K = new KeyValuePair<int, short>(i, (short)i), On = i % 2 == 0 }).ToList());
            Case("(byte,short)[" + n + "]", Enumerable.Range(0, n).Select(i => ((byte)i, (short)(i * 3))).ToArray());
        }
        Case("(int,int)[]", ints.Select(i => (i, i * 3)).ToArray());
        Case("List<(int,long)>", ints.Select(i => (i, (long)i << 40)).ToList());
        Case("List<(long,long,int)>", ints.Select(i => ((long)i, (long)i * 2, i)).ToList());
        // Over 6 KB: read into a new array allocated uninitialized where the CPU allows it.
        var many = Enumerable.Range(0, 1000).Select(i => (i * 7919 % 1000 - 500, (long)i << 40)).ToArray();
        Case("(int,long)[1000]", many);
        Case("List<(int,long)> 1000", many.ToList());
        Case("(byte,int,short)[]", ints.Select(i => ((byte)i, i, (short)i)).ToArray());
        Case("KVP<int,int>[]", ints.Select(i => new KeyValuePair<int, int>(i, -i)).ToArray());
        Case("KVP<int,string>", ints.Take(30).Select(i => new KeyValuePair<int, string>(i, "v" + i)).ToList());
        Case("Stack<int>", new Stack<int>(ints));
        Case("Stack<string>", new Stack<string>(strs));
        var q = new Queue<int>(ints); for (int i = 0; i < 100; i++) { q.Dequeue(); q.Enqueue(i); }
        Case("Queue<int> wrapped", q);
        Case("HashSet<int>", new HashSet<int>(ints));
        Case("Dictionary<int,str>", ints.Distinct().ToDictionary(i => i, i => "v" + i));
        Case("SortedList<int,int>", new SortedList<int, int>(ints.Distinct().ToDictionary(i => i, i => i * 2)));
        Case("SortedList<str,int>", new SortedList<string, int>(strs.Where(s => s != null).Distinct().ToDictionary(s => s, s => s.Length), StringComparer.Ordinal));
        Case("SortedSet<int>", new SortedSet<int>(ints));
        Case("SortedSet<string>", new SortedSet<string>(strs.Where(s => s != null), StringComparer.Ordinal));
        Case("SortedDict<int,Padded>", new SortedDictionary<int, Padded>(ints.Distinct().ToDictionary(i => i, i => padded[0])));
        Case("SortedDict<int,long>", new SortedDictionary<int, long>(ints.Distinct().ToDictionary(i => i, i => (long)i)));
        Case("LinkedList<int>", new LinkedList<int>(ints));
        Case("LinkedList<Padded>", new LinkedList<Padded>(padded));
        Case("LinkedList<string>", new LinkedList<string>(strs));
        // One entry: the order of more depends on the dictionary's capacity, which reading may change.
        Case("ConcurrentDict", new ConcurrentDictionary<int, int>(new Dictionary<int, int> { [42] = 7 }));
        Case("ConcurrentQueue", new ConcurrentQueue<int>(ints));
        Case("ConcurrentStack", new ConcurrentStack<int>(ints));
        Case("ObservableColl", new ObservableCollection<int>(ints));
        Case("IList<int>", (IList<int>)ints.ToList());
        Case("IEnumerable<str>", (IEnumerable<string>)strs.ToList());
        Case("Item", new Item { Id = 3, Name = "n", Pairs = ints.Take(9).Select(i => (i, (long)i)).ToList(),
            ByName = new SortedDictionary<string, Padded> { ["a"] = padded[1], ["b"] = padded[2] },
            At = new DateTimeOffset(2026, 10, 18, 1, 2, 3, TimeSpan.FromHours(8)) });
        Case("List<Item>", Enumerable.Range(0, 20).Select(i => new Item { Id = i, Name = i % 2 == 0 ? null : "x" + i }).ToList());
        // A bool byte other than 0 or 1 in a block, wherever the vector paths of this CPU move it: rejected.
        var waypoints = NinoSerializer.Serialize(Enumerable.Range(0, 100).Select(i => new Waypoint { Stamp = (i, 1) }).ToArray());
        foreach (int at in new[] { 0, 37, 99 })
        {
            var bad = (byte[])waypoints.Clone();
            bad[4 + at * 11 + 10] = 2;
            bool rejected;
            try { NinoDeserializer.Deserialize<Waypoint[]>(bad); rejected = false; }
            catch (NinoDataException) { rejected = true; }
            if (!rejected) _failed++;
            Console.WriteLine($"Waypoint[100] bool {at,-6} {(rejected ? "rejected" : "ACCEPTED")}");
        }
        Console.WriteLine(_failed == 0 ? "ALL OK" : _failed + " FAILED");
        return _failed;
    }
}
