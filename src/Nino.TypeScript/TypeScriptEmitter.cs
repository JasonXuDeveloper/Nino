using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;

namespace Nino.TypeScript
{
    /// <summary>Options of the generated code.</summary>
    internal sealed class TypeScriptOptions
    {
        /// <summary>The extension of the runtime's import (<c>""</c>, <c>".js"</c>, <c>".ts"</c>).</summary>
        public string ImportExtension { get; set; } = "";

        /// <summary>Whether member names are camelCased (the default) or kept as in C#.</summary>
        public bool CamelCase { get; set; } = true;
    }

    /// <summary>
    /// Writes the TypeScript of the <c>[NinoType]</c>s whose layouts <see cref="LayoutReader"/> read: a class per type,
    /// an <c>as const</c> object per enum and a <c>NinoCodec</c> per type, which read and write exactly the bytes the
    /// layout describes. A type whose layout it cannot express (generics, reference tracking, <c>[NinoType]</c>
    /// collections, encodings without a TypeScript form) is left out with a warning, and so is every type holding one.
    /// </summary>
    internal sealed class TypeScriptEmitter
    {
        private readonly Dictionary<Type, TypeLayout> _layouts;
        private readonly TypeScriptOptions _options;
        private readonly HashSet<Type> _supported = new();
        private readonly Dictionary<Type, string> _names = new();

        // Met while shaping the members of the supported types.
        private readonly HashSet<Type> _enums = new();
        private readonly Dictionary<Type, WireEncoding> _rawStructs = new();
        private readonly StringBuilder _helpers = new();
        private readonly Dictionary<string, string> _helperNames = new();

        public List<string> Warnings { get; } = new();

        public TypeScriptEmitter(Dictionary<Type, TypeLayout> layouts, TypeScriptOptions options)
        {
            _layouts = layouts;
            _options = options;
        }

        private sealed class UnsupportedException : Exception
        {
            public UnsupportedException(string message) : base(message)
            {
            }
        }

        /// <summary>
        /// How a value is written in TypeScript: its type, an expression reading it from a reader, a statement writing a
        /// value to a writer (which may evaluate the value's expression more than once) and its default value.
        /// </summary>
        private sealed record Shape(string Ts, Func<string, string> Read, Func<string, string, string> Write,
            string Default)
        {
            /// <summary>
            /// The size of a value of fixed size (raw memory, or fixed-size values one after another), else -1. Such a
            /// value is also read and written at an offset of the reader's or writer's view (<see cref="ReadAt"/>,
            /// <see cref="WriteAt"/>), so a run of them takes its bytes at once.
            /// </summary>
            public int Size { get; init; } = -1;

            /// <summary>An expression reading the value at an offset (which it may use more than once).</summary>
            public Func<string, string, string>? ReadAt { get; init; }

            /// <summary>A statement writing the value at an offset.</summary>
            public Func<string, string, string, string>? WriteAt { get; init; }

            /// <summary>Whether <see cref="WriteAt"/> needs zeroed bytes: it skips padding, or absent values.</summary>
            public bool Zeroed { get; init; }

            /// <summary>Whether the value may hold a class body (a class, <c>object</c> or an interface) itself.</summary>
            public bool Opens { get; init; }

            /// <summary>The non-raw structs the value holds directly: their bodies may open more (see <see cref="Opens"/>).</summary>
            public IReadOnlyList<Type> Reaches { get; init; } = Type.EmptyTypes;

            public bool Fixed => Size >= 0;
        }

        /// <summary>The generated file for the types of the layouts.</summary>
        public string Emit(string source)
        {
            ResolveSupported();
            var layouts = _layouts.Values.Where(l => _supported.Contains(l.Type)).ToList();
            AssignNames(layouts.Select(l => l.Type).Concat(_enums).Concat(_rawStructs.Keys));

            var body = new StringBuilder();
            foreach (var e in _enums.OrderBy(Name, StringComparer.Ordinal))
                EmitEnum(body, e);
            foreach (var (type, encoding) in _rawStructs.OrderBy(p => Name(p.Key), StringComparer.Ordinal).ToList())
                EmitRawStruct(body, type, encoding);
            foreach (var layout in layouts.Where(l => l.Type.IsInterface).OrderBy(l => Name(l.Type), StringComparer.Ordinal))
                EmitInterface(body, layout);
            foreach (var layout in layouts.Where(l => !l.Type.IsInterface && !l.Raw)
                         .OrderBy(l => Depth(l.Type)).ThenBy(l => Name(l.Type), StringComparer.Ordinal))
                EmitClass(body, layout);

            // Every type that can be written behind a type id, once its class is declared.
            foreach (var layout in Registered(layouts.Select(l => l.Type)).Select(t => _layouts[t]))
            {
                string name = Name(layout.Type);
                body.Append("types.add(").Append(Int(layout.Id)).Append(", ").Append(name).Append(", ")
                    .Append(layout.Raw
                        ? $"(r) => {ReadBody(layout.Type, "r")}, (w, v) => {WriteBody(layout.Type, "w", "v")}"
                        : $"read{name}, write{name}").Append(");\n");
            }

            body.Append('\n');
            foreach (var layout in layouts.OrderBy(l => Name(l.Type), StringComparer.Ordinal))
                EmitCodec(body, layout);
            body.Append(_helpers);

            var sb = new StringBuilder();
            sb.Append("// Generated by Nino.TypeScript from ").Append(source)
                .Append(": its [NinoType]s, read and written as Nino serializes them in .NET.\n")
                .Append("// Do not edit: it is overwritten on every build.\n\n")
                .Append("import { NinoReader, NinoRegistry, NinoWriter, type NinoCodec } from \"./nino")
                .Append(_options.ImportExtension).Append("\";\n\n")
                .Append("const types = new NinoRegistry();\n\n")
                .Append(body);
            return sb.ToString().TrimEnd('\n') + "\n";
        }

        // ---------------------------------------------------------------- which types

        private void ResolveSupported()
        {
            foreach (var layout in _layouts.Values)
            {
                string? reason = layout.Type.IsGenericType ? "generic types are not supported"
                    : layout.TrackReferences ? "reference tracking (trackReferences) is not supported"
                    : layout.Elements != null ? "[NinoType] collections are not supported"
                    : null;
                if (reason == null)
                    _supported.Add(layout.Type);
                else
                    Warn(layout.Type, reason);
            }

            // Leave out the types holding a type that is left out, until every type left can be written.
            while (true)
            {
                _enums.Clear();
                _rawStructs.Clear();
                var failing = _layouts.Values.Where(l => _supported.Contains(l.Type))
                    .Select(l => (l.Type, Reason: Check(l))).FirstOrDefault(f => f.Reason != null);
                if (failing.Type == null)
                    return;
                _supported.Remove(failing.Type);
                Warn(failing.Type, failing.Reason!);
            }
        }

        /// <summary>Why a layout cannot be written, or null.</summary>
        private string? Check(TypeLayout layout)
        {
            try
            {
                if (layout.Raw)
                    RawShape(layout.RawEncoding!, layout.Type);
                else
                    foreach (var member in layout.Members)
                        ShapeOf(member.Encoding, member.Type ??
                                                 throw new UnsupportedException($"member {member.Name} was not found"));
                return null;
            }
            catch (UnsupportedException e)
            {
                return e.Message;
            }
        }

        private void Warn(Type type, string reason) =>
            Warnings.Add($"{type.FullName} is not written to TypeScript: {reason}.");

        /// <summary>The concrete types of <paramref name="types"/> written behind a type id.</summary>
        private IEnumerable<Type> Registered(IEnumerable<Type> types) =>
            types.Where(t => _layouts[t].Id != 0 && !t.IsAbstract && !t.IsInterface)
                .OrderBy(Name, StringComparer.Ordinal);

        // ---------------------------------------------------------------- shapes

        /// <summary>A value of fixed size, read and written alone by functions that take its bytes.</summary>
        private Shape Fixed(string ts, int size, Func<string, string, string> readAt,
            Func<string, string, string, string> writeAt, string def, bool zeroed) =>
            new(ts,
                r => Helper($"read:{ts}:{readAt("r", "at")}", n =>
                    $"function {n}(r: NinoReader): {ts} {{\n  const at = r.take({Int(size)});\n" +
                    $"  return {readAt("r", "at")};\n}}\n") + $"({r})",
                (w, v) => Helper($"write:{ts}:{writeAt("w", "at", "v")}", n =>
                    $"function {n}(w: NinoWriter, v: {ts}): void {{\n  const at = {Reserve("w", size, zeroed)};\n" +
                    $"  {Statement(writeAt("w", "at", "v"))}\n}}\n") + $"({w}, {v})",
                def) { Size = size, ReadAt = readAt, WriteAt = writeAt, Zeroed = zeroed };

        /// <summary>Takes <paramref name="size"/> bytes of the writer, zeroed if <paramref name="zeroed"/>.</summary>
        private static string Reserve(string w, int size, bool zeroed) => Reserve(w, Int(size), zeroed);

        private static string Reserve(string w, string size, bool zeroed) =>
            $"{w}.{(zeroed ? "raw" : "reserve")}({size})";

        /// <summary>The offset <paramref name="add"/> bytes after <paramref name="at"/> (<c>at + n</c>).</summary>
        private static string Plus(string at, int add)
        {
            if (add == 0)
                return at;
            int plus = at.LastIndexOf(" + ", StringComparison.Ordinal);
            if (plus >= 0 && int.TryParse(at.AsSpan(plus + 3), NumberStyles.None, CultureInfo.InvariantCulture,
                    out int offset))
                return $"{at.Substring(0, plus)} + {Int(offset + add)}";
            return $"{at} + {Int(add)}";
        }

        /// <summary>A statement ending with <c>;</c> or a block.</summary>
        private static string Statement(string code) => code.EndsWith('}') ? code : code + ";";

        /// <summary>A presence byte, then the value if present (<c>T?</c>, <c>Tuple</c>, a sealed class).</summary>
        private static Shape Nullable(Shape inner) =>
            new($"{inner.Ts} | null", r => $"({r}.notNull() ? {inner.Read(r)} : null)",
                (w, v) => $"if ({v} === null || {v} === undefined) {w}.u8(0); else {{ {w}.u8(1); " +
                          $"{Statement(inner.Write(w, v))} }}", "null") { Opens = inner.Opens, Reaches = inner.Reaches };

        /// <summary>What a value made of <paramref name="parts"/> holds (<see cref="Shape.Opens"/>, <see cref="Shape.Reaches"/>).</summary>
        private static Shape Holding(Shape shape, params Shape[] parts) => shape with
        {
            Opens = parts.Any(p => p.Opens), Reaches = parts.SelectMany(p => p.Reaches).Distinct().ToList(),
        };

        private Shape ShapeOf(WireEncoding e, Type clr)
        {
            switch (e.Kind)
            {
                case "raw":
                    return RawShape(e, clr);
                case "string":
                    if (clr.FullName != "System.String")
                        throw new UnsupportedException($"{clr.FullName} has no TypeScript form");
                    return new Shape("string | null", r => $"{r}.string()", (w, v) => $"{w}.string({v})", "null");
                case "utf8":
                    return new Shape("string | null", r => $"{r}.utf8()", (w, v) => $"{w}.utf8({v})", "null");
                case "object":
                    return new Shape("unknown", r => $"types.read({r}, null)", (w, v) => $"types.write({w}, {v}, null)",
                        "null") { Opens = true, Reaches = BodyStructs(_ => true) };
                case "type":
                    return TypeShape(clr);
                case "nullable":
                {
                    // T?, Lazy<T>: a presence byte, then T. Tuple<...>: a presence byte, then its items.
                    var definition = clr.IsGenericType ? clr.GetGenericTypeDefinition().FullName : null;
                    return definition switch
                    {
                        "System.Nullable`1" or "System.Lazy`1" =>
                            Nullable(ShapeOf(e.Arguments[0], clr.GetGenericArguments()[0])),
                        _ when definition?.StartsWith("System.Tuple`", StringComparison.Ordinal) == true =>
                            Nullable(ShapeOf(e.Arguments[0], clr)),
                        _ => throw new UnsupportedException($"{clr.FullName} ({e}) has no TypeScript form"),
                    };
                }
                case "seq":
                {
                    var elementType = clr.IsArray ? clr.GetElementType()
                        : Implements(clr, "System.Collections.Generic.IEnumerable`1")?.GetGenericArguments()[0];
                    if (elementType == null || clr.IsArray && clr.GetArrayRank() != 1)
                        throw new UnsupportedException($"{clr.FullName} has no TypeScript form");
                    var element = ShapeOf(e.Arguments[0], elementType);
                    if (clr.IsArray && elementType.FullName == "System.Byte")
                        return new Shape("Uint8Array | null", r => $"{r}.bytes()", (w, v) => $"{w}.bytes({v})",
                            "null");
                    return Holding(Sequence(element, Implements(clr, "System.Collections.Generic.ISet`1") != null), element);
                }
                case "map":
                {
                    var pair = (Implements(clr, "System.Collections.Generic.IDictionary`2") ??
                                Implements(clr, "System.Collections.Generic.IReadOnlyDictionary`2"))
                               ?.GetGenericArguments() ??
                               throw new UnsupportedException($"{clr.FullName} has no TypeScript form");
                    var (key, value) = (ShapeOf(e.Arguments[0], pair[0]), ShapeOf(e.Arguments[1], pair[1]));
                    return Holding(Map(key, value), key, value);
                }
                case "pair":
                case "tuple":
                {
                    var args = clr.IsGenericType ? clr.GetGenericArguments() : Type.EmptyTypes;
                    if (args.Length != e.Arguments.Count || args.Length > 7)
                        throw new UnsupportedException($"{clr.FullName} ({e}) has no TypeScript form");
                    // The items one after another: at fixed offsets when they all have a fixed size.
                    var items = e.Arguments.Select((a, i) => ShapeOf(a, args[i])).ToList();
                    var offsets = new List<int>();
                    int size = 0;
                    foreach (var item in items)
                    {
                        offsets.Add(size);
                        size += item.Size;
                    }

                    return Holding(Tuple(items, offsets, items.All(i => i.Fixed) ? size : -1, items.Any(i => i.Zeroed)),
                        items.ToArray());
                }
                default:
                    throw new UnsupportedException($"{clr.FullName} ({e.Kind}) has no TypeScript form");
            }
        }

        /// <summary>
        /// A tuple or pair as a TypeScript tuple: its items at <paramref name="offsets"/> when they are all of fixed
        /// size (<paramref name="size"/> bytes in all), else one after another.
        /// </summary>
        private Shape Tuple(List<Shape> items, List<int> offsets, int size, bool zeroed)
        {
            string ts = "[" + string.Join(", ", items.Select(i => i.Ts)) + "]";
            string def = "[" + string.Join(", ", items.Select(i => i.Default)) + "]";
            if (size >= 0)
                return Fixed(ts, size,
                    (r, at) => "[" + string.Join(", ",
                        items.Select((item, i) => item.ReadAt!(r, Plus(at, offsets[i])))) + "]",
                    (w, at, v) => string.Join(" ", items.Select((item, i) =>
                        Statement(item.WriteAt!(w, Plus(at, offsets[i]), $"{v}[{Int(i)}]")))).TrimEnd(';'),
                    def, zeroed);
            return new Shape(ts, r => "[" + string.Join(", ", items.Select(i => i.Read(r))) + "]",
                (w, v) => string.Join(" ", items.Select((item, i) => Statement(item.Write(w, $"{v}[{Int(i)}]"))))
                    .TrimEnd(';'), def);
        }

        /// <summary>
        /// An array, list or set (length, then each element), read and written by a loop of its own; the elements of
        /// fixed size take their bytes at once.
        /// </summary>
        private Shape Sequence(Shape element, bool set)
        {
            string ts = set ? $"Set<{element.Ts}>" : $"{Parenthesize(element.Ts)}[]";
            string input = set ? $"ReadonlySet<{element.Ts}>" : $"readonly {Parenthesize(element.Ts)}[]";
            string Add(string value) => set ? $"values.add({value})" : $"values[i] = {value}";
            // Keyed by the code of the elements too: two encodings can share a TypeScript type.
            string ElementRead() => element.Fixed ? element.ReadAt!("r", "at") : element.Read("r");
            string ElementWrite() => element.Fixed ? element.WriteAt!("w", "at", "value") : element.Write("w", "value");
            return new Shape(ts + " | null", r => Helper($"read:{ts}:{ElementRead()}", n =>
                {
                    var sb = new StringBuilder();
                    sb.Append("function ").Append(n).Append("(r: NinoReader): ").Append(ts).Append(" | null {\n")
                        .Append("  const length = r.length();\n  if (length < 0) return null;\n")
                        .Append(set ? $"  const values = new Set<{element.Ts}>();\n"
                            : $"  const values = new Array<{element.Ts}>(length);\n");
                    if (element.Fixed)
                        sb.Append("  for (let i = 0, at = r.take(length * ").Append(Int(element.Size))
                            .Append("); i < length; i++, at += ").Append(Int(element.Size)).Append(") ")
                            .Append(Add(element.ReadAt!("r", "at"))).Append(";\n");
                    else
                        sb.Append("  for (let i = 0; i < length; i++) ").Append(Add(element.Read("r"))).Append(";\n");
                    return sb.Append("  return values;\n}\n").ToString();
                }) + $"({r})",
                (w, v) => Helper($"write:{ts}:{ElementWrite()}", n =>
                {
                    var sb = new StringBuilder();
                    sb.Append("function ").Append(n).Append("(w: NinoWriter, values: ").Append(input)
                        .Append(" | null): void {\n")
                        .Append("  if (values === null || values === undefined) {\n    w.i32(-1);\n    return;\n  }\n");
                    string length = set ? "values.size" : "values.length";
                    sb.Append("  w.i32(").Append(length).Append(");\n");
                    // Arrays by index: V8 runs an index loop faster than an array iterator (Chromium most).
                    string loop = set ? "  for (const value of values) {\n"
                        : "  for (let i = 0; i < values.length; i++) {\n    const value = values[i];\n";
                    if (element.Fixed)
                    {
                        sb.Append("  let at = ").Append(Reserve("w", $"{length} * {Int(element.Size)}", element.Zeroed))
                            .Append(";\n").Append(loop).Append("    ")
                            .Append(Statement(element.WriteAt!("w", "at", "value"))).Append("\n    at += ")
                            .Append(Int(element.Size)).Append(";\n  }\n");
                    }
                    else
                        sb.Append(loop).Append("    ")
                            .Append(Statement(element.Write("w", "value"))).Append("\n  }\n");
                    return sb.Append("}\n").ToString();
                }) + $"({w}, {v})", "null");
        }

        /// <summary>A dictionary: length, then each key and value, read and written by a loop of its own.</summary>
        private Shape Map(Shape key, Shape value)
        {
            string ts = $"Map<{key.Ts}, {value.Ts}>";
            bool fixedPair = key.Fixed && value.Fixed;
            int size = key.Size + value.Size;
            string PairRead() => fixedPair ? key.ReadAt!("r", "at") + value.ReadAt!("r", "at")
                : key.Read("r") + value.Read("r");
            string PairWrite() => fixedPair ? key.WriteAt!("w", "at", "key") + value.WriteAt!("w", "at", "value")
                : key.Write("w", "key") + value.Write("w", "value");
            return new Shape(ts + " | null", r => Helper($"read:{ts}:{PairRead()}", n =>
                {
                    var sb = new StringBuilder();
                    sb.Append("function ").Append(n).Append("(r: NinoReader): ").Append(ts).Append(" | null {\n")
                        .Append("  const length = r.length();\n  if (length < 0) return null;\n")
                        .Append("  const map = new ").Append(ts).Append("();\n");
                    if (fixedPair)
                        sb.Append("  for (let i = 0, at = r.take(length * ").Append(Int(size))
                            .Append("); i < length; i++, at += ").Append(Int(size)).Append(") map.set(")
                            .Append(key.ReadAt!("r", "at")).Append(", ")
                            .Append(value.ReadAt!("r", Plus("at", key.Size))).Append(");\n");
                    else
                        sb.Append("  for (let i = 0; i < length; i++) {\n    const key = ").Append(key.Read("r"))
                            .Append(";\n    map.set(key, ").Append(value.Read("r")).Append(");\n  }\n");
                    return sb.Append("  return map;\n}\n").ToString();
                }) + $"({r})",
                (w, v) => Helper($"write:{ts}:{PairWrite()}", n =>
                {
                    var sb = new StringBuilder();
                    sb.Append("function ").Append(n).Append("(w: NinoWriter, map: ReadonlyMap<").Append(key.Ts)
                        .Append(", ").Append(value.Ts).Append("> | null): void {\n")
                        .Append("  if (map === null || map === undefined) {\n    w.i32(-1);\n    return;\n  }\n")
                        .Append("  w.i32(map.size);\n");
                    if (fixedPair)
                        sb.Append("  let at = ")
                            .Append(Reserve("w", $"map.size * {Int(size)}", key.Zeroed || value.Zeroed))
                            .Append(";\n  for (const [key, value] of map) {\n    ")
                            .Append(Statement(key.WriteAt!("w", "at", "key"))).Append("\n    ")
                            .Append(Statement(value.WriteAt!("w", Plus("at", key.Size), "value")))
                            .Append("\n    at += ").Append(Int(size)).Append(";\n  }\n");
                    else
                        sb.Append("  for (const [key, value] of map) {\n    ").Append(Statement(key.Write("w", "key")))
                            .Append("\n    ").Append(Statement(value.Write("w", "value"))).Append("\n  }\n");
                    return sb.Append("}\n").ToString();
                }) + $"({w}, {v})", "null");
        }

        private static string Parenthesize(string ts) => ts.Contains(' ') ? $"({ts})" : ts;

        /// <summary>A <c>[NinoType]</c> member, with the header of its kind.</summary>
        private Shape TypeShape(Type clr)
        {
            if (!_layouts.TryGetValue(clr, out var layout) || !_supported.Contains(clr))
                throw new UnsupportedException($"{clr.FullName} is not written to TypeScript");
            if (layout.Raw)
                return RawShape(layout.RawEncoding!, clr);
            string name = Name(clr);
            bool body = !clr.IsValueType;
            return layout.Header switch
            {
                TypeLayout.NoHeader => new Shape(name, r => $"read{name}({r})", (w, v) => $"write{name}({w}, {v})",
                    $"new {name}()") { Opens = body, Reaches = body ? Type.EmptyTypes : new[] { clr } },
                TypeLayout.NullFlag => Nullable(new Shape(name, r => $"read{name}({r})",
                    (w, v) => $"write{name}({w}, {v})", "null") { Opens = body, Reaches = body ? Type.EmptyTypes : new[] { clr } }),
                // Any type assignable to it, by the type id in front of its body.
                _ => new Shape($"{name} | null", r => $"{name}Codec.read({r})",
                    (w, v) => $"{name}Codec.write({w}, {v})", "null")
                {
                    Opens = true, Reaches = BodyStructs(clr.IsAssignableFrom),
                },
            };
        }

        /// <summary>A value copied as raw memory: a primitive, an enum, or a struct in its canonical layout.</summary>
        private Shape RawShape(WireEncoding e, Type clr)
        {
            if (clr.IsEnum)
            {
                _enums.Add(clr);
                var underlying = Primitive(clr.GetEnumUnderlyingType()) ??
                                 throw new UnsupportedException($"{clr.FullName}'s underlying type has no TypeScript form");
                string name = Name(clr);
                bool zero = clr.GetFields(BindingFlags.Public | BindingFlags.Static)
                    .Any(f => Convert.ToString(f.GetRawConstantValue(), CultureInfo.InvariantCulture) == "0");
                return underlying with
                {
                    Ts = name,
                    Read = r => $"{underlying.Read(r)} as {name}",
                    ReadAt = (r, at) => $"{underlying.ReadAt!(r, at)} as {name}",
                    Default = zero || Flags(clr) ? $"{underlying.Default} as {name}"
                        : $"{underlying.Default} as {underlying.Ts} as {name}",
                };
            }

            if (Primitive(clr) is { } primitive)
                return primitive;
            if (e.Fields.Count == 0 || !clr.IsValueType)
                throw new UnsupportedException($"{clr.FullName} ({e}) has no TypeScript form");

            string definition = clr.IsGenericType ? clr.GetGenericTypeDefinition().FullName ?? "" : "";
            if (definition == "System.Nullable`1")
            {
                // T? in the canonical Nullable<T> layout: the flag, then the value at its offset (zeroed when absent).
                var (flag, value) = (e.Fields[0], e.Fields[1]);
                var inner = RawShape(value.Encoding, clr.GetGenericArguments()[0]);
                return Fixed($"{inner.Ts} | null", e.Size,
                    (r, at) => $"({r}.view.getUint8({Plus(at, flag.Offset)}) !== 0 ? " +
                               $"{inner.ReadAt!(r, Plus(at, value.Offset))} : null)",
                    (w, at, v) => $"if ({v} !== null && {v} !== undefined) {{ " +
                                  $"{w}.view.setUint8({Plus(at, flag.Offset)}, 1); " +
                                  $"{Statement(inner.WriteAt!(w, Plus(at, value.Offset), v))} }}",
                    "null", zeroed: true);
            }

            if (definition.StartsWith("System.ValueTuple`", StringComparison.Ordinal) ||
                definition == "System.Collections.Generic.KeyValuePair`2")
            {
                // Raw tuples and pairs are TypeScript tuples too: each item at its offset.
                var items = e.Fields.Select(f => RawShape(f.Encoding, FieldType(clr, f.Name))).ToList();
                return Tuple(items, e.Fields.Select(f => f.Offset).ToList(), e.Size,
                    !Packed(e) || items.Any(i => i.Zeroed));
            }

            _rawStructs.TryAdd(clr, e);
            // Also adds the enums and structs of its fields.
            var fieldShapes = e.Fields.Select(f => RawShape(f.Encoding, FieldType(clr, f.Name))).ToList();
            // Padding is zeroed (as Nino zeroes it), and so is anything its fields skip.
            bool zeroed = !Packed(e) || fieldShapes.Any(f => f.Zeroed);

            // Read and written by functions of its own, at the offset given.
            string structName = Name(clr);
            return new Shape(structName, r => $"read{structName}({r}, {r}.take({Int(e.Size)}))",
                (w, v) => $"write{structName}({w}, {Reserve(w, e.Size, zeroed)}, {v})", $"new {structName}()")
            {
                Size = e.Size,
                ReadAt = (r, at) => $"read{structName}({r}, {at})",
                WriteAt = (w, at, v) => $"write{structName}({w}, {at}, {v})",
                Zeroed = zeroed,
            };
        }

        private static readonly long MinDateMilliseconds =
            (DateTime.MinValue - DateTime.UnixEpoch).Ticks / TimeSpan.TicksPerMillisecond;

        private static Shape? Primitive(Type clr)
        {
            // Read and written alone by the reader's and writer's methods; at an offset, from their views.
            static Shape Of(string ts, string fn, string def, int size, string get, string set) =>
                new(ts, r => $"{r}.{fn}()", (w, v) => $"{w}.{fn}({v})", def)
                {
                    Size = size,
                    ReadAt = (r, at) => string.Format(CultureInfo.InvariantCulture, get, r, at),
                    WriteAt = (w, at, v) => string.Format(CultureInfo.InvariantCulture, set, w, at, v),
                };
            static Shape View(string ts, string fn, string def, int size, string type) =>
                Of(ts, fn, def, size, $"{{0}}.view.get{type}({{1}}{(size > 1 ? ", true" : "")})",
                    $"{{0}}.view.set{type}({{1}}, {{2}}{(size > 1 ? ", true" : "")})");
            return clr.FullName switch
            {
                "System.Boolean" => Of("boolean", "bool", "false", 1, "{0}.view.getUint8({1}) !== 0",
                    "{0}.view.setUint8({1}, {2} ? 1 : 0)"),
                "System.Byte" => View("number", "u8", "0", 1, "Uint8"),
                "System.SByte" => View("number", "i8", "0", 1, "Int8"),
                "System.Int16" => View("number", "i16", "0", 2, "Int16"),
                "System.UInt16" => View("number", "u16", "0", 2, "Uint16"),
                "System.Int32" => View("number", "i32", "0", 4, "Int32"),
                "System.UInt32" => View("number", "u32", "0", 4, "Uint32"),
                "System.Int64" or "System.IntPtr" => View("bigint", "i64", "0n", 8, "BigInt64"),
                "System.UInt64" or "System.UIntPtr" => View("bigint", "u64", "0n", 8, "BigUint64"),
                "System.Single" => View("number", "f32", "0", 4, "Float32"),
                "System.Double" => View("number", "f64", "0", 8, "Float64"),
                // "" has no char code (NaN), which a Uint16 converts to 0.
                "System.Char" => Of("string", "char", "\"\\0\"", 2,
                    "String.fromCharCode({0}.view.getUint16({1}, true))",
                    "{0}.view.setUint16({1}, {2}.charCodeAt(0), true)"),
                "System.Decimal" => Of("string", "decimal", "\"0\"", 16, "{0}.decimalAt({1})",
                    "{0}.decimalAt({1}, {2})"),
                "System.Guid" => Of("string", "guid", "\"00000000-0000-0000-0000-000000000000\"", 16, "{0}.guidAt({1})",
                    "{0}.guidAt({1}, {2})"),
                "System.DateTime" => Of("Date", "dateTime", $"new Date({Long(MinDateMilliseconds)})", 8,
                    "{0}.dateTimeAt({1})", "{0}.dateTimeAt({1}, {2})"),
                "System.TimeSpan" => View("bigint", "timeSpan", "0n", 8, "BigInt64"),
                _ => null,
            };
        }

        // ---------------------------------------------------------------- nesting

        /// <summary>
        /// The non-raw structs written behind a type id (for <c>object</c> or an interface) that
        /// <paramref name="assignable"/> accepts.
        /// </summary>
        private List<Type> BodyStructs(Func<Type, bool> assignable) =>
            _layouts.Values.Where(l => l.Type.IsValueType && !l.Raw && l.Id != 0 && assignable(l.Type))
                .Select(l => l.Type).ToList();

        private readonly Dictionary<Type, bool> _structOpens = new();

        /// <summary>Whether a value of <paramref name="shape"/> may hold a class body, directly or in its structs.</summary>
        private bool Opens(Shape shape) => shape.Opens || shape.Reaches.Any(StructOpens);

        private bool StructOpens(Type type)
        {
            if (_structOpens.TryGetValue(type, out bool opens))
                return opens;
            // A struct that holds itself opens what its other members open.
            _structOpens[type] = false;
            opens = _supported.Contains(type) && Members(_layouts[type]).Any(m => Opens(m.Shape));
            _structOpens[type] = opens;
            return opens;
        }

        /// <summary>
        /// Whether a class body may hold other class bodies (<c>NinoBody.Nests</c>): it counts the depth for them, and
        /// at depth 0, ends with the bodies deferred below it. Saying so of one that does not changes no byte.
        /// </summary>
        private bool Nests(List<(MemberLayout Member, string Name, Shape Shape)> members) =>
            members.Any(m => Opens(m.Shape));

        /// <summary>
        /// Whether a struct can hold itself without a class body between (through collections, nullables,
        /// <c>object</c> or interfaces): its nesting is counted (<c>Reader.EnterStruct</c> in .NET), so deep data throws
        /// instead of overflowing the stack.
        /// </summary>
        private bool HoldsItself(Type type)
        {
            var seen = new HashSet<Type>();
            var pending = new Stack<Type>();
            pending.Push(type);
            while (pending.Count != 0)
            {
                var next = pending.Pop();
                if (!_supported.Contains(next))
                    continue;
                foreach (var reached in Members(_layouts[next]).SelectMany(m => m.Shape.Reaches))
                {
                    if (reached == type)
                        return true;
                    if (seen.Add(reached))
                        pending.Push(reached);
                }
            }

            return false;
        }

        private readonly HashSet<Type> _warnedDeferrable = new();

        /// <summary>
        /// Whether .NET defers the body of the class <paramref name="type"/> past 128 open bodies (else it reads and
        /// writes it in place, up to 512): <c>Deferrable</c> in its published layout. A class built by its
        /// constructor, or compared by value, is not deferred. A layout from a generator older than the property
        /// leaves it out: such a class is taken to be deferred, as most are.
        /// </summary>
        private bool Deferrable(Type type)
        {
            if (_layouts.TryGetValue(type, out var layout) && layout.Deferrable is { } deferrable)
                return deferrable;
            if (_warnedDeferrable.Add(type))
                Warnings.Add($"{type.FullName}: its layout does not say whether it is deferrable (built with an " +
                             "older Nino); its bodies are taken to be deferred past 128 nested bodies, as most " +
                             "classes are.");
            return true;
        }

        // ---------------------------------------------------------------- CLR helpers

        private static Type? Implements(Type type, string definition)
        {
            if (type.IsGenericType && type.GetGenericTypeDefinition().FullName == definition)
                return type;
            return type.GetInterfaces().FirstOrDefault(i =>
                i.IsGenericType && i.GetGenericTypeDefinition().FullName == definition);
        }

        private static Type FieldType(Type type, string name)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var field = type.GetField(name, flags) ?? type.GetField($"<{name}>k__BackingField", flags) ??
                throw new UnsupportedException($"field {name} of {type.FullName} was not found");
            return field.FieldType;
        }

        private static bool Flags(Type type) =>
            type.GetCustomAttributesData().Any(a => a.AttributeType.FullName == "System.FlagsAttribute");

        private static int Depth(Type type)
        {
            int depth = 0;
            for (var t = type.BaseType; t != null; t = t.BaseType)
                depth++;
            return depth;
        }

        private static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);
        private static string Long(long value) => value.ToString(CultureInfo.InvariantCulture);

        // ---------------------------------------------------------------- names

        /// <summary>Types are named as in C# (nested and generic types joined with _), or with their namespace when two share a name.</summary>
        private void AssignNames(IEnumerable<Type> types)
        {
            var all = types.Distinct().ToList();
            var simple = all.ToLookup(Simple);
            foreach (var type in all)
            {
                string name = Simple(type);
                if (simple[name].Count() > 1)
                    name = (type.Namespace ?? "").Replace('.', '_') + "_" + name;
                _names[type] = name;
            }
        }

        private static string Simple(Type type)
        {
            string name = type.Name;
            int tick = name.IndexOf('`');
            if (tick >= 0)
                name = name.Substring(0, tick);
            if (type.IsGenericType)
                name += "_" + string.Join("_", type.GetGenericArguments().Select(Simple));
            return type.IsNested ? Simple(type.DeclaringType!) + "_" + name : name;
        }

        private string Name(Type type) => _names.TryGetValue(type, out var name) ? name : Simple(type);

        private string Member(string name)
        {
            if (!_options.CamelCase || name.Length == 0 || !char.IsUpper(name[0]))
                return name;
            int upper = 0;
            while (upper < name.Length && char.IsUpper(name[upper]))
                upper++;
            // "URL" -> "url", "URLPath" -> "urlPath", "Id" -> "id".
            int lower = upper == name.Length ? upper : Math.Max(1, upper - 1);
            return string.Concat(name.Substring(0, lower).ToLowerInvariant(), name.AsSpan(lower));
        }

        /// <summary>A function shared by every value of one encoding, written once after the types.</summary>
        private string Helper(string key, Func<string, string> body)
        {
            if (_helperNames.TryGetValue(key, out var name))
                return name;
            name = (key.StartsWith("read", StringComparison.Ordinal) ? "read$" : "write$") + Int(_helperNames.Count);
            _helperNames[key] = name;
            _helpers.Append(body(name)).Append('\n');
            return name;
        }

        // ---------------------------------------------------------------- emission

        private void EmitEnum(StringBuilder sb, Type type)
        {
            string name = Name(type);
            bool big = Primitive(type.GetEnumUnderlyingType())!.Ts == "bigint";
            sb.Append("/** `").Append(type.FullName).Append("`. */\n");
            sb.Append("export const ").Append(name).Append(" = {\n");
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static))
            {
                string value = Convert.ToString(field.GetRawConstantValue(), CultureInfo.InvariantCulture)!;
                sb.Append("  ").Append(field.Name).Append(": ").Append(value).Append(big ? "n" : "").Append(",\n");
            }

            sb.Append("} as const;\n");
            // A flags value is any combination of them.
            sb.Append("export type ").Append(name).Append(" = ")
                .Append(Flags(type) ? big ? "bigint" : "number" : $"(typeof {name})[keyof typeof {name}]")
                .Append(";\n\n");
        }

        private void EmitRawStruct(StringBuilder sb, Type type, WireEncoding e)
        {
            string name = Name(type);
            var fields = e.Fields.Select(f => (f, Member(f.Name), RawShape(f.Encoding, FieldType(type, f.Name))))
                .ToList();
            sb.Append("/** `").Append(type.FullName).Append("`, copied as raw memory (").Append(e.Size)
                .Append(" bytes). */\n");
            sb.Append("export class ").Append(name).Append(" {\n");
            foreach (var (_, member, shape) in fields)
                sb.Append("  ").Append(member).Append(": ").Append(shape.Ts).Append(" = ").Append(shape.Default)
                    .Append(";\n");
            sb.Append("}\n\n");

            // Each field at its offset of the bytes the caller took (zeroed, if the struct has padding).
            sb.Append("function read").Append(name).Append("(r: NinoReader, at: number): ").Append(name).Append(" {\n")
                .Append("  const v = new ").Append(name).Append("();\n");
            foreach (var (f, member, shape) in fields)
                sb.Append("  v.").Append(member).Append(" = ").Append(shape.ReadAt!("r", Plus("at", f.Offset)))
                    .Append(";\n");
            sb.Append("  return v;\n}\n\n");

            sb.Append("function write").Append(name).Append("(w: NinoWriter, at: number, v: ").Append(name)
                .Append("): void {\n");
            foreach (var (f, member, shape) in fields)
                sb.Append("  ").Append(Statement(shape.WriteAt!("w", Plus("at", f.Offset), "v." + member)))
                    .Append('\n');
            sb.Append("}\n\n");
        }

        /// <summary>Whether the fields of a raw struct fill it with no gap: each starts where the one before ends.</summary>
        private static bool Packed(WireEncoding e)
        {
            int end = 0;
            foreach (var field in e.Fields)
            {
                if (field.Offset != end || field.Encoding.Size < 0)
                    return false;
                end += field.Encoding.Size;
            }

            return end == e.Size;
        }

        /// <summary>The supported layout of the nearest base class, whose TypeScript class this one extends.</summary>
        private TypeLayout? BaseLayout(Type type)
        {
            for (var t = type.BaseType; t != null; t = t.BaseType)
            {
                if (_layouts.TryGetValue(t, out var layout) && _supported.Contains(t))
                    return layout;
            }

            return null;
        }

        /// <summary>The members of a layout, with their TypeScript names and shapes.</summary>
        private List<(MemberLayout Member, string Name, Shape Shape)> Members(TypeLayout layout)
        {
            var used = new Dictionary<string, int>(StringComparer.Ordinal);
            var list = new List<(MemberLayout, string, Shape)>();
            foreach (var member in layout.Members)
            {
                string name = Member(member.Name);
                // A member hidden with `new` and the one hiding it: the hidden one keeps the name.
                used[name] = used.TryGetValue(name, out int count) ? count + 1 : 0;
                if (used[name] != 0)
                    name += "_" + Int(used[name]);
                list.Add((member, name, ShapeOf(member.Encoding, member.Type!)));
            }

            return list;
        }

        private void EmitInterface(StringBuilder sb, TypeLayout layout)
        {
            string name = Name(layout.Type);
            sb.Append("/** `").Append(layout.Type.FullName).Append("`. */\n");
            sb.Append("export interface ").Append(name).Append(" {\n");
            foreach (var (_, member, shape) in Members(layout))
                sb.Append("  ").Append(member).Append(": ").Append(shape.Ts).Append(";\n");
            sb.Append("}\n\n");
        }

        private void EmitClass(StringBuilder sb, TypeLayout layout)
        {
            var type = layout.Type;
            string name = Name(type);
            var baseLayout = BaseLayout(type);
            var members = Members(layout);
            // The members of the base class (and its bases) are declared there.
            var own = members.Where(m => baseLayout == null || m.Member.DeclaringType == null ||
                                         !m.Member.DeclaringType.IsAssignableFrom(baseLayout.Type)).ToList();
            var interfaces = type.GetInterfaces()
                .Where(i => _layouts.ContainsKey(i) && _supported.Contains(i)).Select(Name).ToList();

            sb.Append("/** `").Append(type.FullName).Append("`. */\n");
            sb.Append("export ").Append(type.IsAbstract ? "abstract " : "").Append("class ").Append(name);
            if (baseLayout != null)
                sb.Append(" extends ").Append(Name(baseLayout.Type));
            if (interfaces.Count != 0)
                sb.Append(" implements ").Append(string.Join(", ", interfaces));
            sb.Append(" {\n");
            // In a class hierarchy, V8 creates objects about twice as fast from constructors as from field
            // initializers (one initializer per class), so the members are declared and set in the constructor there.
            bool hierarchy = baseLayout != null || _layouts.Values.Any(l =>
                !l.Type.IsInterface && _supported.Contains(l.Type) && BaseLayout(l.Type) == layout);
            if (!hierarchy)
            {
                foreach (var (_, member, shape) in own)
                    sb.Append("  ").Append(member).Append(": ").Append(shape.Ts).Append(" = ").Append(shape.Default)
                        .Append(";\n");
            }
            else if (own.Count != 0)
            {
                foreach (var (_, member, shape) in own)
                    sb.Append("  declare ").Append(member).Append(": ").Append(shape.Ts).Append(";\n");
                sb.Append("\n  constructor() {\n").Append(baseLayout != null ? "    super();\n" : "");
                foreach (var (_, member, shape) in own)
                    sb.Append("    this.").Append(member).Append(" = ").Append(shape.Default).Append(";\n");
                sb.Append("  }\n");
            }

            sb.Append("}\n\n");
            if (!type.IsAbstract)
                EmitBody(sb, layout, name, members);
        }

        /// <summary>
        /// <c>readX</c> and <c>writeX</c>: the body of a type, without its header (positional members, or a version
        /// tolerant or keyed region).
        /// </summary>
        private void EmitBody(StringBuilder sb, TypeLayout layout, string name,
            List<(MemberLayout Member, string Name, Shape Shape)> members)
        {
            // A class body counts the depth and is deferred past 128 open bodies (see "Deep nesting" in
            // docs/v4-design.md), as in .NET: its members are read and written by readBody$X and writeBody$X, which
            // the deferred bodies are read and written with later. A struct that can hold itself counts its own nesting.
            bool body = !layout.Type.IsValueType;
            bool counted = !body && HoldsItself(layout.Type);
            if (body)
            {
                bool nests = Nests(members);
                string later = Deferrable(layout.Type) ? "defer" : "deep";
                sb.Append("function read").Append(name).Append("(r: NinoReader): ").Append(name).Append(" {\n")
                    .Append("  const v = new ").Append(name).Append("()").Append(nests ? ", depth = r.depth" : "")
                    .Append(";\n  if (").Append(nests ? "depth" : "r.depth").Append(" >= 128) return r.").Append(later)
                    .Append("(v, readBody$").Append(name).Append(");\n");
                if (nests)
                    sb.Append("  r.depth = depth + 1;\n  readBody$").Append(name).Append("(r, v);\n  r.exit(depth);\n");
                else
                    sb.Append("  readBody$").Append(name).Append("(r, v);\n");
                sb.Append("  return v;\n}\n\n");

                sb.Append("function write").Append(name).Append("(w: NinoWriter, v: ").Append(name).Append("): void {\n");
                if (nests)
                    sb.Append("  const depth = w.depth;\n  if (depth >= 128) {\n    w.").Append(later)
                        .Append("(v, writeBody$").Append(name).Append(");\n    return;\n  }\n")
                        .Append("  w.depth = depth + 1;\n  writeBody$").Append(name).Append("(w, v);\n  w.exit(depth);\n");
                else
                    sb.Append("  if (w.depth >= 128) w.").Append(later).Append("(v, writeBody$").Append(name)
                        .Append(");\n  else writeBody$").Append(name).Append("(w, v);\n");
                sb.Append("}\n\n");

                sb.Append("function readBody$").Append(name).Append("(r: NinoReader, v: ").Append(name)
                    .Append("): void {\n");
            }
            else
            {
                sb.Append("function read").Append(name).Append("(r: NinoReader): ").Append(name).Append(" {\n");
                if (counted)
                    sb.Append("  r.enterStruct();\n");
                sb.Append("  const v = new ").Append(name).Append("();\n");
            }

            switch (layout.Format)
            {
                case TypeLayout.Keyed:
                    // Members by key, in any order; a key this version does not know is skipped.
                    sb.Append("  const end = r.region();\n  while (r.pos < end) {\n")
                        .Append("    const key = r.i32(), member = r.region();\n    switch (key) {\n");
                    foreach (var (m, member, shape) in members)
                    {
                        foreach (int key in m.FormerKeys.Prepend(m.Key))
                            sb.Append("      case ").Append(Int(key)).Append(":\n");
                        sb.Append("        v.").Append(member).Append(" = ").Append(shape.Read("r"))
                            .Append(";\n        break;\n");
                    }

                    sb.Append("    }\n    r.endRegion(member);\n  }\n  r.endRegion(end);\n");
                    break;
                case TypeLayout.VersionTolerant:
                    // Members written by an older version end early; those of a newer one are skipped.
                    sb.Append("  const end = r.region();\n");
                    foreach (var (_, member, shape) in members)
                        sb.Append("  if (r.pos < end) v.").Append(member).Append(" = ").Append(shape.Read("r"))
                            .Append(";\n");
                    sb.Append("  r.endRegion(end);\n");
                    break;
                default:
                {
                    // Each run of fixed-size members takes its bytes at once and reads them at their offsets.
                    var runs = Runs(members);
                    string at = runs.Count(r => r.Count > 1) > 1 ? "let at" : "const at";
                    foreach (var run in runs)
                    {
                        if (run.Count == 1)
                        {
                            sb.Append("  v.").Append(run[0].Name).Append(" = ").Append(run[0].Shape.Read("r"))
                                .Append(";\n");
                            continue;
                        }

                        sb.Append("  ").Append(at).Append(" = r.take(").Append(Int(run.Sum(m => m.Shape.Size)))
                            .Append(");\n");
                        at = "at";
                        int offset = 0;
                        foreach (var (_, member, shape) in run)
                        {
                            sb.Append("  v.").Append(member).Append(" = ")
                                .Append(shape.ReadAt!("r", Plus("at", offset))).Append(";\n");
                            offset += shape.Size;
                        }
                    }

                    break;
                }
            }

            if (counted)
                sb.Append("  r.exitStruct();\n");
            sb.Append(body ? "}\n\n" : "  return v;\n}\n\n");

            sb.Append(body ? "function writeBody$" : "function write").Append(name).Append("(w: NinoWriter, v: ")
                .Append(name).Append("): void {\n");
            if (counted)
                sb.Append("  w.enterStruct();\n");
            if (layout.Format != TypeLayout.Positional)
                sb.Append("  const start = w.region();\n");
            if (layout.Format == TypeLayout.Keyed)
            {
                foreach (var (m, member, shape) in members)
                    sb.Append("  w.i32(").Append(Int(m.Key)).Append(");\n  const ").Append(member)
                        .Append("$ = w.region();\n  ").Append(Statement(shape.Write("w", "v." + member)))
                        .Append("\n  w.endRegion(").Append(member).Append("$);\n");
            }
            else
            {
                // Each run of fixed-size members reserves its bytes at once and writes them at their offsets.
                var runs = Runs(members);
                string at = runs.Count(r => r.Count > 1) > 1 ? "let at" : "const at";
                foreach (var run in runs)
                {
                    if (run.Count == 1)
                    {
                        sb.Append("  ").Append(Statement(run[0].Shape.Write("w", "v." + run[0].Name))).Append('\n');
                        continue;
                    }

                    sb.Append("  ").Append(at).Append(" = ")
                        .Append(Reserve("w", run.Sum(m => m.Shape.Size), run.Any(m => m.Shape.Zeroed))).Append(";\n");
                    at = "at";
                    int offset = 0;
                    foreach (var (_, member, shape) in run)
                    {
                        sb.Append("  ").Append(Statement(shape.WriteAt!("w", Plus("at", offset), "v." + member)))
                            .Append('\n');
                        offset += shape.Size;
                    }
                }
            }

            if (layout.Format != TypeLayout.Positional)
                sb.Append("  w.endRegion(start);\n");
            if (counted)
                sb.Append("  w.exitStruct();\n");
            sb.Append("}\n\n");
        }

        /// <summary>The members in runs: fixed-size members next to one another together, the others alone.</summary>
        private static List<List<(MemberLayout Member, string Name, Shape Shape)>> Runs(
            List<(MemberLayout Member, string Name, Shape Shape)> members)
        {
            var runs = new List<List<(MemberLayout, string, Shape)>>();
            foreach (var member in members)
            {
                if (member.Shape.Fixed && runs.Count != 0 && runs[^1][^1].Item3.Fixed)
                    runs[^1].Add(member);
                else
                    runs.Add(new List<(MemberLayout, string, Shape)> { member });
            }

            return runs;
        }

        /// <summary><c>XCodec</c>: the type with its header, as a member of its type or <c>NinoSerializer</c> writes it.</summary>
        private void EmitCodec(StringBuilder sb, TypeLayout layout)
        {
            string name = Name(layout.Type);
            sb.Append("export const ").Append(name).Append("Codec: NinoCodec<").Append(name);
            switch (layout.Header)
            {
                case TypeLayout.NoHeader when layout.Raw:
                {
                    var shape = RawShape(layout.RawEncoding!, layout.Type);
                    sb.Append("> = {\n  read: (r) => ").Append(shape.Read("r")).Append(",\n  write: (w, v) => ")
                        .Append(shape.Write("w", "v")).Append(",\n};\n");
                    break;
                }
                case TypeLayout.NoHeader:
                    sb.Append("> = { read: read").Append(name).Append(", write: write").Append(name).Append(" };\n");
                    break;
                case TypeLayout.NullFlag:
                {
                    var shape = TypeShape(layout.Type);
                    sb.Append(" | null> = {\n  read: (r) => ").Append(shape.Read("r"))
                        .Append(",\n  write: (w, v) => {\n    ").Append(Statement(shape.Write("w", "v")))
                        .Append("\n  },\n};\n");
                    break;
                }
                default:
                {
                    // Any type assignable to it, by the type id in front of its body: by its class when written.
                    var types = Registered(_supported.Where(t => layout.Type.IsAssignableFrom(t))).ToList();
                    sb.Append(" | null> = {\n  read: (r) => {\n    const id = r.i32();\n    switch (id) {\n")
                        .Append("      case 0:\n        return null;\n");
                    foreach (var type in types)
                        sb.Append("      case ").Append(Int(_layouts[type].Id)).Append(":\n        return ")
                            .Append(ReadBody(type, "r")).Append(";\n");
                    sb.Append("    }\n    throw types.unreadable(id);\n  },\n")
                        .Append("  write: (w, v) => {\n    if (v === null || v === undefined) {\n      w.i32(0);\n")
                        .Append("      return;\n    }\n    switch (v.constructor) {\n");
                    foreach (var type in types)
                        sb.Append("      case ").Append(Name(type)).Append(":\n        w.i32(")
                            .Append(Int(_layouts[type].Id)).Append(");\n        ")
                            .Append(Statement(WriteBody(type, "w", $"v as {Name(type)}")))
                            .Append("\n        return;\n");
                    sb.Append("    }\n    throw types.unwritable(v);\n  },\n};\n");
                    break;
                }
            }
        }

        /// <summary>An expression reading the body of a type written behind a type id (without its header).</summary>
        private string ReadBody(Type type, string r) =>
            _layouts[type].Raw ? RawShape(_layouts[type].RawEncoding!, type).Read(r) : $"read{Name(type)}({r})";

        /// <summary>A statement writing the body of a type written behind a type id.</summary>
        private string WriteBody(Type type, string w, string v) => _layouts[type].Raw
            ? RawShape(_layouts[type].RawEncoding!, type).Write(w, v)
            : $"write{Name(type)}({w}, {v})";
    }
}
