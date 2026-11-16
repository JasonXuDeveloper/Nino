using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using Nino.Core;

/// <summary>A collection whose base type holds Vector&lt;T&gt;: refused like the list it derives from.</summary>
public class VectorList : List<System.Numerics.Vector<int>>
{
}

public enum Tag : byte
{
    A = 1,
    B = 200,
}

/// <summary>
/// Every unmanaged BCL struct Nino copies as raw memory inside a plain struct, and the value tuples and pairs it writes
/// item by item on their own: each runtime must write the bytes 64-bit CoreCLR writes (the canonical layout) and read
/// them back (<see cref="BclFixtures"/>, written by CoreCLR x64). Each also prints how it lays the type out
/// (<c># layout</c> lines, not compared: docs/layouts.md is their table).
/// </summary>
public static class BclStructs
{
    public static int Failed { get; private set; }

    public static void Run(bool writeFixtures)
    {
        var dto1 = new DateTimeOffset(637_000_000_000_000_000, TimeSpan.FromMinutes(-150));
        var dto2 = new DateTimeOffset(2026, 11, 1, 8, 30, 0, TimeSpan.FromHours(14));
        var dt1 = new DateTime(0x0123456789ABCDEF & 0x0FFFFFFFFFFFFFFF, DateTimeKind.Utc);
        var dt2 = new DateTime(2000, 1, 2, 3, 4, 5, DateTimeKind.Local);
        var g1 = new Guid(0x01020304, 0x0506, 0x0708, 9, 10, 11, 12, 13, 14, 15, 16);
        var g2 = new Guid("f0e1d2c3-b4a5-9687-7869-5a4b3c2d1e0f");

        Bcl("(int,long)", (-1, long.MinValue), (2, 0x0102030405060708));
        Bcl("(long,int)", (long.MaxValue, -3), (4L, 5));
        Bcl("(byte,short)", ((byte)1, (short)-2), ((byte)255, short.MaxValue));
        Bcl("(byte,long,int)", ((byte)7, -8L, 9), ((byte)0x80, long.MaxValue, int.MinValue));
        Bcl("(byte,int,short)", ((byte)1, 2, (short)3), ((byte)4, -5, (short)-6));
        Bcl("(short,byte,int)", ((short)1, (byte)2, 3), ((short)-4, (byte)5, -6));
        Bcl("(int,int)", (1, 2), (-3, -4));
        Bcl("(int,int,int)", (1, 2, 3), (-4, -5, -6));
        Bcl("(byte,byte,byte)", ((byte)1, (byte)2, (byte)3), ((byte)4, (byte)5, (byte)6));
        Bcl("(bool,char,byte)", (true, 'x', (byte)3), (false, '中', (byte)0xFE));
        Bcl("(double,float)", (Math.PI, 1.5f), (-0.0, float.NaN));
        Bcl("(Tag,long)", (Tag.A, -1L), (Tag.B, 2L));
        Bcl("(int,IntPtr)", (1, new IntPtr(-2)), (3, new IntPtr(int.MaxValue)));
        Bcl("(byte,UIntPtr)", ((byte)1, new UIntPtr(2)), ((byte)3, new UIntPtr(uint.MaxValue)));
        Bcl("ValueTuple<long>", new ValueTuple<long>(-7), new ValueTuple<long>(8));
        Bcl("(int,DateTime)", (1, dt1), (-2, dt2));
        Bcl("(byte,DateTime,int)", ((byte)1, dt1, 2), ((byte)3, dt2, -4));
        Bcl("(int,TimeSpan)", (1, TimeSpan.FromTicks(-5)), (2, TimeSpan.MaxValue));
        Bcl("(byte,decimal)", ((byte)1, 1.5m), ((byte)2, decimal.MinValue));
        Bcl("(int,decimal)", (1, -79228162514264337593543950335m), (2, 0.0000000000000000000000000001m));
        Bcl("(byte,Guid)", ((byte)1, g1), ((byte)2, g2));
        Bcl("(byte,int?)", ((byte)1, (int?)null), ((byte)2, (int?)-3));
        Bcl("(long?,byte)", ((long?)5, (byte)6), ((long?)null, (byte)7));
        Bcl("(byte,(int,long))", ((byte)1, (2, 3L)), ((byte)4, (-5, -6L)));
        Bcl("((byte,short),long)", (((byte)1, (short)2), 3L), (((byte)4, (short)-5), -6L));
        Bcl("(int,(byte,short),long)", (1, ((byte)2, (short)3), 4L), (-5, ((byte)6, (short)-7), -8L));
        Bcl("(int,KVP<int,long>)", (1, new KeyValuePair<int, long>(2, 3)), (4, new KeyValuePair<int, long>(-5, -6)));
        Bcl("(byte,DateTimeOffset)", ((byte)1, dto1), ((byte)2, dto2));
        Bcl("(byte,short,int,long,byte,short,int)", ((byte)1, (short)2, 3, 4L, (byte)5, (short)6, 7),
            ((byte)8, (short)-9, -10, -11L, (byte)12, (short)-13, -14));
        Bcl("(byte,long,byte,long,byte,long,byte,int)", ((byte)1, 2L, (byte)3, 4L, (byte)5, 6L, (byte)7, 8),
            ((byte)9, -10L, (byte)11, -12L, (byte)13, -14L, (byte)15, -16));
        Bcl("(int,(byte,long),(short,(byte,int)))", (1, ((byte)2, 3L), ((short)4, ((byte)5, 6))),
            (-1, ((byte)7, -8L), ((short)-9, ((byte)10, -11))));
        Bcl("KVP<int,long>", new KeyValuePair<int, long>(1, -2), new KeyValuePair<int, long>(int.MinValue, long.MaxValue));
        Bcl("KVP<byte,long>", new KeyValuePair<byte, long>(1, -2), new KeyValuePair<byte, long>(255, 3));
        Bcl("KVP<long,byte>", new KeyValuePair<long, byte>(-1, 2), new KeyValuePair<long, byte>(3, 255));
        Bcl("KVP<int,DateTime>", new KeyValuePair<int, DateTime>(1, dt1), new KeyValuePair<int, DateTime>(2, dt2));
        Bcl("KVP<byte,(int,long)>", new KeyValuePair<byte, (int, long)>(1, (2, 3)),
            new KeyValuePair<byte, (int, long)>(4, (-5, -6)));
        Bcl("DateTimeOffset", dto1, dto2);
        Bcl("DateTime", dt1, dt2);
        Bcl("TimeSpan", TimeSpan.MinValue, TimeSpan.FromDays(1));
        Bcl("decimal", 1.5m, decimal.MaxValue);
        Bcl("Guid", g1, g2);
        Bcl("int?", (int?)null, (int?)5);
        Bcl("long?", (long?)-6, (long?)null);
        Bcl("(int,long)?", ((int, long)?)(1, 2L), ((int, long)?)null);
        Bcl("DateTimeOffset?", (DateTimeOffset?)dto1, (DateTimeOffset?)null);
        Bcl("IntPtr", new IntPtr(-1), new IntPtr(int.MaxValue));
        Bcl("Complex", new System.Numerics.Complex(1.5, -2.5), new System.Numerics.Complex(double.MaxValue, 0));
        // Vector<T> is refused, alone or inside (16, 32 or 64 bytes by the CPU: no bytes every host reads); the fixed
        // lanes that replace it (LaneParticle) are the same bytes everywhere, whatever this CPU's Vector<float>.Count.
        Console.WriteLine($"# Vector<float>.Count {System.Numerics.Vector<float>.Count}");
        Refused("Vector<int>", new System.Numerics.Vector<int>(7));
        Refused("Around<Vector<int>>[]", new[] { new Around<System.Numerics.Vector<int>> { A = 1, C = -1 } });
        Refused("VectorList", new VectorList { new System.Numerics.Vector<int>(7) });
        RefusedByHand("Vector<int> by hand", new System.Numerics.Vector<int>(7));
        RefusedByHand("Around<Vector<long>> by hand", new Around<System.Numerics.Vector<long>> { A = 1, C = -1 });
        Top("LaneParticle", LaneParticle.Doubled(3));
#if NET8_0_OR_GREATER
        // .NET only (the net472 build, Mono's, has none of these): checked against the fixtures, printed as comments so the
        // outputs of the Mono runs stay comparable with .NET's.
        _netOnly = true;
        Bcl("Int128", (Int128)(-1), Int128.MaxValue);
        Bcl("(byte,Int128)", ((byte)1, (Int128)(-2)), ((byte)3, Int128.MinValue));
        Bcl("Half", (Half)1.5f, Half.MinValue);
        Bcl("(byte,Half)", ((byte)1, (Half)(-2f)), ((byte)3, Half.Epsilon));
        Bcl("DateOnly", new DateOnly(2026, 11, 1), DateOnly.MaxValue);
        Bcl("TimeOnly", new TimeOnly(1, 2, 3), TimeOnly.MaxValue);
        Bcl("(byte,DateOnly,TimeOnly)", ((byte)1, new DateOnly(1, 1, 1), new TimeOnly(4)),
            ((byte)2, DateOnly.MaxValue, TimeOnly.MaxValue));
        Bcl("Range", 1..^2, ..3);
        Bcl("Index", ^1, new Index(5));
        Bcl("Rune", new Rune(0x1F600), new Rune('a'));
        Bcl("Vector2", new System.Numerics.Vector2(1, 2), new System.Numerics.Vector2(-3, 4));
        Bcl("Vector3", new System.Numerics.Vector3(1, 2, 3), new System.Numerics.Vector3(-4, 5, -6));
        Bcl("Vector4", new System.Numerics.Vector4(1, 2, 3, 4), new System.Numerics.Vector4(-5, 6, -7, 8));
        Bcl("Quaternion", new System.Numerics.Quaternion(1, 2, 3, 4), System.Numerics.Quaternion.Identity);
        Bcl("Plane", new System.Numerics.Plane(1, 2, 3, 4), new System.Numerics.Plane(-1, 0, 1, 0.5f));
        Bcl("Matrix3x2", new System.Numerics.Matrix3x2(1, 2, 3, 4, 5, 6), System.Numerics.Matrix3x2.Identity);
        Bcl("Matrix4x4", new System.Numerics.Matrix4x4(1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16),
            System.Numerics.Matrix4x4.Identity);
        Bcl("Vector64<int>", System.Runtime.Intrinsics.Vector64.Create(1, -2),
            System.Runtime.Intrinsics.Vector64.Create(3, 4));
        Bcl("Vector128<int>", System.Runtime.Intrinsics.Vector128.Create(1, -2, 3, -4),
            System.Runtime.Intrinsics.Vector128.Create(5));
        Bcl("Vector256<long>", System.Runtime.Intrinsics.Vector256.Create(1L, -2, 3, -4),
            System.Runtime.Intrinsics.Vector256.Create(5L));
        Bcl("Vector512<long>", System.Runtime.Intrinsics.Vector512.Create(1L, -2, 3, -4, 5, -6, 7, -8),
            System.Runtime.Intrinsics.Vector512.Create(9L));
        _netOnly = false;
#endif

        // Value tuples and pairs on their own, and as collections and dictionary entries: item by item.
        Top("(int,long)[]", new[] { (1, 2L), (-3, long.MinValue) });
        Top("List<(byte,long,int)>", new List<(byte, long, int)> { (1, 2, 3), (255, -1, int.MaxValue) });
        Top("(byte,(int,long))[]", new[] { ((byte)1, (2, 3L)), ((byte)4, (-5, -6L)) });
        Top("(int,DateTime,decimal)[]", new[] { (1, dt1, 1.5m), (2, dt2, -0.1m) });
        Top("KVP<int,long>[]", new[] { new KeyValuePair<int, long>(1, 2), new KeyValuePair<int, long>(-3, -4) });
        Top("List<KVP<long,byte>>", new List<KeyValuePair<long, byte>> { new(1, 2), new(-3, 4) });
        Top("Dictionary<int,(byte,long)>", new Dictionary<int, (byte, long)> { [1] = (2, 3), [-4] = (5, -6) });
        Top("Dictionary<long,KVP<int,byte>>", new Dictionary<long, KeyValuePair<int, byte>>
            { [1] = new KeyValuePair<int, byte>(2, 3) });
        // T?[] of a raw struct holding a tuple: the presence byte padded to the struct's alignment (8).
        Top("Around<(int,long)>?[]", new Around<(int, long)>?[] { new Around<(int, long)> { A = 1, B = (2, 3), C = 4 }, null });
        Top("List<Around<DateTimeOffset>?>", new List<Around<DateTimeOffset>?> { null, new Around<DateTimeOffset> { A = 5, B = dto1, C = 6 } });
        Top("(int,long)", (1, -2L));
        Top("KVP<byte,DateTime>", new KeyValuePair<byte, DateTime>(1, dt1));

        if (writeFixtures)
            WriteFixtures();
    }

#if NET8_0_OR_GREATER
    private static bool _netOnly;
#else
    private const bool _netOnly = false;
#endif

    private static readonly List<KeyValuePair<string, byte[]>> Written = new();

    /// <summary>
    /// <c>Around&lt;T&gt;[]</c> of two values: checked against CoreCLR's bytes both ways (written here, and read from
    /// them); this runtime's layout of <typeparamref name="T"/> printed.
    /// </summary>
    private static void Bcl<T>(string name, T first, T second)
    {
        Console.WriteLine($"# layout {name}: {Describe<T>()}; in Around: {Describe<Around<T>>()}");
        var value = new[]
        {
            new Around<T> { A = 1, B = first, C = -1 },
            new Around<T> { A = 0xFE, B = second, C = int.MaxValue },
        };
        Fixture("Around<" + name + ">[]", value);
        Fixture("Around<" + name + ">", value[0]);
    }

    private static void Top<T>(string name, T value) => Fixture(name, value);

    /// <summary>
    /// Writing and reading <typeparamref name="T"/> (it holds a <c>Vector&lt;T&gt;</c>) throw
    /// <see cref="NotSupportedException"/> on this runtime too, the generator's NINO029 suppressed.
    /// </summary>
    private static void Refused<T>(string name, T value)
    {
        string outcome;
        try
        {
#pragma warning disable NINO029
            NinoSerializer.Serialize(value);
            outcome = "WRITTEN";
        }
        catch (NotSupportedException)
        {
            try
            {
                NinoDeserializer.Deserialize<T>(new byte[64]);
#pragma warning restore NINO029
                outcome = "READ";
            }
            catch (NotSupportedException)
            {
                outcome = "refused";
            }
            catch (Exception e)
            {
                outcome = "READ THREW " + e.GetType().Name;
            }
        }
        catch (Exception e)
        {
            outcome = "WRITE THREW " + e.GetType().Name;
        }

        if (outcome != "refused")
            Failed++;
        Console.WriteLine($"bcl {name,-44} {outcome}");
    }

    /// <summary>
    /// The same refusal without the resolver: an <see cref="UnmanagedFormatter{T}"/> made by hand writes and reads
    /// <typeparamref name="T"/> through its layout, which throws <see cref="NotSupportedException"/> for it on every
    /// runtime (never from the layout's initializer, which Mono runs when the calling method is compiled).
    /// </summary>
    private static void RefusedByHand<T>(string name, T value) where T : unmanaged
    {
        string outcome;
        try
        {
            var writer = new Writer(new NinoArrayBufferWriter());
            new UnmanagedFormatter<T>().Serialize(value, ref writer);
            outcome = "WRITTEN";
        }
        catch (NotSupportedException)
        {
            try
            {
                var reader = new Reader(new byte[64]);
                T read = default;
                new UnmanagedFormatter<T>().Deserialize(ref reader, ref read);
                outcome = "READ";
            }
            catch (NotSupportedException)
            {
                outcome = "refused";
            }
            catch (Exception e)
            {
                outcome = "READ THREW " + e.GetType().Name;
            }
        }
        catch (Exception e)
        {
            outcome = "WRITE THREW " + e.GetType().Name;
        }

        if (outcome != "refused")
            Failed++;
        Console.WriteLine($"bcl {name,-44} {outcome}");
    }

    /// <summary>
    /// Writes <paramref name="value"/> and compares the bytes with CoreCLR x64's (<see cref="BclFixtures"/>), then reads
    /// CoreCLR's bytes and compares what it read, through the public API, with <paramref name="value"/>.
    /// </summary>
    private static void Fixture<T>(string name, T value)
    {
        string outcome;
        try
        {
            byte[] bytes = NinoSerializer.Serialize(value);
            Written.Add(new KeyValuePair<string, byte[]>(name, bytes));
            if (!BclFixtures.Bytes.TryGetValue(name, out var hex))
            {
                outcome = "NO FIXTURE";
            }
            else
            {
                var coreclr = BclFixtures.FromHex(hex);
                var read = NinoDeserializer.Deserialize<T>(coreclr);
                bool writes = bytes.SequenceEqual(coreclr);
                bool reads = Contents(read) == Contents(value);
                outcome = writes && reads ? "ok" : (writes ? "" : "WRITES OTHER BYTES ") + (reads ? "" : "READS OTHER VALUES");
            }
        }
        catch (Exception e)
        {
            outcome = "EXCEPTION " + e.GetType().Name + ": " + e.Message;
        }

        if (outcome != "ok")
            Failed++;
        Console.WriteLine($"{(_netOnly ? "# " : "")}bcl {name,-44} {outcome}");
    }

    /// <summary>What a value holds, through the public API (fields and properties, items in order).</summary>
    private static string Contents(object value)
    {
        switch (value)
        {
            case null:
                return "null";
            case DateTime d:
                return d.Ticks + "/" + d.Kind;
            case DateTimeOffset o:
                return o.UtcTicks + "/" + o.Offset.Ticks;
            case double x:
                return BitConverter.DoubleToInt64Bits(x).ToString(CultureInfo.InvariantCulture);
            case float x:
                return BitConverter.DoubleToInt64Bits(x).ToString(CultureInfo.InvariantCulture);
            case decimal m:
                return string.Join(",", decimal.GetBits(m));
            case string s:
                return s;
            case System.Collections.IEnumerable e:
                return "[" + string.Join(",", e.Cast<object>().Select(Contents)) + "]";
        }

        var type = value.GetType();
        if (type.IsPrimitive || type.IsEnum || value is Guid || value is TimeSpan || value is IntPtr || value is UIntPtr)
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public);
        var properties = fields.Length != 0
            ? Array.Empty<PropertyInfo>()
            : type.GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(p => p.GetIndexParameters().Length == 0 && p.CanRead).ToArray();
        if (fields.Length == 0 && properties.Length == 0)
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        return "{" + string.Join(",", fields.Select(f => f.Name + "=" + Contents(f.GetValue(value)))
            .Concat(properties.Select(p => p.Name + "=" + Contents(p.GetValue(value))))) + "}";
    }

    /// <summary>
    /// The size of <typeparamref name="T"/> on this runtime and the offset of each of its own fields, read from an
    /// instance: every byte set to one bit of its own offset at a time, each field copied out by reflection.
    /// </summary>
    private static string Describe<T>()
    {
        int size = Unsafe.SizeOf<T>();
        var type = typeof(T);
        if (Nullable.GetUnderlyingType(type) is { } underlying)
            return $"size {size}, hasValue@0, value@{size - SizeOf(underlying)}";
        // Not a pointer's (IntPtr): reading one by reflection crashes Mono.
        var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            .Where(f => !f.FieldType.IsPointer).ToArray();
        var offsets = new int[fields.Length];
        object box = default(T);
        if (box == null)
            return $"size {size}";
        int bit = 0;
        do
        {
            for (int i = 0; i < size; i++)
                Unsafe.Add(ref Unsafe.As<RawObject>(box).Data, i) = (byte)(i >> bit & 1);
            for (int f = 0; f < fields.Length; f++)
            {
                object field = fields[f].GetValue(box);
                // A null Nullable field: its flag (first) is 0. A boxed one: the first byte of its value or flag.
                int first = field == null ? 0
                    : Nullable.GetUnderlyingType(fields[f].FieldType) != null ? 1
                    : Unsafe.As<RawObject>(field).Data;
                offsets[f] |= (first & 1) << bit;
            }
        } while ((size - 1) >> ++bit != 0);

        return $"size {size}, " + string.Join(", ", fields.Select((f, i) =>
            $"{(f.Name.Length != 0 && f.Name[0] == '<' ? f.Name.Substring(1, f.Name.IndexOf('>') - 1) : f.Name)}@{offsets[i]}"));
    }

    private static int SizeOf(Type type) =>
        (int)typeof(BclStructs).GetMethod(nameof(SizeOfT), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(type).Invoke(null, null)!;

    private static int SizeOfT<T>() => Unsafe.SizeOf<T>();

    private sealed class RawObject
    {
#pragma warning disable CS0649
        public byte Data;
#pragma warning restore CS0649
    }

    /// <summary>
    /// Prints BclFixtures.cs with what this runtime wrote (run on CoreCLR x64: <c>--write-fixtures</c>, standard error
    /// redirected to src/Nino.Unity/Assets/Test/Runtime/BclFixtures.cs).
    /// </summary>
    private static void WriteFixtures()
    {
        var sb = new StringBuilder();
        sb.AppendLine("using System.Collections.Generic;");
        sb.AppendLine();
        sb.AppendLine("/// <summary>");
        sb.AppendLine("/// What CoreCLR x64 writes for each case of tests/Nino.RuntimeTests/BclStructs.cs (the canonical layout), as hex:");
        sb.AppendLine("/// every runtime must write and read these bytes (BclStructs, and Unity's BclLayoutTests on Mono and IL2CPP). Written by");
        sb.AppendLine("/// <c>dotnet Nino.RuntimeTests.dll --write-fixtures 2> src/Nino.Unity/Assets/Test/Runtime/BclFixtures.cs</c> on .NET 10 x64.");
        sb.AppendLine("/// </summary>");
        sb.AppendLine("public static class BclFixtures");
        sb.AppendLine("{");
        sb.AppendLine("    /// <summary>The bytes of a case's hex (Convert.FromHexString, which Unity and .NET Framework lack).</summary>");
        sb.AppendLine("    public static byte[] FromHex(string hex)");
        sb.AppendLine("    {");
        sb.AppendLine("        var bytes = new byte[hex.Length / 2];");
        sb.AppendLine("        for (int i = 0; i < bytes.Length; i++)");
        sb.AppendLine("            bytes[i] = System.Convert.ToByte(hex.Substring(2 * i, 2), 16);");
        sb.AppendLine("        return bytes;");
        sb.AppendLine("    }");
        sb.AppendLine();
        sb.AppendLine("    public static readonly Dictionary<string, string> Bytes = new()");
        sb.AppendLine("    {");
        foreach (var pair in Written)
        {
#if NET8_0_OR_GREATER
            string hex = Convert.ToHexString(pair.Value);
#else
            string hex = BitConverter.ToString(pair.Value).Replace("-", "");
#endif
            sb.AppendLine($"        [\"{pair.Key}\"] = \"{hex}\",");
        }
        sb.AppendLine("    };");
        sb.AppendLine("}");
        Console.Error.Write(sb.ToString());
    }
}
