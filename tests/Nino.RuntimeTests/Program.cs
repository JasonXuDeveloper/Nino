using System;
using System.Collections.Concurrent;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Reflection;
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

#if NET8_0_OR_GREATER
// Int128 is aligned to 16 on 64-bit .NET 8+ and to 8 on Arm32: written in the canonical layout (16) everywhere.
[NinoType]
public struct WideAfterByte { public byte A; public Int128 B; }
#endif

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
/// (<c>CollectionViews</c>, <c>HashTables</c>, <c>ConcurrentTables</c>, <c>SortedSetNodes</c>, Nino.Immutable's
/// <c>HashTrees</c> and <c>SortedTrees</c>), printed as one line per case: name, size, hash of the bytes, and "ok"
/// when writing what was read gives the same bytes and the public API sees the same contents (also when read into the
/// instance read before). Each of those paths must have been taken: a runtime where one fell back to the public API
/// fails (<see cref="FastPaths"/>).
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
            // What was read holds what was written, seen through the public API (enumerators, Count): a collection
            // built through its fields is checked by the runtime's own code, not by Nino's writer.
            string contents = Contents(value);
            same &= Contents(back) == contents;
            // Read into an existing instance too (reuse paths).
            T into = back;
            NinoDeserializer.Deserialize(bytes, ref into);
            same &= bytes.SequenceEqual(NinoSerializer.Serialize(into)) && Contents(into) == contents;
            string hash = Fnv64(bytes).ToString("X16");
            if (!same) _failed++;
            Console.WriteLine($"{name,-24} {bytes.Length,6} {hash} {(same ? "ok" : "MISMATCH")}");
        }
        catch (Exception e)
        {
            _failed++;
            Console.WriteLine($"{name,-24} EXCEPTION {e.GetType().Name}: {e.Message}");
        }
    }

    /// <summary>
    /// FNV-1a over <paramref name="bytes"/>: compares what each runtime wrote, in managed code (SHA256 needs OpenSSL,
    /// which the Arm32 job's qemu sysroot lacks).
    /// </summary>
    private static ulong Fnv64(byte[] bytes)
    {
        ulong h = 14695981039346656037;
        foreach (byte b in bytes)
            h = (h ^ b) * 1099511628211;
        return h;
    }

    /// <summary>
    /// What <paramref name="value"/> holds, through the public API only: items in enumeration order (sorted for the
    /// hash collections, whose order depends on their capacity), each collection's Count, fields and properties.
    /// </summary>
    private static string Contents(object value)
    {
        switch (value)
        {
            case null:
                return "null";
            case string s:
                return "\"" + s + "\"";
            case DateTime d:
                return d.Ticks + "/" + d.Kind;
            case DateTimeOffset o:
                return o.Ticks + "/" + o.Offset.Ticks;
            case IFormattable f:
                return f.ToString(null, CultureInfo.InvariantCulture);
            case IConvertible c:
                return c.ToString(CultureInfo.InvariantCulture);
            case IEnumerable e:
                var items = new List<string>();
                foreach (var item in e)
                    items.Add(Contents(item));
                var type = value.GetType();
                if (new[] { "HashSet`", "Dictionary`", "ConcurrentDictionary`", "ImmutableHashSet`", "ImmutableDictionary`" }
                    .Any(n => type.Name.StartsWith(n, StringComparison.Ordinal)))
                    items.Sort(StringComparer.Ordinal);
                var count = type.GetProperty("Count", BindingFlags.Public | BindingFlags.Instance);
                return "[" + string.Join(",", items) + "]" +
                       (count != null && count.GetIndexParameters().Length == 0 ? "#" + count.GetValue(value) : "");
        }

        var t = value.GetType();
        return "{" + string.Join(",", t.GetFields(BindingFlags.Public | BindingFlags.Instance)
                   .Select(f => f.Name + "=" + Contents(f.GetValue(value)))
                   .Concat(t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                       .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
                       .Select(p => p.Name + "=" + Contents(p.GetValue(value))))) + "}";
    }

    /// <summary>
    /// The paths through a runtime's own internals, each found (or not) at startup; each must be found on every
    /// runtime this program runs on (.NET 64-bit and 32-bit, .NET Framework 64-bit and 32-bit, Mono): one that is not
    /// means Nino silently uses the public API there, and its layouts for that runtime are wrong or missing.
    /// </summary>
    private static readonly (Assembly Assembly, string Type, string Field)[] FastPaths =
    {
        (typeof(NinoSerializer).Assembly, "Nino.Core.Internal.ObjectTypes", "Usable"),
        (typeof(NinoSerializer).Assembly, "Nino.Core.Internal.CollectionViews", "List"),
        (typeof(NinoSerializer).Assembly, "Nino.Core.Internal.CollectionViews", "Stack"),
        (typeof(NinoSerializer).Assembly, "Nino.Core.Internal.CollectionViews", "Queue"),
        (typeof(NinoSerializer).Assembly, "Nino.Core.Internal.CollectionViews", "SortedList"),
        (typeof(NinoSerializer).Assembly, "Nino.Core.Internal.CollectionViews", "SortedSet"),
        (typeof(NinoSerializer).Assembly, "Nino.Core.Internal.CollectionViews", "SortedDictionary"),
        (typeof(NinoSerializer).Assembly, "Nino.Core.Internal.CollectionViews", "LinkedList"),
        (typeof(NinoSerializer).Assembly, "Nino.Core.Internal.CollectionViews", "HashSet"),
        (typeof(NinoSerializer).Assembly, "Nino.Core.Internal.CollectionViews", "Dictionary"),
        (typeof(NinoSerializer).Assembly, "Nino.Core.Internal.SortedSetNodes", "Layout"),
        (typeof(NinoSerializer).Assembly, "Nino.Core.Internal.HashTables", "Set"),
        (typeof(NinoSerializer).Assembly, "Nino.Core.Internal.HashTables", "Map"),
        (typeof(NinoSerializer).Assembly, "Nino.Core.Internal.HashTables", "SetReferenceChains"),
        (typeof(NinoSerializer).Assembly, "Nino.Core.Internal.HashTables", "MapReferenceChains"),
        (typeof(NinoSerializer).Assembly, "Nino.Core.Internal.HashTables", "SetReferences"),
        (typeof(NinoSerializer).Assembly, "Nino.Core.Internal.HashTables", "MapReferences"),
        (typeof(NinoSerializer).Assembly, "Nino.Core.Internal.HashTables", "SetStrings"),
        (typeof(NinoSerializer).Assembly, "Nino.Core.Internal.HashTables", "MapStrings"),
        (typeof(NinoSerializer).Assembly, "Nino.Core.Internal.ConcurrentTables", "Chains"),
        (typeof(NinoSerializer).Assembly, "Nino.Core.Internal.ConcurrentTables", "ReferenceChains"),
        (typeof(NinoSerializer).Assembly, "Nino.Core.Internal.ConcurrentTables", "Builds"),
        (typeof(Nino.Immutable.ImmutableArrayFormatter<,>).Assembly, "Nino.Immutable.HashTrees", "Usable"),
        (typeof(Nino.Immutable.ImmutableArrayFormatter<,>).Assembly, "Nino.Immutable.SortedTrees", "Usable"),
    };

    /// <summary>
    /// Whether the path <see cref="FastPaths"/> names <paramref name="field"/> is meant to be taken on this runtime. Not
    /// a HashSet filled directly from the netstandard2.0 build of Nino.Core (no HashSet.EnsureCapacity to size a new
    /// set with); not string keys on .NET 5+, whose tables hash strings with a comparer of their own.
    /// </summary>
    private static bool Expected(string field)
    {
        var target = typeof(NinoSerializer).Assembly
            .GetCustomAttributes(typeof(System.Runtime.Versioning.TargetFrameworkAttribute), false)
            .Cast<System.Runtime.Versioning.TargetFrameworkAttribute>().FirstOrDefault()?.FrameworkName ?? "";
        if ((field == "Set" || field == "SetReferences" || field == "SetStrings") &&
            target == ".NETStandard,Version=v2.0")
            return false;
        string runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription;
        bool core = runtime.StartsWith(".NET ", StringComparison.Ordinal) &&
                    !runtime.StartsWith(".NET Framework", StringComparison.Ordinal);
        return !(field.EndsWith("Strings", StringComparison.Ordinal) && core);
    }

    /// <summary>
    /// Builds <paramref name="source"/>'s bytes into ten instances with a compacting collection after each (objects made
    /// through a runtime's internals must be what the GC expects), checks each through the public API, then changes
    /// the first through it (<paramref name="change"/>, which says whether it saw what it expected) and checks it after
    /// another collection.
    /// </summary>
    private static void Built<T>(string name, T source, Func<T, bool> change)
    {
        bool ok;
        try
        {
            var data = NinoSerializer.Serialize(source);
            var built = new List<T>();
            for (int i = 0; i < 10; i++)
            {
                built.Add(NinoDeserializer.Deserialize<T>(data));
                GC.Collect(2, GCCollectionMode.Forced, true, true);
            }

            string contents = Contents(source);
            ok = built.All(b => Contents(b) == contents) && change(built[0]);
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            ok &= built.Skip(1).All(b => Contents(b) == contents);
        }
        catch (Exception e)
        {
            Console.WriteLine($"{name} EXCEPTION {e.GetType().Name}: {e.Message}");
            ok = false;
        }

        if (!ok) _failed++;
        Console.WriteLine($"{name,-24} {(ok ? "ok" : "WRONG")}");
    }

    public static int Main()
    {
        var views = typeof(NinoSerializer).Assembly.GetType("Nino.Core.Internal.CollectionViews");
        // Informational, not compared: where this runtime keeps the fields.
        if (views != null)
            Console.WriteLine("# views: " + string.Join(" ", views.GetFields(BindingFlags.Public | BindingFlags.Static)
                .Select(f => f.Name + "=" + f.GetValue(null))));
        var concurrent = typeof(NinoSerializer).Assembly.GetType("Nino.Core.Internal.ConcurrentTables");
        if (concurrent != null)
            Console.WriteLine("# concurrent: " + string.Join(" ", concurrent
                .GetFields(BindingFlags.NonPublic | BindingFlags.Static)
                .Where(f => f.FieldType == typeof(bool) || f.FieldType == typeof(int))
                .Select(f => f.Name + "=" + f.GetValue(null))));
        Console.WriteLine("# 64-bit: " + Environment.Is64BitProcess + ", runtime: " +
                          System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
        // Compared: the same line on every runtime when every path was found that is meant to be found there.
        var missing = new List<string>();
        var paths = new List<string>();
        foreach (var (assembly, typeName, fieldName) in FastPaths)
        {
            if (!Expected(fieldName))
                continue;
            var field = assembly.GetType(typeName)?.GetField(fieldName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            object found = field?.GetValue(null);
            paths.Add(typeName.Substring(typeName.LastIndexOf('.') + 1) + "." + fieldName + "=" + found);
            if (!(found is true || found is int layout && layout != 0))
                missing.Add(typeName.Substring(typeName.LastIndexOf('.') + 1) + "." + fieldName + "=" +
                            (found ?? "(no such field)"));
        }

        Console.WriteLine("# paths: " + string.Join(" ", paths));
        if (missing.Count != 0) _failed++;
        Console.WriteLine(missing.Count == 0 ? "fast paths found" : "FAST PATHS MISSING: " + string.Join(" ", missing));

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
        // Keys built to share a hash bucket: up to NinoSettings.MaxHashCollisions others per key are read, more are
        // rejected, where this runtime's table layout was found (here: .NET 8+ and Mono).
        foreach (int count in new[] { 101, 102 })
        {
            var colliding = new ConcurrentDictionary<long, int>(Enumerable.Range(1, count)
                .ToDictionary(i => (long)i << 32 | (uint)i, i => i));
            var data = NinoSerializer.Serialize(colliding);
            string outcome;
            try { outcome = NinoDeserializer.Deserialize<ConcurrentDictionary<long, int>>(data).Count == count ? "read" : "WRONG"; }
            catch (NinoDataException) { outcome = "rejected"; }
            if (outcome != (count == 101 ? "read" : "rejected")) _failed++;
            Console.WriteLine($"ConcurrentDict flood {count,-4} {outcome}");
        }
        // Built from its nodes where this runtime's layout was found: many of them, through compacting collections,
        // then used through the public API (removes, adds that grow the table).
        {
            var source = new ConcurrentDictionary<long, string>(Enumerable.Range(0, 3000)
                .ToDictionary(i => (long)i * 31, i => "v" + i));
            var data = NinoSerializer.Serialize(source);
            var built = new List<ConcurrentDictionary<long, string>>();
            for (int i = 0; i < 10; i++)
            {
                built.Add(NinoDeserializer.Deserialize<ConcurrentDictionary<long, string>>(data));
                GC.Collect(2, GCCollectionMode.Forced, true, true);
            }

            bool same = built.All(d => d.Count == source.Count && source.All(p => d.TryGetValue(p.Key, out var v) && v == p.Value));
            var first = built[0];
            for (long i = 0; i < 3000; i += 2)
                same &= first.TryRemove(i * 31, out _);
            for (int i = 0; i < 20_000; i++)
                same &= first.TryAdd(-i - 1, "n" + i);
            GC.Collect(2, GCCollectionMode.Forced, true, true);
            same &= first.Count == 21_500 && first[-20_000] == "n19999" && first[31] == "v1" && !first.ContainsKey(62);
            if (!same) _failed++;
            Console.WriteLine($"ConcurrentDict built 3000 {(same ? "ok" : "WRONG")}");
        }
        // Nino.Immutable: the hash and sorted collections are built as their trees (HashTrees, SortedTrees).
        var distinct = ints.Distinct().ToArray();
        Case("ImmutableArray<int>", ImmutableArray.Create(ints));
        Case("ImmutableList<string>", ImmutableList.CreateRange(strs));
        Case("ImmutableHashSet<int>", ImmutableHashSet.CreateRange(ints));
        Case("ImmutableDict<int,str>", distinct.ToImmutableDictionary(i => i, i => "v" + i));
        // String keys only in the built cases below: their hash order (so the bytes) differs between processes.
        Case("ImmSortedSet<int>", ImmutableSortedSet.CreateRange(ints));
        Case("ImmSortedSet<string>", ImmutableSortedSet.CreateRange(StringComparer.Ordinal, strs.Where(s => s != null)));
        Case("ImmSortedDict<int,long>", distinct.ToImmutableSortedDictionary(i => i, i => (long)i << 20));
        Case("ImmutableQueue<int>", ImmutableQueue.CreateRange(ints));
        Case("ImmutableStack<int>", ImmutableStack.CreateRange(ints));

        // Built from the data through a runtime's internals, then used through the public API.
        Built("SortedSet built", new SortedSet<int>(Enumerable.Range(0, 2000).Select(i => i * 3)), set =>
        {
            bool seen = set.Count == 2000 && set.Min == 0 && set.Max == 5997 && set.Contains(300) && !set.Contains(301);
            for (int i = 0; i < 6000; i += 6)
                seen &= set.Remove(i);
            for (int i = 1; i < 3000; i += 3)
                seen &= set.Add(i);
            return seen && set.Count == 2000 && set.GetViewBetween(0, 99).Count == 50 && set.Min == 1;
        });
        Built("SortedSet<str> built", new SortedSet<string>(Enumerable.Range(0, 500).Select(i => "s" + i)), set =>
            set.Remove("s7") && set.Add("s7x") && set.Count == 500 && set.Contains("s499") && !set.Contains("s7"));
        Built("SortedDict built", new SortedDictionary<int, string>(distinct.ToDictionary(i => i, i => "v" + i)), map =>
            map.Remove(distinct[0]) && map.Count == distinct.Length - 1 && map[distinct[1]] == "v" + distinct[1] &&
            map.Keys.SequenceEqual(map.Keys.OrderBy(k => k)));
        Built("LinkedList built", new LinkedList<string>(Enumerable.Range(0, 1000).Select(i => "n" + i)), list =>
        {
            list.AddFirst("first");
            list.Remove("n500");
            list.AddLast("last");
            return list.Count == 1001 && list.First.Value == "first" && list.Last.Value == "last" &&
                   list.Last.Previous.Value == "n999" && list.First.Next.Value == "n0" && list.Find("n500") == null &&
                   list.Reverse().Count() == 1001;
        });
        Built("HashSet<str> built", new HashSet<string>(Enumerable.Range(0, 3000).Select(i => "h" + i)), set =>
            set.Remove("h3") && set.Add("x") && !set.Add("h4") && set.Count == 3000 && set.Contains("h2999") &&
            !set.Contains("h3"));
        Built("Dictionary built", Enumerable.Range(0, 3000).ToDictionary(i => (long)i * 31, i => "v" + i), map =>
        {
            for (long i = 0; i < 3000; i += 2)
                map.Remove(i * 31);
            for (int i = 0; i < 5000; i++)
                map.Add(-i - 1, "n" + i);
            return map.Count == 6500 && map[31] == "v1" && !map.ContainsKey(62) && map[-5000] == "n4999";
        });
        Built("ImmutableDict built", distinct.ToImmutableDictionary(i => "k" + i, i => (long)i), map =>
        {
            var changed = map.Remove("k" + distinct[0]).Add("new", 1).SetItem("k" + distinct[1], -1);
            return changed.Count == map.Count && changed["new"] == 1 && changed["k" + distinct[1]] == -1 &&
                   !changed.ContainsKey("k" + distinct[0]) && map["k" + distinct[1]] == distinct[1];
        });
        Built("ImmutableHashSet built", ImmutableHashSet.CreateRange(Enumerable.Range(0, 2000).Select(i => (long)i << 33)),
            set => set.Remove(0).Add(1).Contains(1) && !set.Remove(0).Contains(0) && set.Contains(1999L << 33) &&
                   set.Union(new[] { 5L }).Count == 2001);
        Built("ImmSortedDict built", distinct.ToImmutableSortedDictionary(i => i, i => "v" + i), map =>
        {
            var changed = map.Remove(distinct.Min()).Add(int.MaxValue, "max");
            return changed.Count == map.Count && changed.Keys.Last() == int.MaxValue &&
                   changed.Keys.First() > distinct.Min() && map.Keys.SequenceEqual(map.Keys.OrderBy(k => k));
        });
        Built("ImmSortedSet built", ImmutableSortedSet.CreateRange(Enumerable.Range(0, 2000).Select(i => "s" + i.ToString("D5"))),
            set => set.Remove("s00000").Add("t").Max == "t" && set.Min == "s00000" && set.IndexOf("s01000") == 1000);

#if NET8_0_OR_GREATER
        // Int128 after a byte: the canonical layout (16-byte alignment) whatever this runtime aligns it to, padding
        // written as zeros. Not compared (the net472 build has no Int128): checked against the expected bytes.
        {
            var value = new WideAfterByte { A = 1, B = -2 };
            var expected = new byte[32];
            expected[0] = 1;
            expected[16] = 0xFE;
            for (int i = 17; i < 32; i++)
                expected[i] = 0xFF;
            var wideAlign = typeof(NinoSerializer).Assembly.GetType("Nino.Core.Internal.NinoLayout")
                ?.GetField("WideAlign", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null);
            var array = NinoSerializer.Serialize(new[] { value, value });
            bool ok = NinoSerializer.Serialize(value).SequenceEqual(expected) &&
                      NinoDeserializer.Deserialize<WideAfterByte>(expected).B == -2 &&
                      array.Skip(4).SequenceEqual(expected.Concat(expected)) &&
                      NinoDeserializer.Deserialize<WideAfterByte[]>(array)[1].B == -2;
            if (!ok) _failed++;
            Console.WriteLine($"# Int128 after a byte (aligned to {wideAlign} here): {(ok ? "ok" : "WRONG BYTES")}");
        }
#endif
        Console.WriteLine(_failed == 0 ? "ALL OK" : _failed + " FAILED");
        return _failed;
    }
}
