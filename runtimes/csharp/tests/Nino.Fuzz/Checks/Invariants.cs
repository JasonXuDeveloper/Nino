using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;

namespace Nino.Fuzz
{
    /// <summary>A value Nino returned that breaks an invariant of its type.</summary>
    public sealed class InvariantException : Exception
    {
        public InvariantException(string message) : base(message) { }
    }

    /// <summary>
    /// Checks what a read returned through public APIs only: every collection in the graph enumerates as many items
    /// as its Count, finds each of them again by lookup, and the sorted ones are in order. A collection Nino built
    /// from its internals (CollectionViews, SortedSetNodes, HashTrees, ReadLinked) breaks one of these when the
    /// data it was built from was inconsistent and Nino did not reject it.
    /// </summary>
    public static class Invariants
    {
        const int MaxDepth = 4000;
        const int MaxItems = 10_000_000;

        public static void Check(object root)
        {
            var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
            var stack = new Stack<(object, int)>();
            stack.Push((root, 0));
            while (stack.Count > 0)
            {
                var (o, depth) = stack.Pop();
                if (o == null || depth > MaxDepth || !seen.Add(o)) continue;
                var t = o.GetType();
                if (t.IsPrimitive || t.IsEnum || o is string) continue;
                if (IsDefaultStruct(o)) continue; // default(ArraySegment<T>) and default(ImmutableArray<T>) are valid but cannot enumerate.
                Collection(o);
                if (o is IEnumerable e)
                {
                    int n = 0;
                    foreach (var x in e)
                    {
                        // Unmanaged items hold no collection: not walked (11,000 tuples took 25 ms a read).
                        if (x != null && TypeLayout.HoldsReferences(x.GetType())) stack.Push((x, depth + 1));
                        if (++n > MaxItems) Fail(t, "enumeration does not end");
                    }
                }
                if (IsOwnType(t) || IsTupleLike(t))
                {
                    foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                    {
                        var v = f.GetValue(o);
                        if (v != null) stack.Push((v, depth + 1));
                    }
                }
                if (t.IsGenericType)
                {
                    var def = t.GetGenericTypeDefinition();
                    if (def == typeof(KeyValuePair<,>))
                    {
                        stack.Push((t.GetProperty("Key").GetValue(o), depth + 1));
                        stack.Push((t.GetProperty("Value").GetValue(o), depth + 1));
                    }
                    else if (def == typeof(Lazy<>))
                    {
                        stack.Push((t.GetProperty("Value").GetValue(o), depth + 1));
                    }
                    else if (t.FullName.StartsWith("System.Tuple`", StringComparison.Ordinal))
                    {
                        foreach (var p in t.GetProperties()) if (p.Name.StartsWith("Item", StringComparison.Ordinal)) stack.Push((p.GetValue(o), depth + 1));
                    }
                }
            }
        }

        /// <summary>
        /// A text that two equal values share: members in order, sequences in order, and the items of unordered
        /// collections (hash sets and dictionaries, whose enumeration order depends on their history) sorted. Objects
        /// met again (cycles, shared references) are written as a back reference.
        /// </summary>
        public static string Fingerprint(object root)
        {
            var sb = new System.Text.StringBuilder();
            Print(root, sb, new HashSet<object>(ReferenceEqualityComparer.Instance), 0);
            return sb.ToString();
        }

        static void Print(object o, System.Text.StringBuilder sb, HashSet<object> seen, int depth)
        {
            if (o == null) { sb.Append("null"); return; }
            var t = o.GetType();
            if (o is string str) { sb.Append('"').Append(str.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"'); return; }
            if (t.IsPrimitive || t.IsEnum || o is decimal) { sb.Append(Convert.ToString(o, System.Globalization.CultureInfo.InvariantCulture)); return; }
            if (depth > MaxDepth) { sb.Append("..."); return; }
            if (!t.IsValueType && !seen.Add(o)) { sb.Append("<ref ").Append(Name(t)).Append('>'); return; }
            if (IsDefaultStruct(o)) { sb.Append("default"); return; }
            sb.Append(Name(t));
            if (o is IEnumerable e)
            {
                var items = new List<string>();
                foreach (var x in e)
                {
                    var item = new System.Text.StringBuilder();
                    Print(x, item, seen, depth + 1);
                    items.Add(item.ToString());
                }
                if (IsUnordered(t)) items.Sort(StringComparer.Ordinal);
                sb.Append('[').Append(string.Join(",", items)).Append(']');
                return;
            }
            if (IsOwnType(t) || IsTupleLike(t) || t.IsGenericType && t.GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
            {
                sb.Append('{');
                foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    sb.Append(f.Name).Append('=');
                    Print(f.GetValue(o), sb, seen, depth + 1);
                    sb.Append(';');
                }
                sb.Append('}');
                return;
            }
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Lazy<>))
            {
                sb.Append('(');
                Print(t.GetProperty("Value").GetValue(o), sb, seen, depth + 1);
                sb.Append(')');
                return;
            }
            sb.Append('(').Append(Convert.ToString(o, System.Globalization.CultureInfo.InvariantCulture)).Append(')');
        }

        static bool IsUnordered(Type t)
        {
            if (t.Name.StartsWith("Sorted", StringComparison.Ordinal) || t.Name.StartsWith("ImmutableSorted", StringComparison.Ordinal)) return false;
            if (typeof(IDictionary).IsAssignableFrom(t)) return true;
            return t.GetInterfaces().Any(i => i.IsGenericType &&
                (i.GetGenericTypeDefinition() == typeof(ISet<>) || i.GetGenericTypeDefinition() == typeof(IReadOnlySet<>) ||
                 i.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>))) ||
                t.IsGenericType && t.GetGenericTypeDefinition() == typeof(System.Collections.Concurrent.ConcurrentBag<>);
        }

        static bool IsOwnType(Type t) =>
            t.Assembly == typeof(Invariants).Assembly || t.Assembly == typeof(Golden.GoldenCorpus).Assembly;

        static bool IsTupleLike(Type t) => t.IsValueType && t.FullName != null && t.FullName.StartsWith("System.ValueTuple`", StringComparison.Ordinal);

        static bool IsDefaultStruct(object o)
        {
            var t = o.GetType();
            if (!t.IsValueType || !t.IsGenericType) return false;
            var def = t.GetGenericTypeDefinition();
            if (def == typeof(ArraySegment<>)) return t.GetProperty("Array").GetValue(o) == null;
            if (def == typeof(ImmutableArray<>)) return (bool)t.GetProperty("IsDefault").GetValue(o);
            return false;
        }

        static void Fail(Type t, string message) => throw new InvariantException(Name(t) + ": " + message);
        static void Fail(object o, string message) => Fail(o.GetType(), message);

        static string Name(Type t) => t.IsGenericType ? t.Name.Split('`')[0] + "<" + string.Join(",", t.GetGenericArguments().Select(Name)) + ">" : t.Name;

        static readonly Dictionary<string, MethodInfo> Checks = typeof(Invariants)
            .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Where(m => m.IsGenericMethodDefinition && m.Name.StartsWith("Check", StringComparison.Ordinal))
            .ToDictionary(m => m.Name.Substring(5));

        static void Collection(object o)
        {
            if (o is ICollection c)
            {
                int n = 0;
                foreach (var _ in c) if (++n > MaxItems) break;
                if (n != c.Count) Fail(o, $"Count {c.Count} but {n} enumerated");
            }
            var t = o.GetType();
            if (!t.IsGenericType) return;
            if (Checks.TryGetValue(t.GetGenericTypeDefinition().Name.Split('`')[0], out var m) &&
                m.GetGenericArguments().Length == t.GetGenericArguments().Length)
            {
                try { m.MakeGenericMethod(t.GetGenericArguments()).Invoke(null, new[] { o }); }
                catch (TargetInvocationException tie) when (tie.InnerException != null)
                {
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
                }
            }
        }

        static void CheckList<T>(List<T> l)
        {
            int n = 0;
            foreach (var _ in l) n++;
            if (n != l.Count || l.Capacity < l.Count) Fail(l, $"Count {l.Count} Capacity {l.Capacity} enumerated {n}");
            if (l.ToArray().Length != l.Count) Fail(l, "ToArray length");
        }

        static void CheckHashSet<T>(HashSet<T> s)
        {
            int n = 0;
            foreach (var x in s) { n++; if (!s.Contains(x)) Fail(s, "item not found by Contains: " + x); }
            if (n != s.Count) Fail(s, "count");
            if (new HashSet<T>(s, s.Comparer).Count != s.Count) Fail(s, "duplicate items");
        }

        static void CheckDictionary<K, V>(Dictionary<K, V> d)
        {
            int n = 0;
            foreach (var kv in d)
            {
                n++;
                if (!d.TryGetValue(kv.Key, out var v) || !EqualityComparer<V>.Default.Equals(v, kv.Value)) Fail(d, "key not found by lookup: " + kv.Key);
            }
            if (n != d.Count) Fail(d, "count");
            if (new Dictionary<K, V>(d, d.Comparer).Count != d.Count) Fail(d, "copy count");
        }

        static void CheckSortedSet<T>(SortedSet<T> s)
        {
            T prev = default; bool first = true; int n = 0;
            foreach (var x in s)
            {
                if (!first && s.Comparer.Compare(prev, x) >= 0) Fail(s, "out of order");
                if (!s.Contains(x)) Fail(s, "item not found by Contains");
                prev = x; first = false; n++;
            }
            if (n != s.Count) Fail(s, "count");
            int back = 0;
            foreach (var _ in s.Reverse()) back++;
            if (back != n) Fail(s, "reverse count");
            if (n > 0)
            {
                var view = s.GetViewBetween(s.Min, s.Max);
                if (view.Count != n) Fail(s, "view count");
                var copy = new SortedSet<T>(s, s.Comparer);
                copy.Add(s.Max); copy.Remove(s.Min);
                if (copy.Count != n - 1) Fail(s, "remove after copy");
            }
        }

        static void CheckSortedDictionary<K, V>(SortedDictionary<K, V> d)
        {
            K prev = default; bool first = true; int n = 0;
            foreach (var kv in d)
            {
                if (!first && d.Comparer.Compare(prev, kv.Key) >= 0) Fail(d, "out of order");
                if (!d.ContainsKey(kv.Key)) Fail(d, "key not found");
                prev = kv.Key; first = false; n++;
            }
            if (n != d.Count) Fail(d, "count");
        }

        static void CheckSortedList<K, V>(SortedList<K, V> d)
        {
            for (int i = 1; i < d.Count; i++) if (d.Comparer.Compare(d.Keys[i - 1], d.Keys[i]) >= 0) Fail(d, "out of order");
            foreach (var k in d.Keys) if (d.IndexOfKey(k) < 0) Fail(d, "key not found");
            if (d.Capacity < d.Count) Fail(d, "capacity");
        }

        static void CheckLinkedList<T>(LinkedList<T> l)
        {
            int n = 0;
            for (var x = l.First; x != null; x = x.Next)
            {
                n++;
                if (x.List != l) Fail(l, "node of another list");
                if (n > l.Count + 1) Fail(l, "forward loop");
            }
            int b = 0;
            for (var x = l.Last; x != null; x = x.Previous) if (++b > l.Count + 1) Fail(l, "backward loop");
            if (n != l.Count || b != l.Count) Fail(l, $"Count {l.Count} forward {n} backward {b}");
        }

        static void CheckQueue<T>(Queue<T> q) { if (q.ToArray().Length != q.Count) Fail(q, "ToArray length"); }
        static void CheckStack<T>(Stack<T> q) { if (q.ToArray().Length != q.Count) Fail(q, "ToArray length"); }

        static void CheckImmutableDictionary<K, V>(ImmutableDictionary<K, V> d)
        {
            int n = 0;
            foreach (var kv in d) { n++; if (!d.TryGetValue(kv.Key, out _)) Fail(d, "key not found"); }
            if (n != d.Count) Fail(d, "count");
            if (d.ToBuilder().Count != d.Count) Fail(d, "builder count");
            if (n > 0)
            {
                var k = d.Keys.First();
                var r = d.Remove(k);
                if (r.Count != n - 1 || r.ContainsKey(k)) Fail(d, "remove");
            }
        }

        static void CheckImmutableHashSet<T>(ImmutableHashSet<T> s)
        {
            int n = 0;
            foreach (var x in s) { n++; if (!s.Contains(x)) Fail(s, "item not found"); }
            if (n != s.Count) Fail(s, "count");
            if (n > 0)
            {
                var k = s.First();
                var r = s.Remove(k);
                if (r.Count != n - 1 || r.Contains(k)) Fail(s, "remove");
            }
        }

        static void CheckImmutableSortedSet<T>(ImmutableSortedSet<T> s)
        {
            int n = 0; T prev = default;
            foreach (var x in s)
            {
                if (n > 0 && s.KeyComparer.Compare(prev, x) >= 0) Fail(s, "out of order");
                if (!s.Contains(x)) Fail(s, "item not found");
                if (!EqualityComparer<T>.Default.Equals(s[n], x)) Fail(s, "indexer");
                prev = x; n++;
            }
            if (n != s.Count) Fail(s, "count");
        }

        static void CheckImmutableSortedDictionary<K, V>(ImmutableSortedDictionary<K, V> d)
        {
            int n = 0; K prev = default;
            foreach (var kv in d)
            {
                if (n > 0 && d.KeyComparer.Compare(prev, kv.Key) >= 0) Fail(d, "out of order");
                if (!d.ContainsKey(kv.Key)) Fail(d, "key not found");
                prev = kv.Key; n++;
            }
            if (n != d.Count) Fail(d, "count");
        }

        static void CheckImmutableList<T>(ImmutableList<T> l)
        {
            int n = 0;
            foreach (var x in l) { if (!EqualityComparer<T>.Default.Equals(l[n], x)) Fail(l, "indexer"); n++; }
            if (n != l.Count) Fail(l, "count");
        }
    }
}
