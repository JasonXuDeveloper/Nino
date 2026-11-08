using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Nino.Codegen.Model;

namespace Nino.Codegen.Inspect
{
    /// <summary>What <see cref="Inspector.Read"/> read: the tree, where it stopped and why, if it failed.</summary>
    internal sealed record InspectResult(InspectNode Root, InspectException? Error, int End);

    /// <summary>
    /// Reads one message with the layouts the model holds (the IR every nino-gen target writes code from), as Nino.Core
    /// reads it, into a tree of what each byte is: headers, members, elements, keyed entries, type ids and references,
    /// deferred bodies. It makes the checks a reader in untrusted mode makes (wire-format spec §9) and stops at the
    /// first that fails; nesting is bounded as .NET bounds it (§7.10, §9.9), so no input makes it recurse without end,
    /// and no count is believed beyond the bytes left (§9.2).
    /// </summary>
    internal sealed class Inspector
    {
        /// <summary>Open class bodies after which a deferrable body is deferred (<c>NinoDepth.Inline</c>).</summary>
        private const int InlineDepth = 128;

        /// <summary>Open bodies past which reading fails (<c>NinoDepth.Max</c>, <c>NinoSettings.MaxDepth</c>'s default).</summary>
        private const int MaxDepth = 512;

        /// <summary>Elements of a block of numbers shown in the text; JSON has all.</summary>
        private const int ShownElements = 32;

        /// <summary>Chars of a string shown in the text; JSON has all.</summary>
        private const int ShownChars = 200;

        private readonly CodegenModel _model;
        private readonly WireReader _r;

        // Depth (§7.10.1): open class bodies, and open bodies of structs and collections that can hold themselves.
        private int _depth, _structs;

        // Regions (§7.5, §7.6) open, and where the outermost one's section of deferred bodies ends (0: none).
        private int _regions, _sectionEnd;

        // Bodies deferred outside regions and inside the outermost region.
        private readonly List<Deferred> _top = new(), _region = new();
        private int _topHead, _regionHead;
        private bool _topActive;

        /// <summary>The types of the tracked objects read so far: a back reference n names the n-th (§7.9).</summary>
        private readonly List<Type> _objects = new();

        /// <summary>Where the value about to be read must end (a keyed entry's end), so an opaque one can be skipped.</summary>
        private int _opaqueEnd = -1;

        /// <summary>
        /// Values read, and the most there may be: a value of no bytes (the body of a struct with no members) repeated
        /// by counts the bytes allow could otherwise make the tree grow with the square of the input.
        /// </summary>
        private int _values;
        private readonly long _maxValues;

        private Dictionary<int, List<Body>>? _byId;
        private readonly Dictionary<TypeModel, bool> _counted = new();

        private Inspector(CodegenModel model, byte[] data, int offset)
        {
            _model = model;
            _r = new WireReader(data, offset);
            _maxValues = 16L * (data.Length - offset) + 4096;
        }

        /// <summary>The type arguments a generic type's body is read with: a value each, and where it was written.</summary>
        private sealed record Env(List<Arg> Args);

        private sealed record Arg(ValueModel Value, Env? Env);

        /// <summary>A type whose body is read: its layout, its type arguments (generic), its closed .NET type.</summary>
        private sealed record Body(TypeModel Type, Env? Env, System.Type Clr)
        {
            public string Name => TypeNames.Runtime(Clr);
        }

        /// <summary>A body deferred where it would have begun (<see cref="Position"/>), and the node of its slot.</summary>
        private sealed record Deferred(Body Body, InspectNode Node, int Position);

        /// <summary>Reads a value of <paramref name="root"/> (a <paramref name="rootType"/>) at <paramref name="offset"/>.</summary>
        public static InspectResult Read(CodegenModel model, ValueModel root, Type rootType, byte[] data, int offset)
        {
            var inspector = new Inspector(model, data, offset);
            var node = new InspectNode("value", offset) { Type = TypeNames.Runtime(rootType) };
            try
            {
                inspector.Value(root, null, node);
                return new InspectResult(node, null, inspector._r.Pos);
            }
            catch (InspectException e)
            {
                return new InspectResult(node, e, inspector._r.Pos);
            }
        }

        private InspectException Fail(string message) => _r.Fail(message);

        private static InspectException TooDeep(int at) =>
            WireReader.Fail(at, $"nested deeper than {MaxDepth} bodies");

        // ---------------------------------------------------------------- values

        private void Value(ValueModel v, Env? env, InspectNode node)
        {
            int opaqueEnd = _opaqueEnd;
            _opaqueEnd = -1;
            if (++_values > _maxValues)
                throw Fail($"more than {_maxValues} values in {_r.Length} bytes: values of no bytes repeated");
            try
            {
                switch (v.Kind)
                {
                    case ValueKind.Primitive:
                    case ValueKind.Enum:
                    case ValueKind.RawStruct:
                    case ValueKind.RawNullable:
                    case ValueKind.RawTuple:
                    {
                        int at = _r.Pos;
                        _r.Take(v.Size);
                        Raw(v, at, node);
                        break;
                    }
                    case ValueKind.String:
                        String(node);
                        break;
                    case ValueKind.Utf8:
                        Utf8(node);
                        break;
                    case ValueKind.Object:
                        Object(null, node);
                        break;
                    case ValueKind.Type:
                        Object(new Body(v.Type!, v.Type!.IsGenericDefinition ? env : null, ClrOf(v, env)), node);
                        break;
                    case ValueKind.Generic:
                        Object(new Body(v.Type!, new Env(v.Arguments.Select(a => new Arg(a, env)).ToList()),
                            ClrOf(v, env)), node);
                        break;
                    case ValueKind.Parameter:
                    {
                        // The type argument's value, in the place of the type parameter.
                        if (env == null)
                            throw Fail($"cannot read {v.ParameterName}: no type argument is known for it");
                        var argument = env.Args[v.Parameter];
                        _opaqueEnd = opaqueEnd;
                        Value(argument.Value, argument.Env, node);
                        break;
                    }
                    case ValueKind.Named:
                        Named(v, node);
                        break;
                    case ValueKind.Nullable:
                        if (Presence(node))
                            Value(v.Element!, env, node);
                        break;
                    case ValueKind.Sequence:
                        Sequence(v, env, node, false);
                        break;
                    case ValueKind.Map:
                        Map(v, env, node, false);
                        break;
                    case ValueKind.Tuple:
                        for (int i = 0; i < v.Items.Count; i++)
                            Value(v.Items[i], env, Item(node, v, i, _r.Pos));
                        break;
                    case ValueKind.Grid:
                        Grid(v, env, node);
                        break;
                    case ValueKind.BigInteger:
                    {
                        int at = _r.Pos;
                        int length = _r.I32();
                        if (length < 0)
                            throw WireReader.Fail(at, $"invalid length {length}");
                        var (text, json) = Scalars.BigInteger(_r.Take(length));
                        node.Set(text, json);
                        break;
                    }
                    case ValueKind.Version:
                        Version(node);
                        break;
                    case ValueKind.DateTimeOffset:
                    {
                        int at = _r.Pos;
                        var bytes = _r.Take(10);
                        var (text, json) = Scalars.DateTimeOffset(BinaryPrimitives.ReadInt64LittleEndian(bytes),
                            BinaryPrimitives.ReadInt16LittleEndian(bytes.Slice(8)), at);
                        node.Set(text, json);
                        break;
                    }
                    default:
                        Unsupported(v, node, opaqueEnd);
                        break;
                }
            }
            finally
            {
                node.Length = _r.Pos - node.Offset;
            }
        }

        /// <summary>A tuple's item: <c>Key</c> and <c>Value</c> of a pair, <c>Item1</c>... of a tuple.</summary>
        private static InspectNode Item(InspectNode node, ValueModel tuple, int index, int offset)
        {
            var child = node.Add(tuple.IsPair ? index == 0 ? "Key" : "Value" : "Item" + (index + 1), offset);
            child.Type = TypeNames.Runtime(tuple.Items[index].Clr);
            return child;
        }

        /// <summary>The closed .NET type of a value read with <paramref name="env"/>'s type arguments.</summary>
        private static Type ClrOf(ValueModel v, Env? env)
        {
            if (v.Kind == ValueKind.Parameter && env != null)
            {
                var argument = env.Args[v.Parameter];
                return ClrOf(argument.Value, argument.Env);
            }

            if (env == null || !v.Clr.ContainsGenericParameters)
                return v.Clr;
            return CodegenModel.Substitute(v.Clr, env.Args.Select(a => ClrOf(a.Value, a.Env)).ToArray());
        }

        /// <summary>
        /// A presence byte (§2.4): false, with the node null, when absent. An object's (<paramref name="header"/>) is a
        /// node of its own; a nullable value's is part of the value's node.
        /// </summary>
        private bool Presence(InspectNode node, bool header = false)
        {
            int at = _r.Pos;
            byte flag = _r.U8();
            if (flag > 1)
                throw WireReader.Fail(at, $"invalid presence byte {flag}");
            if (header)
            {
                var child = node.Add("presence", at);
                child.Length = 1;
                child.Set(flag.ToString(CultureInfo.InvariantCulture), flag.ToString(CultureInfo.InvariantCulture));
            }

            if (flag == 0)
                node.SetNull();
            return flag == 1;
        }

        // ---------------------------------------------------------------- raw values

        /// <summary>A raw value at <paramref name="at"/>, whose bytes have been taken already.</summary>
        private void Raw(ValueModel v, int at, InspectNode node)
        {
            var data = _r.Data;
            switch (v.Kind)
            {
                case ValueKind.Primitive:
                {
                    var (text, json) = Scalars.Primitive(data, at, v.Primitive);
                    node.Set(text, json);
                    break;
                }
                case ValueKind.Enum:
                {
                    var (text, json) = Scalars.Primitive(data, at, v.Primitive);
                    var constant = v.Enum!.Flags ? null : v.Enum.Constants.FirstOrDefault(c => c.Value == text);
                    node.Set(constant == null ? text : $"{text} ({constant.Name})", json);
                    break;
                }
                case ValueKind.RawStruct:
                {
                    var model = v.Struct!;
                    // DateTimeOffset's canonical fields (§5.2): the UTC ticks of `dateTime`, the minutes at `offsetMinutes`.
                    if (model.FullName == "System.DateTimeOffset" &&
                        model.Fields.FirstOrDefault(f => f.Name == "dateTime") is { } ticks &&
                        model.Fields.FirstOrDefault(f => f.Name == "offsetMinutes") is { } minutes)
                    {
                        var (text, json) = Scalars.DateTimeOffset(
                            (long)(BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(at + ticks.Offset)) &
                                   0x3FFFFFFFFFFFFFFFUL),
                            BinaryPrimitives.ReadInt32LittleEndian(data.Slice(at + minutes.Offset)), at);
                        node.Set(text, json);
                        break;
                    }

                    if (model.Empty)
                        node.Set("{}", "{}");
                    foreach (var field in model.Fields)
                        RawChild(node, field.Name, field.Value, at + field.Offset);
                    break;
                }
                case ValueKind.RawNullable:
                {
                    byte flag = data[at + v.FlagOffset];
                    if (flag > 1)
                        throw WireReader.Fail(at + v.FlagOffset, $"invalid presence byte {flag}");
                    if (flag == 0)
                        node.SetNull();
                    else
                        Raw(v.Element!, at + v.ValueOffset, node);
                    break;
                }
                case ValueKind.RawTuple:
                    for (int i = 0; i < v.Items.Count; i++)
                        RawChild(node, v.IsPair ? i == 0 ? "Key" : "Value" : "Item" + (i + 1), v.Items[i],
                            at + v.Offsets[i]);
                    break;
                default:
                {
                    // A field no layout describes: its bytes, when its size is known.
                    int size = KnownSize(v) ?? 0;
                    var (text, json) = Scalars.Hex(data.Slice(at, size));
                    node.Set(text, json);
                    node.AddNote("not decoded: " + Reason(v));
                    break;
                }
            }
        }

        private void RawChild(InspectNode node, string name, ValueModel value, int at)
        {
            var child = node.Add(name, at);
            child.Type = TypeNames.Runtime(value.Clr);
            child.Length = Math.Max(KnownSize(value) ?? 0, 0);
            Raw(value, at, child);
        }

        /// <summary>The bytes a value always takes, from the model or its <c>raw(n)</c> encoding; null when unknown.</summary>
        private static int? KnownSize(ValueModel v)
        {
            if (v.Size >= 0)
                return v.Size;
            string e = v.Encoding;
            if (!e.StartsWith("raw(", StringComparison.Ordinal))
                return null;
            int close = e.IndexOf(')');
            return int.TryParse(e.AsSpan(4, close - 4), NumberStyles.None, CultureInfo.InvariantCulture, out int n)
                ? n
                : null;
        }

        /// <summary>The fewest bytes a value takes: what a count is checked against (§9.2).</summary>
        private static int MinimumSize(ValueModel v, Env? env)
        {
            if (v.Kind == ValueKind.Parameter && env != null)
                return MinimumSize(env.Args[v.Parameter].Value, env.Args[v.Parameter].Env);
            return Math.Max(v.Size, 1);
        }

        // ---------------------------------------------------------------- strings

        private void String(InspectNode node)
        {
            int at = _r.Pos;
            uint header = _r.Header();
            if (header == 0)
            {
                node.SetNull();
                return;
            }

            long chars = header >> 1;
            bool latin1 = (header & 1) == 1;
            long bytes = latin1 ? chars : chars * 2;
            if (bytes > _r.Remaining)
                throw WireReader.Fail(at, $"string of {chars} char(s) ({bytes} bytes) runs past the end: " +
                                          $"{_r.Remaining} byte(s) left");
            var span = _r.Take((int)bytes);
            // UTF-16 as it is: a lone surrogate is kept, not replaced (§4.1).
            string text = latin1
                ? Encoding.Latin1.GetString(span)
                : new string(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, char>(span));
            SetString(node, text, latin1 ? "Latin-1" : "UTF-16");
        }

        private void Utf8(InspectNode node)
        {
            int at = _r.Pos;
            uint header = _r.Header();
            if (header <= 1)
            {
                if (header == 0)
                    node.SetNull();
                else
                    SetString(node, "", "UTF-8");
                return;
            }

            long chars = header - 1L;
            uint count = _r.Header();
            if (chars > int.MaxValue || count > int.MaxValue || count < chars || count > 3 * chars)
                throw WireReader.Fail(at, $"invalid UTF-8 string: {chars} char(s) in {count} byte(s)");
            if (count > _r.Remaining)
                throw WireReader.Fail(at, $"UTF-8 string of {count} bytes runs past the end: {_r.Remaining} left");
            string text;
            try
            {
                text = Strict.GetString(_r.Take((int)count));
            }
            catch (DecoderFallbackException)
            {
                throw WireReader.Fail(at, "invalid UTF-8 bytes");
            }

            if (text.Length != chars)
                throw WireReader.Fail(at, $"UTF-8 string of {text.Length} char(s), not the {chars} its header says");
            SetString(node, text, "UTF-8");
        }

        private static readonly UTF8Encoding Strict = new(false, true);

        private static void SetString(InspectNode node, string text, string form)
        {
            string json = InspectNode.JsonString(text);
            node.Set(text.Length <= ShownChars
                ? json
                : InspectNode.JsonString(text.Substring(0, ShownChars)) + $" ... ({text.Length} chars)", json);
            node.AddNote($"{form}, {text.Length} char(s)");
        }

        // ---------------------------------------------------------------- collections

        private void Sequence(ValueModel v, Env? env, InspectNode node, bool elements)
        {
            var element = v.Element!;
            int count = Count(MinimumSize(element, env), elements);
            if (count < 0)
            {
                node.SetNull();
                return;
            }

            node.AddNote(count == 1 ? "1 element" : $"{count} elements");
            bool unique = v.Collection is CollectionKind.HashSet or CollectionKind.SortedSet;
            Elements(element, env, node, count, i => "[" + i.ToString(CultureInfo.InvariantCulture) + "]",
                unique ? "element" : null, v.IsByteArray || v.IsSByteArray);
        }

        /// <summary>A count (§2.4), or the elements of a <c>[NinoType]</c> collection, which are never null.</summary>
        private int Count(int minimum, bool elements)
        {
            int at = _r.Pos;
            int count = _r.Count(minimum);
            if (count == -1 && elements)
                throw WireReader.Fail(at, "a [NinoType] collection's elements are null");
            return count;
        }

        /// <summary>
        /// <paramref name="count"/> elements: numbers as one value (one block of raw bytes), others a node each. With
        /// <paramref name="unique"/> (a set), an element repeated is invalid (§9.8). A <c>byte[]</c> or <c>sbyte[]</c>
        /// (<paramref name="bytes"/>) is shown as hex.
        /// </summary>
        private void Elements(ValueModel element, Env? env, InspectNode node, long count, Func<long, string> label,
            string? unique, bool bytes = false)
        {
            while (element.Kind == ValueKind.Parameter && env != null)
                (element, env) = (env.Args[element.Parameter].Value, env.Args[element.Parameter].Env);
            if (element.Kind is ValueKind.Primitive or ValueKind.Enum && unique == null)
            {
                int at = _r.Pos;
                var block = _r.Take(checked((int)(count * element.Size)));
                if (bytes)
                {
                    var (hex, hexJson) = Scalars.Hex(block);
                    node.Set(hex, hexJson);
                    return;
                }

                var texts = new StringBuilder("[");
                var json = new StringBuilder("[");
                var leaf = new InspectNode("", 0);
                for (int i = 0; i < count; i++)
                {
                    Raw(element, at + i * element.Size, leaf);
                    if (i < ShownElements)
                        texts.Append(i == 0 ? "" : ", ").Append(leaf.Value);
                    json.Append(i == 0 ? "" : ",").Append(leaf.Json);
                }

                if (count > ShownElements)
                    texts.Append(", ... (").Append(count - ShownElements).Append(" more)");
                node.Set(texts.Append(']').ToString(), json.Append(']').ToString());
                return;
            }

            var seen = unique == null ? null : new HashSet<string>(StringComparer.Ordinal);
            for (long i = 0; i < count; i++)
            {
                var child = node.Add(label(i), _r.Pos);
                Value(element, env, child);
                if (seen != null && !seen.Add(child.Canonical()))
                    throw WireReader.Fail(child.Offset, $"{unique} {child.Canonical()} repeated");
            }
        }

        private void Map(ValueModel v, Env? env, InspectNode node, bool elements)
        {
            int count = Count(MinimumSize(v.Key!, env) + MinimumSize(v.Value!, env), elements);
            if (count < 0)
            {
                node.SetNull();
                return;
            }

            node.AddNote(count == 1 ? "1 entry" : $"{count} entries");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < count; i++)
            {
                var entry = node.Add("[" + i.ToString(CultureInfo.InvariantCulture) + "]", _r.Pos);
                var key = entry.Add("key", _r.Pos);
                key.Type = TypeNames.Runtime(v.Key!.Clr);
                Value(v.Key!, env, key);
                string canonical = key.Canonical();
                if (key.Json == "null")
                    throw WireReader.Fail(key.Offset, "a null key");
                if (!seen.Add(canonical))
                    throw WireReader.Fail(key.Offset, $"key {canonical} repeated");
                var value = entry.Add("value", _r.Pos);
                value.Type = TypeNames.Runtime(v.Value!.Clr);
                Value(v.Value!, env, value);
                entry.Length = _r.Pos - entry.Offset;
            }
        }

        /// <summary>A multi-dimensional array (§6.7, §9.13).</summary>
        private void Grid(ValueModel v, Env? env, InspectNode node)
        {
            int at = _r.Pos;
            var lengths = new int[v.Rank];
            for (int d = 0; d < v.Rank; d++)
            {
                lengths[d] = _r.I32();
                if (d == 0 && lengths[0] == -1)
                {
                    node.SetNull();
                    return;
                }

                if (lengths[d] < 0 || lengths[d] > 0x7FFFFFC7)
                    throw WireReader.Fail(at + 4 * d, $"invalid length {lengths[d]} of dimension {d}");
            }

            long count = 1;
            int minimum = MinimumSize(v.Element!, env);
            foreach (int length in lengths)
            {
                count *= length;
                if (count * minimum > _r.Remaining)
                    throw WireReader.Fail(at, $"invalid lengths {string.Join(" x ", lengths)}: more elements than " +
                                              $"the {_r.Remaining} byte(s) left hold");
            }

            node.AddNote(string.Join(" x ", lengths));
            Elements(v.Element!, env, node, count, i =>
            {
                var index = new long[lengths.Length];
                for (int d = lengths.Length - 1; d >= 0; d--)
                {
                    index[d] = i % lengths[d];
                    i /= lengths[d];
                }

                return "[" + string.Join(",", index) + "]";
            }, null);
        }

        /// <summary>A collection of itself without a layout (<c>class Tree : List&lt;Tree&gt;</c>): each level counted (§9.9).</summary>
        private void Named(ValueModel v, InspectNode node)
        {
            if (!_model.NamedCollections.TryGetValue(v.NamedTypeName ?? "", out var collection))
                throw Fail($"cannot read {v.NamedTypeName}: it has no layout");
            if (++_structs + _depth > MaxDepth)
                throw TooDeep(_r.Pos);
            Value(collection, null, node);
            _structs--;
        }

        private void Version(InspectNode node)
        {
            int at = _r.Pos;
            int major = _r.I32();
            if (major == -1)
            {
                node.SetNull();
                return;
            }

            int minor = _r.I32(), build = _r.I32(), revision = _r.I32();
            if (major < 0 || minor < 0)
                throw WireReader.Fail(at, $"invalid Version {major}.{minor}");
            string text = build < 0 ? $"{major}.{minor}"
                : revision < 0 ? $"{major}.{minor}.{build}"
                : $"{major}.{minor}.{build}.{revision}";
            node.Set(text, $"[{major},{minor},{build},{revision}]");
        }

        /// <summary>
        /// A value no layout describes (<c>formatter(F)</c>: a custom formatter's bytes, §7.11): <c>BitArray</c> and
        /// <c>TimeZoneInfo</c> are read as §3.5 says; any other is skipped as opaque bytes when its length is known (a
        /// keyed entry, the last member of a version-tolerant body, a <c>raw(n)</c>), else reading stops.
        /// </summary>
        private void Unsupported(ValueModel v, InspectNode node, int opaqueEnd)
        {
            int at = _r.Pos;
            switch (v.Encoding)
            {
                case "bits":
                {
                    int bits = _r.I32();
                    if (bits == -1)
                    {
                        node.SetNull();
                        return;
                    }

                    if (bits < 0)
                        throw WireReader.Fail(at, $"invalid length {bits}");
                    var bytes = _r.Take((int)((bits + 7L) / 8));
                    var text = new StringBuilder(bits);
                    for (int i = 0; i < bits; i++)
                        text.Append((bytes[i / 8] >> (i % 8) & 1) == 1 ? '1' : '0');
                    string all = text.ToString();
                    node.Set(bits <= 256 ? all : string.Concat(all.AsSpan(0, 256), $" ... ({bits} bits)"),
                        InspectNode.JsonString(all));
                    return;
                }
                case "timezone":
                {
                    var id = node.Add("Id", at);
                    Value(Model(v, ValueKind.String), null, id);
                    if (id.Json == "null")
                    {
                        node.SetNull();
                        return;
                    }

                    Value(Model(v, ValueKind.String), null, node.Add("custom", _r.Pos));
                    return;
                }
            }

            int? size = KnownSize(v) ?? (opaqueEnd >= 0 ? opaqueEnd - at : null);
            if (size == null)
                throw Fail($"cannot read {node.Name}: {Reason(v)}, and its length is not written, so nothing after " +
                           "it can be read");
            var (hex, json) = Scalars.Hex(_r.Take(size.Value));
            node.Set(hex, json);
            node.AddNote("opaque: " + Reason(v));
        }

        private static ValueModel Model(ValueModel like, ValueKind kind) => new(kind, like.Clr, kind.ToString());

        /// <summary>Why a value is not decoded.</summary>
        private static string Reason(ValueModel v) => v.Encoding.StartsWith("formatter(", StringComparison.Ordinal)
            ? $"written by the custom formatter {v.Encoding.Substring(10, v.Encoding.Length - 11)}"
            : (v.Problem ?? v.Encoding + " is not described by the layout").Replace("{lang}", "decodable");

        // ---------------------------------------------------------------- [NinoType] values

        /// <summary>A value of a <c>[NinoType]</c> declared as <paramref name="declared"/> (null: <c>object</c>), §7.1.</summary>
        private void Object(Body? declared, InspectNode node)
        {
            if (declared is { Type.Header: HeaderKind.None })
            {
                ReadBody(declared, node);
                return;
            }

            if (declared is { Type.Header: HeaderKind.Presence })
            {
                if (declared.Type.TrackReferences ? Reference(node, declared.Clr, true) : Presence(node, true))
                {
                    if (declared.Type.TrackReferences)
                        Track(declared, node);
                    ReadBody(declared, node);
                }

                return;
            }

            // Behind a type id: of a tracked declared type, after the reference header (§7.9).
            bool trackedSlot = declared?.Type.TrackReferences == true;
            if (trackedSlot && !Reference(node, declared!.Clr, true))
                return;
            int at = _r.Pos;
            int id = _r.I32();
            var idNode = node.Add("type id", at);
            idNode.Length = 4;
            idNode.Set(TypeNames.Hex(id), id.ToString(CultureInfo.InvariantCulture));
            if (id == 0)
            {
                if (trackedSlot)
                    throw WireReader.Fail(at, "a null type id after a new object's reference header");
                idNode.Note = "null";
                node.SetNull();
                return;
            }

            var runtime = Resolve(id, declared, at);
            idNode.Note = runtime.Name;
            if (node.Type != runtime.Name)
                node.AddNote(runtime.Name);
            if (!trackedSlot && runtime.Type.TrackReferences && !Reference(node, runtime.Clr, false))
                return;
            if (trackedSlot || runtime.Type.TrackReferences)
                Track(runtime, node);
            ReadBody(runtime, node);
        }

        /// <summary>The type a type id names, which must be one a value of <paramref name="declared"/> can be (§9.6).</summary>
        private Body Resolve(int id, Body? declared, int at)
        {
            _byId ??= TypeIds();
            if (_byId.TryGetValue(id, out var types))
            {
                foreach (var type in types)
                    if (declared == null || CodegenModel.AssignableTo(type.Clr, declared.Clr))
                        return type;
                throw WireReader.Fail(at, $"type id {TypeNames.Hex(id)} is {string.Join(" or ", types.Select(t => t.Name))}, " +
                                          $"not a {declared!.Name}");
            }

            throw WireReader.Fail(at, $"unknown type id {TypeNames.Hex(id)}" +
                                      (declared == null ? "" : $" for a {declared.Name}"));
        }

        /// <summary>
        /// The types a type id can name: every concrete <c>[NinoType]</c> with a layout (of the assembly and those it
        /// references), and the closed generic types the assembly names or its polymorphic slots need.
        /// </summary>
        private Dictionary<int, List<Body>> TypeIds()
        {
            var ids = new Dictionary<int, List<Body>>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            void Add(int id, Body body)
            {
                if (!seen.Add(body.Name))
                    return;
                if (!ids.TryGetValue(id, out var list))
                    ids[id] = list = new List<Body>();
                list.Add(body);
            }

            foreach (var type in _model.Types.Where(t => t.TypeId != 0 && t.IsConcrete && !t.IsGenericDefinition))
                Add(type.TypeId, new Body(type, null, type.Clr));
            foreach (var closed in _model.ClosedTypes)
                Add(closed.TypeId, new Body(closed.Definition,
                    new Env(closed.Value.Arguments.Select(a => new Arg(a, null)).ToList()), closed.Clr));
            return ids;
        }

        /// <summary>
        /// A reference header (§7.9): true for a new object, whose body follows; false for null or a back reference,
        /// which must name an object of <paramref name="type"/>. After a type id (<paramref name="nullable"/> false), null
        /// is invalid: the type id says null.
        /// </summary>
        private bool Reference(InspectNode node, Type type, bool nullable)
        {
            int at = _r.Pos;
            int header = _r.I32();
            var child = node.Add("reference", at);
            child.Length = 4;
            child.Set(header.ToString(CultureInfo.InvariantCulture), header.ToString(CultureInfo.InvariantCulture));
            if (header < -1 || header > _objects.Count || header == 0 && !nullable)
                throw WireReader.Fail(at, $"invalid object reference {header}" +
                                          (header > 0 ? $": {_objects.Count} object(s) read so far" : ""));
            if (header == -1)
                return true;
            if (header == 0)
            {
                child.Note = "null";
                node.SetNull();
                return false;
            }

            var target = _objects[header - 1];
            if (!CodegenModel.AssignableTo(target, type))
                throw WireReader.Fail(at, $"object reference {header} is a {TypeNames.Runtime(target)}, not a " +
                                          TypeNames.Runtime(type));
            child.Note = $"back reference to object #{header}";
            node.Set($"-> #{header}", $"{{\"$ref\":{header}}}");
            return false;
        }

        /// <summary>Numbers a new tracked object (§7.9).</summary>
        private void Track(Body body, InspectNode node)
        {
            _objects.Add(body.Clr);
            node.AddNote($"object #{_objects.Count}");
            node.Children.Last(c => c.Name == "reference").Note = $"new object #{_objects.Count}";
        }

        /// <summary>The body of a <c>[NinoType]</c>, deferred or counted as §7.10 says.</summary>
        private void ReadBody(Body body, InspectNode node)
        {
            var type = body.Type;
            if (type.Raw)
            {
                Value(type.RawValue!, body.Env, node);
                return;
            }

            if (type.IsClassBody)
            {
                bool deferrable = type.Deferrable ?? type.IsConcrete;
                if (deferrable && _depth >= InlineDepth)
                {
                    (_regions == 0 ? _top : _region).Add(new Deferred(body, node, _r.Pos));
                    node.AddNote("body deferred");
                    return;
                }

                if (!deferrable && _depth + _structs >= MaxDepth)
                    throw TooDeep(_r.Pos);
                int depth = _depth;
                _depth = depth + 1;
                Members(body, node);
                _depth = depth;
                if (depth == 0)
                    ReadTop();
                return;
            }

            bool counted = Counted(type);
            if (counted && ++_structs + _depth > MaxDepth)
                throw TooDeep(_r.Pos);
            Members(body, node);
            if (counted)
                _structs--;
        }

        private bool Counted(TypeModel type)
        {
            if (!_counted.TryGetValue(type, out bool counted))
                _counted[type] = counted = type.IsGenericDefinition || _model.HoldsItself(type);
            return counted;
        }

        /// <summary>A body's members in its format (§7.4 to §7.7).</summary>
        private void Members(Body body, InspectNode node)
        {
            var type = body.Type;
            if (type.Format != BodyFormat.Positional)
                node.AddNote(type.Format == BodyFormat.Keyed ? "keyed" : "version tolerant");
            if (type.Elements != null && type.Format != BodyFormat.Keyed)
                CollectionElements(body, node);
            switch (type.Format)
            {
                case BodyFormat.Positional:
                    foreach (var member in type.Members)
                        Member(member, body.Env, node, -1);
                    break;
                case BodyFormat.VersionTolerant:
                    Region(node, end =>
                    {
                        for (int i = 0; i < type.Members.Count; i++)
                        {
                            // Members past the region's end are missing: data of an older version.
                            if (_r.Pos == end)
                            {
                                node.Add(type.Members[i].Name, _r.Pos).Note = "missing (older data): default value";
                                continue;
                            }

                            Member(type.Members[i], body.Env, node, i == type.Members.Count - 1 ? end : -1);
                        }

                        if (_r.Pos < end)
                        {
                            var skipped = node.Add("(unknown members)", _r.Pos);
                            skipped.Length = end - _r.Pos;
                            skipped.Note = $"{end - _r.Pos} byte(s) of a newer version skipped";
                        }
                    });
                    break;
                default:
                    Region(node, end =>
                    {
                        if (type.Elements != null)
                            CollectionElements(body, node);
                        var keys = new HashSet<int>();
                        while (_r.Pos < end)
                            Entry(body, node, keys);
                        foreach (var member in type.Members.Where(m => !m.AllKeys.Any(keys.Contains)))
                            node.Add(member.Name, _r.Pos).Note = $"no entry (key {TypeNames.Hex(member.Key)}): default value";
                    });
                    break;
            }
        }

        private void CollectionElements(Body body, InspectNode node)
        {
            var elements = body.Type.Elements!;
            var child = node.Add("[elements]", _r.Pos);
            child.Encoding = elements.Encoding;
            try
            {
                if (elements.Kind == ValueKind.Map)
                    Map(elements, body.Env, child, true);
                else if (elements.Kind == ValueKind.Sequence)
                    Sequence(elements, body.Env, child, true);
                else
                    Value(elements, body.Env, child);
            }
            finally
            {
                child.Length = _r.Pos - child.Offset;
            }
        }

        private void Member(MemberModel member, Env? env, InspectNode node, int opaqueEnd)
        {
            var child = node.Add(member.Name, _r.Pos);
            child.Type = member.Value.IsOpen && env != null
                ? $"{member.TypeName} ({TypeNames.Runtime(ClrOf(member.Value, env))})"
                : member.TypeName;
            child.Encoding = member.Value.Encoding;
            _opaqueEnd = opaqueEnd;
            Value(member.Value, env, child);
        }

        /// <summary>An entry of a keyed body (§7.6): its key, its length, then the member's value filling it exactly.</summary>
        private void Entry(Body body, InspectNode node, HashSet<int> keys)
        {
            int at = _r.Pos;
            int key = _r.I32();
            int outer = _r.End;
            int end = BeginRegion(at + 4);
            _r.End = end;
            var member = body.Type.Members.FirstOrDefault(m => m.AllKeys.Contains(key));
            InspectNode child;
            if (member == null)
            {
                child = node.Add("(unknown key)", at);
                child.Note = $"key {TypeNames.Hex(key)}: {end - _r.Pos} byte(s) skipped";
                _r.Pos = end;
            }
            else
            {
                child = node.Add(member.Name, at);
                child.Type = member.Value.IsOpen && body.Env != null
                    ? $"{member.TypeName} ({TypeNames.Runtime(ClrOf(member.Value, body.Env))})"
                    : member.TypeName;
                child.Encoding = member.Value.Encoding;
                child.Note = $"key {TypeNames.Hex(key)}" + (key == member.Key ? "" : " (a former name)");
                _opaqueEnd = end;
                Value(member.Value, body.Env, child);
                if (_r.Pos != end)
                    throw Fail($"bytes left over at the end of keyed member {member.Name}: {end - _r.Pos}");
            }

            child.Length = end - at;
            if (!keys.Add(key))
                child.AddNote("key repeated: .NET keeps the last");
            _r.End = outer;
            EndRegion(end, node);
        }

        // ---------------------------------------------------------------- regions and deferred bodies

        /// <summary>A version-tolerant or keyed body: its length, then its members inside it (§7.5, §7.6, §7.10.3).</summary>
        private void Region(InspectNode node, Action<int> members)
        {
            int at = _r.Pos;
            int outer = _r.End;
            int end = BeginRegion(at);
            var length = node.Add("length", at);
            length.Length = 4;
            int raw = BinaryPrimitives.ReadInt32LittleEndian(_r.Data.Slice(at));
            length.Set(raw.ToString(CultureInfo.InvariantCulture), raw.ToString(CultureInfo.InvariantCulture));
            if (_sectionEnd != 0 && _regions == 1)
                length.Note = $"{_sectionEnd - at - 4} bytes: members {end - at - 4}, then a section of deferred bodies";
            _r.End = end;
            members(end);
            _r.End = outer;
            EndRegion(end, node);
        }

        /// <summary>Reads a region's length at <paramref name="at"/>: where its members end.</summary>
        private int BeginRegion(int at)
        {
            _r.Pos = at;
            int length = _r.I32();
            _regions++;
            if (length >= 0 && length <= _r.Remaining)
                return _r.Pos + length;
            int total = length & 0x7FFFFFFF, start = _r.Pos;
            if (length >= 0 || total < 4 || total > _r.Remaining)
                throw WireReader.Fail(at, $"invalid length {length}: " + (length < 0 && length != int.MinValue && total >= 4
                    ? $"a section of {total} bytes, {_r.Remaining} left"
                    : $"{_r.Remaining} byte(s) left"));
            if (_regions != 1)
                throw WireReader.Fail(at, "deferred bodies in a region inside another region");
            var data = _r.Data;
            int end = total - 4, members = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(start + end));
            if (members < 0 || members >= end)
                throw WireReader.Fail(start + end, $"invalid offset of deferred bodies {members}");
            int last = -1;
            for (int p = members; p != end;)
            {
                if (end - p < 8)
                    throw WireReader.Fail(start + p, "invalid section of deferred bodies");
                int key = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(start + p));
                int size = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(start + p + 4));
                if (key <= last || key > p || size < 0 || size > end - p - 8)
                    throw WireReader.Fail(start + p, "invalid section of deferred bodies");
                last = key;
                p += 8 + size;
            }

            _sectionEnd = start + total;
            return start + members;
        }

        /// <summary>Closes a region; at the outermost one, moves past its section and reads the bodies deferred to it.</summary>
        private void EndRegion(int end, InspectNode node)
        {
            if (--_regions != 0)
            {
                _r.Pos = end;
                return;
            }

            int after = _sectionEnd;
            _sectionEnd = 0;
            _r.Pos = after == 0 ? end : after;
            InspectNode? section = null;
            if (after != 0)
            {
                section = node.Add("(deferred bodies)", end);
                section.Length = after - end;
                section.Note = "the section of the outermost region";
            }

            if (_regionHead < _region.Count)
                ReadSection(end, section);
        }

        /// <summary>The bodies deferred inside the outermost region, each from its section entry (§7.10.3).</summary>
        private void ReadSection(int regionEnd, InspectNode? section)
        {
            int after = _r.Pos, sectionEnd = after - 4;
            if (section == null || sectionEnd <= regionEnd)
                throw WireReader.Fail(regionEnd, "missing deferred bodies: the region has no section of them");
            int start = regionEnd - BinaryPrimitives.ReadInt32LittleEndian(_r.Data.Slice(sectionEnd));
            int outer = _r.End, depth = _depth, entryDepth = Math.Min(depth, InlineDepth - 1) + 1;
            int at = regionEnd;
            _regions = 1;
            while (_regionHead < _region.Count)
            {
                var item = _region[_regionHead++];
                while (true)
                {
                    if (sectionEnd - at < 8)
                        throw WireReader.Fail(at, $"missing the deferred body of {item.Node.Name}: no entry keyed " +
                                                  (item.Position - start));
                    int key = BinaryPrimitives.ReadInt32LittleEndian(_r.Data.Slice(at));
                    int length = BinaryPrimitives.ReadInt32LittleEndian(_r.Data.Slice(at + 4));
                    var entry = section.Add("entry", at);
                    entry.Length = 8 + length;
                    at += 8;
                    if (length < 0 || length > sectionEnd - at)
                        throw WireReader.Fail(at - 4, $"invalid length {length}");
                    int body = at;
                    at += length;
                    if (start + key != item.Position)
                    {
                        entry.Note = $"key {key}: a body nothing read refers to, skipped";
                        continue;
                    }

                    entry.Note = $"key {key}: the body of {item.Node.Name} at {item.Node.Offset}";
                    _r.Pos = body;
                    _r.End = body + length;
                    _depth = entryDepth;
                    DeferredBody(item);
                    if (_r.Pos != body + length)
                        throw Fail($"bytes left over at the end of a deferred body: {body + length - _r.Pos}");
                    break;
                }
            }

            _regions = 0;
            _r.End = outer;
            _depth = depth;
            _r.Pos = after;
            _region.Clear();
            _regionHead = 0;
        }

        /// <summary>Every class body is closed: the bodies deferred outside regions, in order, each from depth 1 (§7.10.2).</summary>
        private void ReadTop()
        {
            if (_topActive || _topHead == _top.Count)
                return;
            _topActive = true;
            while (_topHead < _top.Count)
            {
                _depth = 1;
                DeferredBody(_top[_topHead++]);
            }

            _depth = 0;
            _top.Clear();
            _topHead = 0;
            _topActive = false;
        }

        private void DeferredBody(Deferred item)
        {
            var body = item.Node.Add("(body)", _r.Pos);
            body.Note = $"deferred: would have begun at {item.Position}";
            try
            {
                Members(item.Body, body);
            }
            finally
            {
                body.Length = _r.Pos - body.Offset;
            }
        }
    }
}
