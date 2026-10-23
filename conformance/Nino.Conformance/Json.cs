using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nino.Core;

namespace Conformance
{
    /// <summary>
    /// The <c>[NinoTypeLayout]</c>s of the sample types, and the member names (in wire order) parsed from them.
    /// </summary>
    internal static class Layouts
    {
        public static readonly Dictionary<Type, NinoTypeLayoutAttribute> ByType =
            typeof(Layouts).Assembly.GetCustomAttributes<NinoTypeLayoutAttribute>().ToDictionary(l => l.Type);

        /// <summary>Every <c>[NinoType]</c> by <see cref="Name"/>.</summary>
        public static readonly Dictionary<string, Type> ByName = ByType.Keys.ToDictionary(Name, t => t);

        public static NinoTypeLayoutAttribute Of(Type type)
        {
            if (ByType.TryGetValue(type, out var layout))
                return layout;
            if (type.IsGenericType && ByType.TryGetValue(type.GetGenericTypeDefinition(), out layout))
                return layout;
            return null;
        }

        /// <summary>The name a type is listed under: its runtime-form name (<see cref="NinoTypeId.GetName"/>).</summary>
        public static string Name(Type type) => NinoTypeId.GetName(type);

        /// <summary>Splits on <paramref name="separator"/> outside brackets.</summary>
        public static List<string> Split(string text, char separator)
        {
            var parts = new List<string>();
            int depth = 0, start = 0;
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c is '(' or '{' or '<' or '[')
                    depth++;
                else if (c is ')' or '}' or '>' or ']')
                    depth--;
                else if (c == separator && depth == 0)
                {
                    parts.Add(text.Substring(start, i - start));
                    start = i + 1;
                }
            }

            if (start < text.Length)
                parts.Add(text.Substring(start));
            return parts;
        }

        /// <summary>The serialized members of a <c>[NinoType]</c>, in the order they are written.</summary>
        public static List<string> MemberNames(NinoTypeLayoutAttribute layout)
        {
            string members = layout.Members;
            if (layout.Raw)
            {
                // raw(n){Name:type@offset:encoding,...}
                int open = members.IndexOf('{');
                if (open < 0)
                    return new List<string>();
                string inner = members.Substring(open + 1, members.LastIndexOf('}') - open - 1);
                return Split(inner, ',').Select(m => m.Substring(0, m.IndexOf(':'))).ToList();
            }

            return Split(members, ';').Where(m => m.Length > 0 && !m.StartsWith("[elements]"))
                .Select(m => m.Substring(0, m.IndexOf(':'))).ToList();
        }

        public static MemberInfo Member(Type type, string name)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic |
                                       BindingFlags.DeclaredOnly;
            for (var t = type; t != null; t = t.BaseType)
            {
                var field = t.GetField(name, flags);
                if (field != null)
                    return field;
                var property = t.GetProperty(name, flags);
                if (property != null)
                    return property;
            }

            throw new InvalidOperationException($"{type} has no member {name}");
        }

        public static Type MemberType(MemberInfo member) =>
            member is FieldInfo f ? f.FieldType : ((PropertyInfo)member).PropertyType;

        public static object Get(MemberInfo member, object target) =>
            member is FieldInfo f ? f.GetValue(target) : ((PropertyInfo)member).GetValue(target);

        /// <summary>
        /// The <c>[NinoType]</c>s a value of <paramref name="type"/> may hold: the type, the types of its members and
        /// elements, and the subtypes of every class or interface written behind a type id (not those of
        /// <c>object</c>, which are found in the value itself).
        /// </summary>
        public static void Involved(Type type, SortedDictionary<string, Type> into) =>
            Involved(type, into, new HashSet<Type>());

        private static void Involved(Type type, SortedDictionary<string, Type> into, HashSet<Type> visited)
        {
            if (type == null || type.IsGenericParameter || !visited.Add(type))
                return;
            if (type.IsArray)
            {
                Involved(type.GetElementType(), into, visited);
                return;
            }

            if (type.IsGenericType)
                foreach (var argument in type.GetGenericArguments())
                    Involved(argument, into, visited);
            // A collection derived from a generic one (Tree : List<Tree>): its elements.
            if (!type.IsGenericType && type.BaseType is { IsGenericType: true } baseType && Of(type) == null)
                Involved(baseType, into, visited);

            var layout = Of(type);
            if (layout == null)
                return;
            if (!into.TryAdd(Name(layout.Type), layout.Type))
                return;
            foreach (string name in MemberNames(layout))
                Involved(MemberType(Member(layout.Type, name)), into, visited);
            if (layout.Header == NinoHeader.TypeId)
                foreach (var other in ByType.Keys)
                    if (other != type && (other.IsSubclassOf(type) || type.IsAssignableFrom(other) ||
                                          (other.BaseType != null && other.BaseType == type)))
                        Involved(other, into, visited);
        }
    }

    /// <summary>
    /// Writes a value as the corpus's lossless JSON (described in conformance/README.md), directed by the members
    /// the layouts list.
    /// </summary>
    internal sealed class ValueJson
    {
        private const long SafeInteger = 1L << 53;

        /// <summary>Every <c>[NinoType]</c> met in the value, by name.</summary>
        public readonly SortedDictionary<string, Type> Seen = new(StringComparer.Ordinal);

        private readonly Dictionary<object, int> _tracked = new(ReferenceEqualityComparer.Instance);

        public static readonly JsonWriterOptions WriterOptions = new()
        {
            Indented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };

        public void Write(Utf8JsonWriter w, object value)
        {
            if (value == null)
            {
                w.WriteNullValue();
                return;
            }

            var type = value.GetType();
            switch (value)
            {
                case bool b:
                    w.WriteBooleanValue(b);
                    return;
                case Enum e:
                    Integer(w, ToBig(Convert.ChangeType(e, Enum.GetUnderlyingType(type), CultureInfo.InvariantCulture)));
                    return;
                case sbyte or byte or short or ushort or int or uint or long or ulong or Int128 or UInt128:
                    Integer(w, ToBig(value));
                    return;
                case char c:
                    w.WriteNumberValue((int)c);
                    return;
                case Half h:
                    w.WriteStringValue("0x" + BitConverter.HalfToUInt16Bits(h).ToString("x4"));
                    return;
                case float f:
                    w.WriteStringValue("0x" + BitConverter.SingleToUInt32Bits(f).ToString("x8"));
                    return;
                case double d:
                    w.WriteStringValue("0x" + BitConverter.DoubleToUInt64Bits(d).ToString("x16"));
                    return;
                case decimal m:
                    w.WriteStringValue(m.ToString(CultureInfo.InvariantCulture));
                    return;
                case Guid g:
                    w.WriteStringValue(g.ToString("D"));
                    return;
                case DateTime dt:
                    w.WriteStartObject();
                    w.WritePropertyName("ticks");
                    Integer(w, dt.Ticks);
                    w.WriteString("kind", dt.Kind.ToString());
                    w.WriteEndObject();
                    return;
                case DateTimeOffset dto:
                    w.WriteStartObject();
                    w.WritePropertyName("utcTicks");
                    Integer(w, dto.UtcTicks);
                    w.WriteNumber("offsetMinutes", (int)dto.Offset.TotalMinutes);
                    w.WriteEndObject();
                    return;
                case TimeSpan ts:
                    Integer(w, ts.Ticks);
                    return;
                case DateOnly date:
                    w.WriteNumberValue(date.DayNumber);
                    return;
                case TimeOnly time:
                    Integer(w, time.Ticks);
                    return;
                case BigInteger big:
                    w.WriteStringValue(big.ToString(CultureInfo.InvariantCulture));
                    return;
                case Version v:
                    w.WriteStartArray();
                    w.WriteNumberValue(v.Major);
                    w.WriteNumberValue(v.Minor);
                    w.WriteNumberValue(v.Build);
                    w.WriteNumberValue(v.Revision);
                    w.WriteEndArray();
                    return;
                case string s:
                    String(w, s);
                    return;
                case byte[] bytes:
                    w.WriteStringValue(Convert.ToHexString(bytes).ToLowerInvariant());
                    return;
            }

            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(KeyValuePair<,>))
            {
                w.WriteStartArray();
                Write(w, type.GetProperty("Key")!.GetValue(value));
                Write(w, type.GetProperty("Value")!.GetValue(value));
                w.WriteEndArray();
                return;
            }

            var layout = Layouts.Of(type);
            if (layout != null)
            {
                Seen.TryAdd(Layouts.Name(type), type);
                w.WriteStartObject();
                if (layout.TrackReferences)
                {
                    // Tracked objects are numbered from 1 in the order they are first met, which is the order they
                    // are written; a later occurrence is a reference to that number.
                    if (_tracked.TryGetValue(value, out int id))
                    {
                        w.WriteNumber("$ref", id);
                        w.WriteEndObject();
                        return;
                    }

                    _tracked.Add(value, _tracked.Count + 1);
                    w.WriteNumber("$id", _tracked.Count);
                }

                w.WriteString("$type", Layouts.Name(type));
                if (layout.Members.StartsWith("[elements]"))
                {
                    w.WritePropertyName("$elements");
                    Elements(w, (IEnumerable)value);
                }

                foreach (string name in Layouts.MemberNames(layout))
                {
                    w.WritePropertyName(name);
                    Write(w, Layouts.Get(Layouts.Member(type, name), value));
                }

                w.WriteEndObject();
                return;
            }

            if (value is ITuple tuple)
            {
                w.WriteStartArray();
                for (int i = 0; i < tuple.Length; i++)
                    Write(w, tuple[i]);
                w.WriteEndArray();
                return;
            }

            if (value is Array { Rank: > 1 } grid)
            {
                w.WriteStartObject();
                w.WriteStartArray("lengths");
                for (int i = 0; i < grid.Rank; i++)
                    w.WriteNumberValue(grid.GetLength(i));
                w.WriteEndArray();
                w.WritePropertyName("items");
                Elements(w, grid);
                w.WriteEndObject();
                return;
            }

            if (value is IEnumerable sequence)
            {
                Elements(w, sequence);
                return;
            }

            throw new InvalidOperationException($"No JSON encoding for {type}");
        }

        private void Elements(Utf8JsonWriter w, IEnumerable sequence)
        {
            w.WriteStartArray();
            if (sequence is IDictionary map)
            {
                // In the order the entries are written (the enumeration order of the dictionary).
                var e = map.GetEnumerator();
                while (e.MoveNext())
                {
                    w.WriteStartArray();
                    Write(w, e.Key);
                    Write(w, e.Value);
                    w.WriteEndArray();
                }
            }
            else
                foreach (object item in sequence)
                    Write(w, item);

            w.WriteEndArray();
        }

        private static BigInteger ToBig(object integer) => integer switch
        {
            Int128 i => (BigInteger)i,
            UInt128 u => (BigInteger)u,
            ulong u => u,
            _ => Convert.ToInt64(integer, CultureInfo.InvariantCulture),
        };

        /// <summary>A JSON number when it is exact in a double (|n| &lt;= 2^53), else a decimal string.</summary>
        private static void Integer(Utf8JsonWriter w, BigInteger value)
        {
            if (BigInteger.Abs(value) <= SafeInteger)
                w.WriteNumberValue((long)value);
            else
                w.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>A JSON string, or <c>{"utf16": [code units]}</c> when it holds any surrogate.</summary>
        private static void String(Utf8JsonWriter w, string s)
        {
            if (!s.Any(char.IsSurrogate))
            {
                w.WriteStringValue(s);
                return;
            }

            w.WriteStartObject();
            w.WriteStartArray("utf16");
            foreach (char c in s)
                w.WriteNumberValue((int)c);
            w.WriteEndArray();
            w.WriteEndObject();
        }

        public static JsonNode ToNode(object value, ValueJson json)
        {
            var buffer = new System.IO.MemoryStream();
            using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { SkipValidation = false, MaxDepth = 100_000 }))
                json.Write(w, value);
            return JsonNode.Parse(buffer.ToArray(), documentOptions: new JsonDocumentOptions { MaxDepth = 100_000 });
        }
    }

    internal static class Hex
    {
        public static string Of(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();
    }
}
