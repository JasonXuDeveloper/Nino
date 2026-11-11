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
    internal sealed partial class Inspector
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
    }
}
