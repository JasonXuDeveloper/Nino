using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Nino.Codegen.Model;

namespace Nino.Codegen.Inspect
{
    internal sealed partial class Inspector
    {
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
                if (seen != null)
                {
                    string canonical = child.Canonical();
                    if (!seen.Add(canonical))
                        throw WireReader.Fail(child.Offset, $"{unique} {child.Value ?? canonical} repeated");
                }
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
                    throw WireReader.Fail(key.Offset, $"key {key.Value ?? canonical} repeated");
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
                        InspectNode.JsonString(all), all);
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

            int size = KnownSize(v) ?? (opaqueEnd >= 0 ? opaqueEnd - at : (int?)null)
                ?? throw Fail($"cannot read {node.Name}: {Reason(v)}, and its length is not written, so nothing after " +
                           "it can be read");
            var (hex, json) = Scalars.Hex(_r.Take(size));
            node.Set(hex, json);
            node.AddNote("opaque: " + Reason(v));
        }

        private static ValueModel Model(ValueModel like, ValueKind kind) => new(kind, like.Clr, kind.ToString());

        /// <summary>Why a value is not decoded.</summary>
        private static string Reason(ValueModel v) => v.Encoding.StartsWith("formatter(", StringComparison.Ordinal)
            ? $"written by the custom formatter {v.Encoding.Substring(10, v.Encoding.Length - 11)}"
            : (v.Problem ?? v.Encoding + " is not described by the layout").Replace("{lang}", "decodable");
    }
}
