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
    }
}
