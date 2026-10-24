using System;
using System.Collections.Generic;
using System.Linq;
using Nino.Codegen.Model;

namespace Nino.Codegen.Targets
{
    /// <summary>Options of the generated TypeScript.</summary>
    public sealed class TypeScriptOptions
    {
        /// <summary>The extension of the runtime's import (<c>""</c>, <c>".js"</c>, <c>".ts"</c>).</summary>
        public string ImportExtension { get; set; } = "";

        /// <summary>
        /// The module the generated code imports the runtime from: <c>./nino</c> (the default, with
        /// <see cref="ImportExtension"/>; <c>nino.ts</c> is written next to the code) or a package or path
        /// (<c>nino-serialization</c>), imported as given.
        /// </summary>
        public string? RuntimeModule { get; set; }

        /// <summary>Whether member names are camelCased (the default) or kept as in C#.</summary>
        public bool CamelCase { get; set; } = true;
    }

    /// <summary>
    /// TypeScript: a class per type, an <c>as const</c> object per enum and a <c>NinoCodec</c> per type, which read and
    /// write exactly the bytes the layout describes (<c>Templates/typescript/types.sbn</c>). A type whose layout it
    /// cannot express (generics, reference tracking, <c>[NinoType]</c> collections, encodings without a TypeScript
    /// form) is left out with a warning, and so is every type holding one.
    /// </summary>
    public sealed class TypeScriptTarget
    {
        private readonly CodegenModel _model;
        private readonly TypeScriptOptions _options;
        private readonly HashSet<TypeModel> _supported = new();
        private readonly Dictionary<Type, string> _names = new();

        // Met while shaping the members of the supported types.
        private readonly List<EnumModel> _enums = new();
        private readonly List<(RawStructModel Struct, TsShape Shape)> _rawStructs = new();

        // The functions shared by every value of one shape, in the order they were first used.
        private readonly Dictionary<string, TsHelper> _helperByKey = new(StringComparer.Ordinal);
        private readonly List<TsHelper> _helpers = new();

        public List<string> Warnings { get; } = new();

        public TypeScriptTarget(CodegenModel model, TypeScriptOptions options)
        {
            _model = model;
            _options = options;
        }

        private sealed class UnsupportedException : Exception
        {
            public UnsupportedException(string message) : base(message)
            {
            }
        }

        /// <summary>
        /// The TypeScript runtime (<c>runtimes/typescript/src/nino.ts</c>, embedded), with this tool's version: written
        /// next to the generated code when it does not import the runtime from a package.
        /// </summary>
        public static string Runtime()
        {
            var assembly = typeof(TypeScriptTarget).Assembly;
            using var stream = assembly.GetManifestResourceStream("nino.ts")!;
            using var text = new System.IO.StreamReader(stream, System.Text.Encoding.UTF8);
            string version = assembly
                .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
                .Cast<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()
                ?.InformationalVersion ?? "";
            int plus = version.IndexOf('+', StringComparison.Ordinal);
            return text.ReadToEnd().Replace("__NINO_VERSION__", plus < 0 ? version : version.Substring(0, plus),
                StringComparison.Ordinal);
        }

        /// <summary>The generated file for the types of the model.</summary>
        public string Render(string source)
        {
            ResolveSupported();
            var layouts = _model.Types.Where(_supported.Contains).ToList();
            AssignNames(layouts.Select(t => (object)t).Concat(_enums).Concat(_rawStructs.Select(r => r.Struct)));

            var enums = _enums.OrderBy(e => Name(e), StringComparer.Ordinal)
                .Select(e => new TsEnum(Name(e), e, e.Underlying is PrimitiveKind.I64 or PrimitiveKind.U64)).ToList();
            var rawStructs = _rawStructs.OrderBy(r => Name(r.Struct), StringComparer.Ordinal)
                .Select(r => new TsRawStruct(Name(r.Struct), r.Struct, r.Struct.Fields
                    .Select(f => new TsField(Member(f.Name), f.Offset, Shape(f.Value))).ToList())).ToList();
            var interfaces = layouts.Where(t => t.IsInterface).OrderBy(Name, StringComparer.Ordinal)
                .Select(t => new TsClass(this, t)).ToList();
            var classes = layouts.Where(t => !t.IsInterface && !t.Raw)
                .OrderBy(t => t.InheritanceDepth).ThenBy(Name, StringComparer.Ordinal)
                .Select(t => new TsClass(this, t)).ToList();
            var registered = Registered(layouts).Select(t => new TsClass(this, t)).ToList();
            var codecs = layouts.OrderBy(Name, StringComparer.Ordinal).Select(t => new TsClass(this, t)).ToList();

            string module = _options.RuntimeModule ?? "./nino" + _options.ImportExtension;
            var renderer = new TemplateRenderer("typescript");
            renderer.Set("source", source);
            renderer.Set("runtime_module", module);
            renderer.Set("enums", enums);
            renderer.Set("raw_structs", rawStructs);
            renderer.Set("interfaces", interfaces);
            renderer.Set("classes", classes);
            renderer.Set("registered", registered);
            renderer.Set("codecs", codecs);
            renderer.Function("name", new Func<object, string>(Name));
            renderer.Function("helper", new Func<TsShape, string, string>(Helper));
            renderer.Function("helper_count", new Func<int>(() => _helpers.Count));
            renderer.Function("helper_at", new Func<int, TsHelper>(i => _helpers[i]));
            return renderer.Render("types.sbn").TrimEnd('\n') + "\n";
        }

        // ---------------------------------------------------------------- which types

        private void ResolveSupported()
        {
            foreach (var type in _model.Types)
            {
                string? reason = type.IsGeneric ? "generic types are not supported"
                    : type.TrackReferences ? "reference tracking (trackReferences) is not supported"
                    : type.Elements != null ? "[NinoType] collections are not supported"
                    : null;
                if (reason == null)
                    _supported.Add(type);
                else
                    Warn(type, reason);
            }

            // Leave out the types holding a type that is left out, until every type left can be written.
            while (true)
            {
                _enums.Clear();
                _rawStructs.Clear();
                var failing = _model.Types.Where(_supported.Contains)
                    .Select(t => (Type: t, Reason: Check(t))).FirstOrDefault(f => f.Reason != null);
                if (failing.Type == null)
                    return;
                _supported.Remove(failing.Type);
                Warn(failing.Type, failing.Reason!);
            }
        }

        /// <summary>Why a type cannot be written, or null.</summary>
        private string? Check(TypeModel type)
        {
            try
            {
                if (type.Raw)
                    Shape(type.RawValue!);
                else
                    foreach (var member in type.Members)
                        Shape(member.Value);
                return null;
            }
            catch (UnsupportedException e)
            {
                return e.Message;
            }
        }

        private void Warn(TypeModel type, string reason) =>
            Warnings.Add($"{type.FullName} is not written to TypeScript: {reason}.");

        /// <summary>The concrete types of <paramref name="types"/> written behind a type id.</summary>
        private IEnumerable<TypeModel> Registered(IEnumerable<TypeModel> types) =>
            types.Where(t => t.TypeId != 0 && t.IsConcrete).OrderBy(Name, StringComparer.Ordinal);

        // ---------------------------------------------------------------- shapes

        /// <summary>The TypeScript shape of a value: what the template writes for it.</summary>
        internal TsShape Shape(ValueModel value)
        {
            switch (value.Kind)
            {
                case ValueKind.Primitive:
                case ValueKind.Enum:
                case ValueKind.RawStruct:
                case ValueKind.RawNullable:
                case ValueKind.RawTuple:
                    return RawShape(value);
                case ValueKind.String:
                    return new TsShape("string");
                case ValueKind.Utf8:
                    return new TsShape("utf8");
                case ValueKind.Object:
                    return new TsShape("object") { Opens = true, Reaches = BodyStructs(_ => true) };
                case ValueKind.Type:
                    return TypeShape(value.Type!);
                case ValueKind.Named:
                    throw new UnsupportedException($"{value.ClrName} is not written to TypeScript");
                case ValueKind.Nullable:
                    return Nullable(Shape(value.Element!));
                case ValueKind.Sequence:
                {
                    var element = Shape(value.Element!);
                    if (value.IsByteArray)
                        return new TsShape("bytes");
                    return Holding(new TsShape("seq")
                    {
                        Element = element,
                        Set = value.Collection is CollectionKind.HashSet or CollectionKind.SortedSet ||
                              CodegenModel.Implements(value.Clr, "System.Collections.Generic.ISet`1") != null,
                    }, element);
                }
                case ValueKind.Map:
                {
                    var (key, mapValue) = (Shape(value.Key!), Shape(value.Value!));
                    return Holding(new TsShape("map") { Key = key, Value = mapValue }, key, mapValue);
                }
                case ValueKind.Tuple:
                {
                    // The items one after another: at fixed offsets when they all have a fixed size.
                    var items = value.Items.Select(Shape).ToList();
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
                case ValueKind.Unsupported:
                    throw new UnsupportedException(Problem(value));
                default:
                    // grid, bigint, version, datetimeoffset.
                    throw new UnsupportedException($"{value.ClrName} ({KindName(value)}) has no TypeScript form");
            }
        }

        private static string KindName(ValueModel value)
        {
            int open = value.Encoding.IndexOf('(');
            return open < 0 ? value.Encoding : value.Encoding.Substring(0, open);
        }

        private static string Problem(ValueModel value) => value.Problem!.Replace("{lang}", "TypeScript");

        /// <summary>A presence byte, then the value if present (<c>T?</c>, <c>Tuple</c>, a sealed class).</summary>
        private static TsShape Nullable(TsShape inner) =>
            new("nullable") { Inner = inner, Opens = inner.Opens, Reaches = inner.Reaches };

        /// <summary>What a value made of <paramref name="parts"/> holds.</summary>
        private static TsShape Holding(TsShape shape, params TsShape[] parts)
        {
            shape.Opens = parts.Any(p => p.Opens);
            shape.Reaches = parts.SelectMany(p => p.Reaches).Distinct().ToList();
            return shape;
        }

        /// <summary>
        /// A tuple or pair as a TypeScript tuple: its items at <paramref name="offsets"/> when they are all of fixed
        /// size (<paramref name="size"/> bytes in all), else one after another.
        /// </summary>
        private static TsShape Tuple(List<TsShape> items, List<int> offsets, int size, bool zeroed) =>
            new(size >= 0 ? "fixedtuple" : "tuple")
            {
                Items = items, Offsets = offsets, Size = size, Zeroed = size >= 0 && zeroed,
            };

        /// <summary>A <c>[NinoType]</c> member, with the header of its kind.</summary>
        internal TsShape TypeShape(TypeModel type)
        {
            if (!_supported.Contains(type))
                throw new UnsupportedException($"{type.FullName} is not written to TypeScript");
            if (type.Raw)
                return Shape(type.RawValue!);
            bool body = type.IsClassBody;
            var reaches = body ? new List<TypeModel>() : new List<TypeModel> { type };
            return type.Header switch
            {
                HeaderKind.None => new TsShape("type") { Type = type, Header = "none", Opens = body, Reaches = reaches },
                HeaderKind.NullFlag => Nullable(new TsShape("type")
                {
                    Type = type, Header = "null", Opens = body, Reaches = reaches,
                }),
                // Any type assignable to it, by the type id in front of its body.
                _ => new TsShape("type")
                {
                    Type = type, Header = "typeid", Opens = true, Reaches = BodyStructs(type.IsAssignableFrom),
                },
            };
        }

        /// <summary>A value copied as raw memory: a primitive, an enum, or a struct in its canonical layout.</summary>
        private TsShape RawShape(ValueModel value)
        {
            switch (value.Kind)
            {
                case ValueKind.Enum:
                {
                    var model = value.Enum!;
                    if (!_enums.Contains(model))
                        _enums.Add(model);
                    var underlying = Primitive(model.Underlying!.Value) ?? throw new UnsupportedException(
                        $"{model.FullName}'s underlying type has no TypeScript form");
                    return new TsShape("enum")
                    {
                        Enum = model,
                        Inner = underlying,
                        Size = underlying.Size,
                        DefaultCast = !model.HasZero && !model.Flags,
                    };
                }
                case ValueKind.Primitive:
                    if (Primitive(value.Primitive) is { } primitive)
                        return primitive;
                    if (value.Fallback != null)
                        return RawShape(value.Fallback);
                    throw new UnsupportedException($"{value.ClrName} ({value.Encoding}) has no TypeScript form");
                case ValueKind.RawNullable:
                {
                    // T? in the canonical Nullable<T> layout: the flag, then the value at its offset (zeroed when absent).
                    var inner = RawShape(value.Element!);
                    return new TsShape("rawnullable")
                    {
                        Inner = inner, FlagOffset = value.FlagOffset, ValueOffset = value.ValueOffset,
                        Size = value.Size, Zeroed = true,
                    };
                }
                case ValueKind.RawTuple:
                {
                    // Raw tuples and pairs are TypeScript tuples too: each item at its offset.
                    var items = value.Items.Select(RawShape).ToList();
                    return Tuple(items, value.Offsets.ToList(), value.Size, !value.Packed || items.Any(i => i.Zeroed));
                }
                case ValueKind.RawStruct:
                {
                    var model = value.Struct!;
                    if (model.Empty)
                        throw new UnsupportedException($"{value.ClrName} ({value.Encoding}) has no TypeScript form");
                    int index = _rawStructs.FindIndex(r => r.Struct == model);
                    if (index >= 0)
                        return _rawStructs[index].Shape;
                    var shape = new TsShape("rawstruct") { Struct = model, Size = model.Size };
                    _rawStructs.Add((model, shape));
                    // Also adds the enums and structs of its fields. Padding is zeroed (as Nino zeroes it), and so is
                    // anything its fields skip.
                    var fields = model.Fields.Select(f => RawShape(f.Value)).ToList();
                    shape.Zeroed = !model.Packed || fields.Any(f => f.Zeroed);
                    return shape;
                }
                case ValueKind.Unsupported:
                    throw new UnsupportedException(Problem(value));
                default:
                    throw new UnsupportedException($"{value.ClrName} ({value.Encoding}) has no TypeScript form");
            }
        }

        /// <summary>The primitives TypeScript reads and writes alone, and at an offset of a view.</summary>
        private static TsShape? Primitive(PrimitiveKind kind)
        {
            string? name = kind switch
            {
                PrimitiveKind.Bool => "bool",
                PrimitiveKind.U8 => "u8",
                PrimitiveKind.I8 => "i8",
                PrimitiveKind.I16 => "i16",
                PrimitiveKind.U16 => "u16",
                PrimitiveKind.I32 => "i32",
                PrimitiveKind.U32 => "u32",
                PrimitiveKind.I64 or PrimitiveKind.NInt => "i64",
                PrimitiveKind.U64 or PrimitiveKind.NUInt => "u64",
                PrimitiveKind.F32 => "f32",
                PrimitiveKind.F64 => "f64",
                PrimitiveKind.Char => "char",
                PrimitiveKind.Decimal => "decimal",
                PrimitiveKind.Guid => "guid",
                PrimitiveKind.DateTime => "dateTime",
                PrimitiveKind.TimeSpan => "timeSpan",
                _ => null,
            };
            return name == null ? null : new TsShape("prim") { Primitive = name, Size = CodegenModel.PrimitiveSize(kind) };
        }

        // ---------------------------------------------------------------- nesting

        /// <summary>
        /// The non-raw structs written behind a type id (for <c>object</c> or an interface) that
        /// <paramref name="assignable"/> accepts.
        /// </summary>
        private List<TypeModel> BodyStructs(Func<TypeModel, bool> assignable) =>
            _model.Types.Where(t => t.IsValueType && !t.Raw && t.TypeId != 0 && assignable(t)).ToList();

        private readonly Dictionary<TypeModel, bool> _structOpens = new();

        /// <summary>Whether a value of <paramref name="shape"/> may hold a class body, directly or in its structs.</summary>
        private bool Opens(TsShape shape) => shape.Opens || shape.Reaches.Any(StructOpens);

        private bool StructOpens(TypeModel type)
        {
            if (_structOpens.TryGetValue(type, out bool opens))
                return opens;
            // A struct that holds itself opens what its other members open.
            _structOpens[type] = false;
            opens = _supported.Contains(type) && type.Members.Any(m => Opens(Shape(m.Value)));
            _structOpens[type] = opens;
            return opens;
        }

        /// <summary>
        /// Whether a class body may hold other class bodies (<c>NinoBody.Nests</c>): it counts the depth for them, and
        /// at depth 0, ends with the bodies deferred below it. Saying so of one that does not changes no byte.
        /// </summary>
        internal bool Nests(TypeModel type) => type.Members.Any(m => Opens(Shape(m.Value)));

        /// <summary>
        /// Whether a struct can hold itself without a class body between (through collections, nullables,
        /// <c>object</c> or interfaces): its nesting is counted (<c>Reader.EnterStruct</c> in .NET), so deep data throws
        /// instead of overflowing the stack.
        /// </summary>
        internal bool HoldsItself(TypeModel type)
        {
            var seen = new HashSet<TypeModel>();
            var pending = new Stack<TypeModel>();
            pending.Push(type);
            while (pending.Count != 0)
            {
                var next = pending.Pop();
                if (!_supported.Contains(next))
                    continue;
                foreach (var reached in next.Members.SelectMany(m => Shape(m.Value).Reaches))
                {
                    if (reached == type)
                        return true;
                    if (seen.Add(reached))
                        pending.Push(reached);
                }
            }

            return false;
        }

        private readonly HashSet<TypeModel> _warnedDeferrable = new();

        /// <summary>
        /// Whether .NET defers the body of the class <paramref name="type"/> past 128 open bodies (else it reads and
        /// writes it in place, up to 512): <c>Deferrable</c> in its published layout. A class built by its
        /// constructor, or compared by value, is not deferred. A layout from a generator older than the property
        /// leaves it out: such a class is taken to be deferred, as most are.
        /// </summary>
        internal bool Deferrable(TypeModel type)
        {
            if (type.Deferrable is { } deferrable)
                return deferrable;
            if (_warnedDeferrable.Add(type))
                Warnings.Add($"{type.FullName}: its layout does not say whether it is deferrable (built with an " +
                             "older Nino); its bodies are taken to be deferred past 128 nested bodies, as most " +
                             "classes are.");
            return true;
        }

        /// <summary>The supported layout of the nearest base class, whose TypeScript class this one extends.</summary>
        internal TypeModel? BaseLayout(TypeModel type)
        {
            for (var t = type.BaseType; t != null; t = t.BaseType)
            {
                if (_supported.Contains(t))
                    return t;
            }

            return null;
        }

        internal bool InHierarchy(TypeModel type) =>
            BaseLayout(type) != null ||
            _model.Types.Any(t => !t.IsInterface && _supported.Contains(t) && BaseLayout(t) == type);

        internal IEnumerable<TypeModel> SupportedInterfaces(TypeModel type) =>
            type.Interfaces.Where(_supported.Contains);

        /// <summary>The concrete types written behind the type id of <paramref name="type"/>.</summary>
        internal List<TypeModel> RegisteredAssignable(TypeModel type) =>
            Registered(_supported.Where(type.IsAssignableFrom)).ToList();

        // ---------------------------------------------------------------- names

        /// <summary>
        /// Types are named as in C# (nested and generic types joined with _), or with their namespace when two share
        /// a name.
        /// </summary>
        private void AssignNames(IEnumerable<object> types)
        {
            var all = types.Select(Clr).Distinct().ToList();
            var simple = all.ToLookup(TypeNames.Simple);
            foreach (var type in all)
            {
                string name = TypeNames.Simple(type);
                if (simple[name].Count() > 1)
                    name = (type.Namespace ?? "").Replace('.', '_') + "_" + name;
                _names[type] = name;
            }
        }

        private static Type Clr(object type) => type switch
        {
            TypeModel t => t.Clr,
            EnumModel e => e.Clr,
            RawStructModel s => s.Clr,
            _ => throw new ArgumentException(type.ToString()),
        };

        internal string Name(object type) =>
            _names.TryGetValue(Clr(type), out var name) ? name : TypeNames.Simple(Clr(type));

        internal string Member(string name) => _options.CamelCase ? TypeNames.Camel(name) : name;

        /// <summary>The members of a type, with their TypeScript names and shapes.</summary>
        internal List<TsMember> Members(TypeModel type)
        {
            var used = new Dictionary<string, int>(StringComparer.Ordinal);
            var list = new List<TsMember>();
            foreach (var member in type.Members)
            {
                string name = Member(member.Name);
                // A member hidden with `new` and the one hiding it: the hidden one keeps the name.
                used[name] = used.TryGetValue(name, out int count) ? count + 1 : 0;
                if (used[name] != 0)
                    name += "_" + used[name].ToString(System.Globalization.CultureInfo.InvariantCulture);
                list.Add(new TsMember(member, name, Shape(member.Value)));
            }

            return list;
        }

        // ---------------------------------------------------------------- helpers

        /// <summary>
        /// The name of the function that reads (<paramref name="mode"/> <c>read</c>) or writes a value of
        /// <paramref name="shape"/>, shared by every value of the same shape. A new one is numbered after the
        /// functions its own code calls, and the template writes them all in that order after the types.
        /// </summary>
        private string Helper(TsShape shape, string mode)
        {
            string key = mode + ":" + shape.Identity;
            if (_helperByKey.TryGetValue(key, out var helper))
                return helper.Name;
            // The functions the value's code calls come first.
            switch (shape.Kind)
            {
                case "seq" when !shape.Element!.Fixed:
                    Touch(shape.Element, mode);
                    break;
                case "map" when !(shape.Key!.Fixed && shape.Value!.Fixed):
                    Touch(shape.Key, mode);
                    Touch(shape.Value!, mode);
                    break;
            }

            helper = new TsHelper((mode == "read" ? "read$" : "write$") + _helpers.Count, mode, shape);
            _helperByKey[key] = helper;
            _helpers.Add(helper);
            return helper.Name;
        }

        /// <summary>Registers the functions the code reading or writing <paramref name="shape"/> alone calls.</summary>
        private void Touch(TsShape shape, string mode)
        {
            switch (shape.Kind)
            {
                case "seq":
                case "map":
                case "rawnullable":
                case "fixedtuple":
                    Helper(shape, mode);
                    break;
                case "nullable":
                    Touch(shape.Inner!, mode);
                    break;
                case "tuple":
                    foreach (var item in shape.Items)
                        Touch(item, mode);
                    break;
            }
        }
    }

    /// <summary>
    /// How a value is written in TypeScript (<see cref="Kind"/>: <c>prim</c>, <c>enum</c>, <c>rawstruct</c>,
    /// <c>rawnullable</c>, <c>fixedtuple</c>, <c>tuple</c>, <c>string</c>, <c>utf8</c>, <c>object</c>,
    /// <c>nullable</c>, <c>bytes</c>, <c>seq</c>, <c>map</c>, <c>type</c>): the template turns it into a type, a default,
    /// and code reading and writing it, alone or at an offset.
    /// </summary>
    public sealed class TsShape
    {
        internal TsShape(string kind) => Kind = kind;

        public string Kind { get; }

        /// <summary>The size of a value of fixed size, else -1: such a value is also read and written at an offset.</summary>
        public int Size { get; internal set; } = -1;

        public bool Fixed => Size >= 0;

        /// <summary>Whether writing it at an offset needs zeroed bytes: it skips padding, or absent values.</summary>
        public bool Zeroed { get; internal set; }

        /// <summary>The reader's and writer's method of a primitive (<c>i32</c>, <c>dateTime</c>...).</summary>
        public string? Primitive { get; internal set; }

        public EnumModel? Enum { get; internal set; }

        /// <summary>An enum's whose default needs a cast through its underlying type (no constant is 0).</summary>
        public bool DefaultCast { get; internal set; }

        public RawStructModel? Struct { get; internal set; }

        public TypeModel? Type { get; internal set; }

        /// <summary>A type's header: <c>none</c>, <c>null</c> (presence byte) or <c>typeid</c>.</summary>
        public string? Header { get; internal set; }

        /// <summary>The value of a nullable or raw nullable, the underlying integer of an enum.</summary>
        public TsShape? Inner { get; internal set; }

        public TsShape? Element { get; internal set; }

        /// <summary>A set (<c>Set</c>) rather than an array.</summary>
        public bool Set { get; internal set; }

        public TsShape? Key { get; internal set; }

        public TsShape? Value { get; internal set; }

        public List<TsShape> Items { get; internal set; } = new();

        public List<int> Offsets { get; internal set; } = new();

        public int FlagOffset { get; internal set; }

        public int ValueOffset { get; internal set; }

        /// <summary>Whether the value may hold a class body (a class, <c>object</c> or an interface) itself.</summary>
        internal bool Opens { get; set; }

        /// <summary>The non-raw structs the value holds directly: their bodies may open more.</summary>
        internal List<TypeModel> Reaches { get; set; } = new();

        /// <summary>Identifies the code of the shape: two shapes with the same key are read and written alike.</summary>
        internal string Identity => _key ??= Kind + "(" + string.Join(",", Parts()) + ")";

        private string? _key;

        private IEnumerable<string> Parts()
        {
            yield return Size.ToString(System.Globalization.CultureInfo.InvariantCulture);
            yield return Zeroed ? "z" : "";
            yield return Primitive ?? "";
            yield return Enum?.FullName ?? "";
            yield return Struct?.FullName ?? "";
            yield return Type?.FullName ?? "";
            yield return Header ?? "";
            yield return Set ? "set" : "";
            yield return FlagOffset + "/" + ValueOffset;
            yield return string.Join("/", Offsets);
            foreach (var child in new[] { Inner, Element, Key, Value }.Concat(Items))
                yield return child?.Identity ?? "-";
        }
    }

    /// <summary>Fixed-size members next to one another, which take their bytes at once, or one other member.</summary>
    public sealed class TsRun
    {
        public List<TsRunItem> Items { get; } = new();

        /// <summary>The bytes of the run's members (of a run of fixed-size members).</summary>
        public int Size { get; internal set; }

        public bool Zeroed { get; internal set; }
    }

    public sealed class TsRunItem
    {
        internal TsRunItem(TsMember member, int offset)
        {
            Member = member;
            Offset = offset;
        }

        public TsMember Member { get; }

        /// <summary>Its offset in the run.</summary>
        public int Offset { get; }
    }

    /// <summary>A function that reads or writes the values of one shape.</summary>
    public sealed class TsHelper
    {
        internal TsHelper(string name, string mode, TsShape shape)
        {
            Name = name;
            Mode = mode;
            Shape = shape;
        }

        public string Name { get; }

        /// <summary><c>read</c> or <c>write</c>.</summary>
        public string Mode { get; }

        public TsShape Shape { get; }
    }

    public sealed class TsMember
    {
        internal TsMember(MemberModel member, string name, TsShape shape)
        {
            Model = member;
            Name = name;
            Shape = shape;
        }

        public MemberModel Model { get; }

        public string Name { get; }

        public TsShape Shape { get; }
    }

    public sealed class TsField
    {
        internal TsField(string name, int offset, TsShape shape)
        {
            Name = name;
            Offset = offset;
            Shape = shape;
        }

        public string Name { get; }

        public int Offset { get; }

        public TsShape Shape { get; }
    }

    public sealed class TsEnum
    {
        internal TsEnum(string name, EnumModel model, bool big)
        {
            Name = name;
            Model = model;
            Big = big;
        }

        public string Name { get; }

        public EnumModel Model { get; }

        /// <summary>A 64-bit enum: its constants are bigints.</summary>
        public bool Big { get; }
    }

    public sealed class TsRawStruct
    {
        internal TsRawStruct(string name, RawStructModel model, List<TsField> fields)
        {
            Name = name;
            Model = model;
            Fields = fields;
        }

        public string Name { get; }

        public RawStructModel Model { get; }

        public List<TsField> Fields { get; }
    }

    /// <summary>A type as the template writes it: its name, members, and how its body is read and written.</summary>
    public sealed class TsClass
    {
        private readonly TypeScriptTarget _target;

        internal TsClass(TypeScriptTarget target, TypeModel type)
        {
            _target = target;
            Model = type;
            Name = target.Name(type);
        }

        public TypeModel Model { get; }

        public string Name { get; }

        public List<TsMember> Members => _members ??= _target.Members(Model);

        private List<TsMember>? _members;

        /// <summary>The members declared on this class rather than on its base class (declared there).</summary>
        public List<TsMember> Own
        {
            get
            {
                var baseLayout = _target.BaseLayout(Model);
                return Members.Where(m => baseLayout == null || m.Model.DeclaringClr == null ||
                                          !m.Model.DeclaringClr.IsAssignableFrom(baseLayout.Clr)).ToList();
            }
        }

        public string? BaseName => _target.BaseLayout(Model) is { } b ? _target.Name(b) : null;

        public List<string> InterfaceNames => _target.SupportedInterfaces(Model).Select(i => _target.Name(i)).ToList();

        /// <summary>
        /// In a class hierarchy, V8 creates objects about twice as fast from constructors as from field initializers
        /// (one initializer per class), so the members are declared and set in the constructor there.
        /// </summary>
        public bool Hierarchy => _target.InHierarchy(Model);

        /// <summary>Whether the class body may hold other class bodies: it counts the depth for them.</summary>
        public bool Nests => _target.Nests(Model);

        /// <summary>A struct that can hold itself: its nesting is counted.</summary>
        public bool Counted => !Model.IsClassBody && _target.HoldsItself(Model);

        /// <summary><c>defer</c> (deferred past 128 bodies) or <c>deep</c> (in place up to 512).</summary>
        public string Later => _target.Deferrable(Model) ? "defer" : "deep";

        /// <summary>The members in runs: fixed-size members next to one another together, the others alone.</summary>
        public List<TsRun> Runs
        {
            get
            {
                var runs = new List<TsRun>();
                foreach (var member in Members)
                {
                    if (!(member.Shape.Fixed && runs.Count != 0 && runs[^1].Items[^1].Member.Shape.Fixed))
                        runs.Add(new TsRun());
                    var run = runs[^1];
                    run.Items.Add(new TsRunItem(member, run.Size));
                    if (member.Shape.Fixed)
                        run.Size += member.Shape.Size;
                    run.Zeroed |= member.Shape.Zeroed;
                }

                return runs;
            }
        }

        /// <summary>How the runs declare <c>at</c>: <c>let</c> when more than one run of several members reuses it.</summary>
        public string AtDeclaration => Runs.Count(r => r.Items.Count > 1) > 1 ? "let at" : "const at";

        public bool IsClassBody => Model.IsClassBody;

        /// <summary><c>positional</c>, <c>tolerant</c> or <c>keyed</c>.</summary>
        public string Format => Model.Format switch
        {
            BodyFormat.Keyed => "keyed",
            BodyFormat.VersionTolerant => "tolerant",
            _ => "positional",
        };

        /// <summary><c>none</c>, <c>null</c> (a presence byte) or <c>typeid</c>.</summary>
        public string Header => Model.Header switch
        {
            HeaderKind.NullFlag => "null",
            HeaderKind.TypeId => "typeid",
            _ => "none",
        };

        /// <summary>The type with its header, as a member of its type is written.</summary>
        public TsShape HeaderShape => _target.TypeShape(Model);

        /// <summary>The shape of a raw type, or of a type read with its header (a member of its type).</summary>
        public TsShape? RawShape => Model.Raw ? _target.Shape(Model.RawValue!) : null;

        /// <summary>The concrete types written behind this type's id.</summary>
        public List<TsClass> Assignable =>
            _target.RegisteredAssignable(Model).Select(t => new TsClass(_target, t)).ToList();
    }
}
