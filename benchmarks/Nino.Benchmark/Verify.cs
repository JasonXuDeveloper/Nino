using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using BenchmarkDotNet.Attributes;

#nullable disable
namespace Nino.Benchmark;

/// <summary>
/// Correctness check of the benchmarks themselves (`--verify`, run before the benchmarks): a case that reads wrong
/// bytes, or writes another value, would still be timed. For every benchmark class it runs the global setup, then
/// every case twice (the second call on reused buffers and targets), and checks that
///  - each serialize case writes as many bytes as its Payload column says (the byte[] API's length);
///  - every case of a category that returns a value returns one deep-equal to the baseline's: Nino, MemoryPack,
///    MessagePack and protobuf-net read back the same data, new or in place.
/// </summary>
public static class Verify
{
    private static int _checks, _failures;

    public static int Run()
    {
        var classes = typeof(Verify).Assembly.GetTypes()
            .Where(t => t.GetMethods().Any(m => m.IsDefined(typeof(BenchmarkAttribute))))
            .OrderBy(t => t.Name);
        foreach (var type in classes)
        {
            var bench = Activator.CreateInstance(type);
            foreach (var setup in type.GetMethods().Where(m => m.IsDefined(typeof(GlobalSetupAttribute))))
                setup.Invoke(bench, null);

            var categories = type.GetMethods()
                .Where(m => m.IsDefined(typeof(BenchmarkAttribute)))
                .GroupBy(m => string.Join(",", m.GetCustomAttribute<BenchmarkCategoryAttribute>()?.Categories ?? []));
            foreach (var category in categories)
                VerifyCategory(type, bench, category.OrderByDescending(m => m.GetCustomAttribute<BenchmarkAttribute>().Baseline));
        }

        Console.WriteLine(_failures == 0 ? $"VERIFY OK ({_checks} checks)" : $"VERIFY FAILED ({_failures} of {_checks} checks)");
        return _failures == 0 ? 0 : 1;
    }

    private static void VerifyCategory(Type type, object bench, IEnumerable<MethodInfo> methods)
    {
        object expected = null;
        string expectedFrom = null;
        foreach (var method in methods)
        {
            var name = $"{type.Name}.{method.Name}";
            object result;
            try
            {
                method.Invoke(bench, null);
                result = method.Invoke(bench, null);
            }
            catch (TargetInvocationException e)
            {
                Check(name + " throws " + e.InnerException?.GetType().Name + ": " + e.InnerException?.Message, false);
                continue;
            }

            if (BenchmarkPayloadRegistry.TryGet(method.Name, out var size))
            {
                // Serialize cases return the bytes written (int) or the bytes (byte[]).
                var written = result switch { int n => n, byte[] bytes => bytes.Length, _ => -1 };
                Check($"{name} writes {written} bytes, its payload is {size}", written == size);
                continue;
            }

            // Pooled cases return a member of a value already back in the pool.
            if (method.ReturnType == typeof(int)) continue;
            if (expectedFrom == null)
            {
                (expected, expectedFrom) = (result, method.Name);
                Check(name + " returns a value", result != null);
                continue;
            }

            Check($"{name} reads what {expectedFrom} reads", Same(expected, result, new HashSet<(object, object)>()));
        }
    }

    private static void Check(string what, bool ok)
    {
        _checks++;
        if (ok) return;
        _failures++;
        Console.WriteLine("FAIL " + what);
    }

    private static bool Same(object a, object b, HashSet<(object, object)> seen)
    {
        if (a == null || b == null) return a == null && b == null;
        var type = a.GetType();
        if (!type.IsValueType && !seen.Add((a, b))) return true; // a cycle: compared on the way in

        switch (a)
        {
            // MessagePack reads a local DateTime back in UTC: the same instant. It writes an unspecified one as it is,
            // as UTC: the same clock time.
            case DateTime t:
                return b is DateTime u && (t.ToUniversalTime() == u.ToUniversalTime() ||
                                           t.Kind == DateTimeKind.Unspecified && t.Ticks == u.Ticks);
            case StringBuilder s: return b is StringBuilder c && s.ToString() == c.ToString();
            case string or Uri or Version: return a.Equals(b);
        }

        var unwrapped = Unwrap(a);
        if (unwrapped != null) return Same(unwrapped, Unwrap(b), seen);
        if (Grouping(type) is { } key && !Same(key.GetValue(a), Grouping(b.GetType())?.GetValue(b), seen)) return false;
        if (a is IDictionary da)
            return b is IDictionary db && da.Count == db.Count &&
                   da.Keys.Cast<object>().All(k => db.Contains(k) && Same(da[k], db[k], seen));
        if (a is IEnumerable ea)
        {
            if (b is not IEnumerable eb) return false;
            var la = ea.Cast<object>().ToList();
            var lb = eb.Cast<object>().ToList();
            return la.Count == lb.Count && la.Zip(lb).All(p => Same(p.First, p.Second, seen));
        }

        if (type != b.GetType()) return false;
        // Tuples field by field: their Equals compares a class member by reference.
        if (type.IsPrimitive || type.IsEnum || OverridesEquals(type) && a is not ITuple) return a.Equals(b);
        for (var t = type; t != null && t != typeof(object) && t != typeof(ValueType); t = t.BaseType)
        {
            foreach (var field in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic |
                                              BindingFlags.DeclaredOnly))
                if (!Same(field.GetValue(a), field.GetValue(b), seen))
                    return false;
        }

        return true;
    }

    /// <summary>The elements of a memory or a sequence, the value of a <c>Lazy</c>: null for anything else.</summary>
    private static object Unwrap(object value)
    {
        var type = value.GetType();
        if (!type.IsGenericType) return null;
        var definition = type.GetGenericTypeDefinition();
        if (definition == typeof(Lazy<>)) return type.GetProperty("Value")!.GetValue(value);
        if (definition == typeof(Memory<>) || definition == typeof(ReadOnlyMemory<>))
            return type.GetMethod("ToArray")!.Invoke(value, null);
        if (definition == typeof(ReadOnlySequence<>))
            return typeof(BuffersExtensions).GetMethod(nameof(BuffersExtensions.ToArray))!
                .MakeGenericMethod(type.GetGenericArguments()).Invoke(null, [value]);
        return null;
    }

    private static PropertyInfo Grouping(Type type) => type.GetInterfaces()
        .FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IGrouping<,>))
        ?.GetProperty("Key");

    private static bool OverridesEquals(Type type)
    {
        var equals = type.GetMethod(nameof(Equals), [typeof(object)]);
        return equals != null && equals.DeclaringType != typeof(object) && equals.DeclaringType != typeof(ValueType) &&
               !equals.DeclaringType!.IsDefined(typeof(CompilerGeneratedAttribute));
    }
}
