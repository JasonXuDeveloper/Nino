using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Nino.Codegen.Model;

namespace Nino.Codegen.Targets
{
    /// <summary>Options of the generated Rust.</summary>
    public sealed class RustOptions
    {
        /// <summary>Derive serde's traits behind the crate's <c>serde</c> feature.</summary>
        public bool Serde { get; set; }

        /// <summary>The names serde gives the members.</summary>
        public TagNaming SerdeNaming { get; set; } = TagNaming.Camel;

        /// <summary>
        /// Strings as <c>nino::WideString</c> (UTF-16, keeps lone surrogates, as .NET does) instead of <c>String</c>
        /// (which fails to read a string holding one).
        /// </summary>
        public bool WideStrings { get; set; }
    }

    /// <summary>
    /// Rust: a struct per type and its codecs written out (<c>Templates/rust/types.sbn</c>), in the shape of
    /// <c>runtimes/rust/tests/conformance/types.rs</c>: <c>NinoReadBody</c>/<c>NinoWriteBody</c> for bodies, the
    /// runtime's own impls for collections, strings and the BCL values, a raw struct as one fixed-size array, and an
    /// enum per type written behind a type id holding only the types it allows (<c>XRef</c>). A type it cannot express
    /// is left out with a warning, and so is every type holding one.
    /// </summary>
    public sealed class RustTarget
    {
        private readonly CodegenModel _model;
        private readonly RustOptions _options;
        private readonly HashSet<TypeModel> _supported = new();
        private readonly Dictionary<Type, string> _names = new();
        private readonly List<EnumModel> _enums = new();
        private readonly List<RawStructModel> _rawStructs = new();
        internal Dictionary<RawStructModel, RustShape> RawShapes { get; } = new();
        private readonly List<RustNamed> _namedCollections = new();
        private bool _usesObject;

        public List<string> Warnings { get; } = new();

        public RustTarget(CodegenModel model, RustOptions options)
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

        public string Render(string source)
        {
            ResolveSupported();
            var types = _model.Types.Where(_supported.Contains).ToList();
            // A raw type no member holds (one only a polymorphic slot can hold) is declared too.
            foreach (var raw in types.Where(t => t.Raw))
                Raw(raw.RawValue!);
            AssignNames(types.Cast<object>().Concat(_enums).Concat(_rawStructs));

            var all = types.Select(t => new RustType(this, t)).ToList();
            foreach (var t in all)
                _ = t.Members;
            var rawStructs = _rawStructs.Select(s => new RustRawStruct(this, s)).ToList();

            var refs = types.Where(t => t.Header == HeaderKind.TypeId && !t.Raw)
                .Select(t => new RustRef(Name(t) + "Ref", t.FullName, Allowed(t))).ToList();
            if (_usesObject)
                refs.Add(new RustRef("AnyObject", "object", ObjectTypes().ToList()));

            var renderer = new TemplateRenderer("rust");
            renderer.Set("source", source);
            renderer.Set("serde", _options.Serde);
            renderer.Set("serde_rename_all", SerdeRule);
            renderer.Set("enums", _enums.OrderBy(e => Name(e), StringComparer.Ordinal)
                .Select(e => new RustEnum(Name(e), e)).ToList());
            renderer.Set("raw_structs", rawStructs.OrderBy(s => s.Name, StringComparer.Ordinal).ToList());
            renderer.Set("types", all.Where(t => !t.Model.Raw && t.Model.IsConcrete)
                .OrderBy(t => t.Name, StringComparer.Ordinal).ToList());
            renderer.Set("refs", refs.OrderBy(r => r.Name, StringComparer.Ordinal).ToList());
            renderer.Set("named", _namedCollections.OrderBy(n => n.Name, StringComparer.Ordinal).ToList());
            renderer.Set("tops", all.OrderBy(t => t.RuntimeName, StringComparer.Ordinal).ToList());
            renderer.Set("uses_any", refs.Count != 0);
            renderer.Function("hex", new Func<int, string>(TypeNames.Hex));
            renderer.Function("name", new Func<object, string>(Name));
            renderer.Function("type_of", new Func<TypeModel, RustType>(t => new RustType(this, t)));
            renderer.Function("rust_string", new Func<string, string>(RustString));
            return renderer.Render("types.sbn").TrimEnd('\n') + "\n";
        }

        // ---------------------------------------------------------------- which types

        private void ResolveSupported()
        {
            foreach (var type in _model.Types)
            {
                string? reason = type.IsGenericDefinition ? "open generic types are not supported"
                    : type.TrackReferences ? "reference tracking (trackReferences) is not supported"
                    : type.Elements != null ? "[NinoType] collections are not supported"
                    : null;
                if (reason == null)
                    _supported.Add(type);
                else
                    Warn(type, reason);
            }

            while (true)
            {
                Reset();
                var failing = _model.Types.Where(_supported.Contains)
                    .Select(t => (Type: t, Reason: Check(t))).FirstOrDefault(f => f.Reason != null);
                if (failing.Type == null)
                    break;
                _supported.Remove(failing.Type);
                Warn(failing.Type, failing.Reason!);
            }

            Reset();
        }

        private void Reset()
        {
            _enums.Clear();
            _rawStructs.Clear();
            RawShapes.Clear();
            _namedCollections.Clear();
            _usesObject = false;
        }

        private string? Check(TypeModel type)
        {
            try
            {
                if (type.Raw)
                    Raw(type.RawValue!);
                else
                    foreach (var member in type.Members)
                        Shape(member.Value, false);
                return null;
            }
            catch (UnsupportedException e)
            {
                return e.Message;
            }
        }

        private void Warn(TypeModel type, string reason) =>
            Warnings.Add($"{type.FullName} is not written to Rust: {reason}.");

        /// <summary>The concrete types an <c>object</c> slot allows: every one written behind a type id.</summary>
        private IEnumerable<TypeModel> ObjectTypes() =>
            _model.Types.Where(t => _supported.Contains(t) && t.IsConcrete && !t.IsGenericDefinition)
                .OrderBy(Name, StringComparer.Ordinal);

        /// <summary>The concrete types a slot declared as <paramref name="type"/> allows.</summary>
        private List<TypeModel> Allowed(TypeModel type) =>
            ObjectTypes().Where(type.IsAssignableFrom).ToList();

        /// <summary>
        /// The non-raw structs a value holds without a class body between: itself, or (behind a type id) any struct
        /// the slot allows.
        /// </summary>
        private IEnumerable<TypeModel> StructsIn(ValueModel value)
        {
            var candidates = value.Kind switch
            {
                ValueKind.Type when value.Type!.Header == HeaderKind.TypeId => Allowed(value.Type),
                ValueKind.Type => new List<TypeModel> { value.Type! },
                ValueKind.Object => ObjectTypes().ToList(),
                _ => new List<TypeModel>(),
            };
            return candidates.Where(t => t.IsValueType && !t.Raw && _supported.Contains(t));
        }

        /// <summary>
        /// Whether a struct can hold itself through no class body: its nesting counts toward the depth limit
        /// (<c>enter_struct</c>), as nothing else bounds it.
        /// </summary>
        internal bool HoldsItself(TypeModel type)
        {
            var seen = new HashSet<TypeModel>();
            var pending = new Stack<TypeModel>();
            pending.Push(type);
            while (pending.Count != 0)
            {
                var next = pending.Pop();
                foreach (var reached in next.Members.SelectMany(m => m.Value.Walk()).SelectMany(StructsIn))
                {
                    if (reached == type)
                        return true;
                    if (seen.Add(reached))
                        pending.Push(reached);
                }
            }

            return false;
        }

        // ---------------------------------------------------------------- shapes

        /// <summary>The Rust shape of a value. <paramref name="key"/>: a map key or set element, never null.</summary>
        internal RustShape Shape(ValueModel value, bool key)
        {
            switch (value.Kind)
            {
                case ValueKind.Primitive:
                case ValueKind.Enum:
                case ValueKind.RawStruct:
                    return Raw(value);
                case ValueKind.RawTuple:
                {
                    // Items one after another with no gap are the same bytes as a tuple of them.
                    var items = value.Items.Select(Raw).ToList();
                    int at = 0;
                    for (int i = 0; i < items.Count; i++)
                    {
                        if (value.Offsets[i] != at)
                            throw new UnsupportedException(
                                $"{value.ClrName} ({value.Encoding}) has padding, which a Rust tuple cannot hold");
                        at += items[i].Size;
                    }

                    return new RustShape("tuple") { Items = items, Hashable = items.All(i => i.Hashable) };
                }
                case ValueKind.String:
                    return new RustShape(key ? "keystring" : "string")
                        { Wide = _options.WideStrings, Hashable = true };
                case ValueKind.Utf8:
                    return new RustShape(key ? "keyutf8" : "utf8") { Hashable = true };
                case ValueKind.Object:
                    _usesObject = true;
                    return new RustShape("object") { MayDefer = true };
                case ValueKind.Type:
                    return TypeShape(value.Type!);
                case ValueKind.Named:
                {
                    if (!_model.NamedCollections.TryGetValue(value.NamedTypeName ?? "", out var collection))
                        throw new UnsupportedException($"{value.ClrName} is not written to Rust");
                    var named = _namedCollections.FirstOrDefault(n => n.FullName == value.NamedTypeName);
                    if (named == null)
                    {
                        named = new RustNamed(GoTarget.Go(TypeNames.WithoutArity(
                            value.NamedTypeName!.Split('.', '+').Last())), value.NamedTypeName!);
                        _namedCollections.Add(named);
                        named.Value = Shape(collection, false);
                    }

                    return new RustShape("named") { Named = named, MayDefer = true };
                }
                case ValueKind.Nullable:
                {
                    var inner = Shape(value.Element!, false);
                    return new RustShape("nullable") { Inner = inner, MayDefer = inner.MayDefer, Hashable = inner.Hashable };
                }
                case ValueKind.Sequence:
                {
                    if (value.Element!.Kind == ValueKind.RawNullable)
                    {
                        var raw = Raw(value.Element.Element!);
                        return new RustShape("seq") { Element = raw, Mode = "rawnullables" };
                    }

                    var element = Shape(value.Element, true);
                    bool set = value.Collection is CollectionKind.HashSet or CollectionKind.SortedSet ||
                               CodegenModel.Implements(value.Clr, "System.Collections.Generic.ISet`1") != null;
                    if (set && !element.Hashable)
                        throw new UnsupportedException(
                            $"{value.ClrName}: a set of values Rust cannot hash (an IndexSet needs Eq and Hash)");
                    if (!set)
                        element = Shape(value.Element, false);
                    return new RustShape("seq") { Element = element, Mode = set ? "set" : "vec", MayDefer = element.MayDefer };
                }
                case ValueKind.Map:
                {
                    var (k, v) = (Shape(value.Key!, true), Shape(value.Value!, false));
                    if (!k.Hashable)
                        throw new UnsupportedException(
                            $"{value.ClrName}: keys Rust cannot hash (an IndexMap needs Eq and Hash)");
                    return new RustShape("map") { Key = k, Value = v, MayDefer = k.MayDefer || v.MayDefer };
                }
                case ValueKind.Tuple:
                {
                    var items = value.Items.Select(i => Shape(i, false)).ToList();
                    return new RustShape("tuple")
                        { Items = items, MayDefer = items.Any(i => i.MayDefer), Hashable = items.All(i => i.Hashable) };
                }
                case ValueKind.Grid:
                {
                    var element = Shape(value.Element!, false);
                    return new RustShape("grid") { Element = element, Rank = value.Rank, MayDefer = element.MayDefer };
                }
                case ValueKind.BigInteger:
                    return new RustShape("bigint") { Hashable = true };
                case ValueKind.Version:
                    return new RustShape("version") { Hashable = true };
                case ValueKind.DateTimeOffset:
                    return new RustShape("datetimeoffset") { Hashable = true };
                case ValueKind.Unsupported:
                    throw new UnsupportedException(value.Problem!.Replace("{lang}", "Rust"));
                default:
                    throw new UnsupportedException($"{value.ClrName} ({value.Encoding}) has no Rust form");
            }
        }

        /// <summary>A <c>[NinoType]</c> member, with the header of its kind.</summary>
        private RustShape TypeShape(TypeModel type)
        {
            if (!_supported.Contains(type))
                throw new UnsupportedException($"{type.FullName} is not written to Rust");
            if (type.Raw)
                return Raw(type.RawValue!);
            return type.Header switch
            {
                HeaderKind.None => new RustShape("struct") { Type = type },
                HeaderKind.NullFlag => new RustShape("class") { Type = type, MayDefer = true },
                _ => new RustShape("slot") { Type = type, MayDefer = true },
            };
        }

        /// <summary>A value copied as raw memory: a number, a BCL value, an enum's integer or a raw struct.</summary>
        private RustShape Raw(ValueModel value)
        {
            switch (value.Kind)
            {
                case ValueKind.Primitive:
                    return new RustShape("prim")
                    {
                        Primitive = value.Primitive.ToString(), Size = value.Size,
                        Align = PrimitiveAlign(value.Primitive),
                        Hashable = value.Primitive is not (PrimitiveKind.F32 or PrimitiveKind.F64),
                    };
                case ValueKind.Enum:
                {
                    var model = value.Enum!;
                    if (model.Underlying == null)
                        throw new UnsupportedException($"{model.FullName}'s underlying type has no Rust form");
                    if (!_enums.Contains(model))
                        _enums.Add(model);
                    var inner = new RustShape("prim")
                    {
                        Primitive = model.Underlying.Value.ToString(), Size = value.Size, Align = value.Size,
                        Hashable = true,
                    };
                    return new RustShape("enum") { Enum = model, Inner = inner, Size = value.Size, Align = value.Size, Hashable = true };
                }
                case ValueKind.RawStruct:
                {
                    var model = value.Struct!;
                    var fields = model.Fields.Select(f => f.Value.Kind is ValueKind.Primitive or ValueKind.Enum or ValueKind.RawStruct
                        ? Raw(f.Value)
                        : throw new UnsupportedException(
                            $"{model.FullName}.{f.Name} ({f.Value.Encoding}) has no Rust form inside a raw struct")).ToList();
                    if (!_rawStructs.Contains(model))
                        _rawStructs.Add(model);
                    int align = 1;
                    int largest = fields.Select(f => f.Align).DefaultIfEmpty(1).Max();
                    // The largest alignment every field's offset allows, as gen.py derives a struct's ALIGN.
                    foreach (int a in new[] { 1, 2, 4, 8 })
                        if (a <= largest && model.Fields.Select((f, i) => f.Offset % Math.Min(fields[i].Align, a) == 0)
                                .All(ok => ok))
                            align = a;
                    return RawShapes[model] = new RustShape("rawstruct")
                    {
                        Struct = model, Size = model.Empty ? 1 : model.Size, Align = align,
                        Hashable = fields.All(f => f.Hashable),
                    };
                }
                case ValueKind.Unsupported:
                    throw new UnsupportedException(value.Problem!.Replace("{lang}", "Rust"));
                default:
                    throw new UnsupportedException($"{value.ClrName} ({value.Encoding}) has no Rust form inside raw memory");
            }
        }

        /// <summary>The alignment of a primitive in raw memory (<c>nino::Raw::ALIGN</c>).</summary>
        private static int PrimitiveAlign(PrimitiveKind kind) => kind switch
        {
            PrimitiveKind.Bool or PrimitiveKind.I8 or PrimitiveKind.U8 => 1,
            PrimitiveKind.I16 or PrimitiveKind.U16 or PrimitiveKind.F16 or PrimitiveKind.Char => 2,
            PrimitiveKind.I32 or PrimitiveKind.U32 or PrimitiveKind.F32 or PrimitiveKind.DateOnly or PrimitiveKind.Guid
                or PrimitiveKind.Rune => 4,
            _ => 8,
        };

        // ---------------------------------------------------------------- names

        /// <summary>Names a generated type must not take: the prelude's and the generated code's own.</summary>
        private static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
        {
            "Option", "Some", "None", "Result", "Ok", "Err", "Vec", "String", "Box", "Default", "Clone", "Copy",
            "Debug", "PartialEq", "Eq", "Hash", "Self", "AnyObject", "NinoVisit", "Any",
        };

        private void AssignNames(IEnumerable<object> types)
        {
            var all = types.Select(Clr).Distinct().ToList();
            var simple = all.ToLookup(t => GoTarget.Go(TypeNames.Simple(t)));
            foreach (var type in all)
            {
                string name = GoTarget.Go(TypeNames.Simple(type));
                if (simple[name].Count() > 1)
                    name = GoTarget.Go((type.Namespace ?? "").Replace('.', '_')) + name;
                if (Reserved.Contains(name))
                    name += "Type";
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
            _names.TryGetValue(Clr(type), out var name) ? name : GoTarget.Go(TypeNames.Simple(Clr(type)));

        private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
        {
            "as", "async", "await", "box", "break", "const", "continue", "dyn", "else", "enum", "extern", "false", "fn",
            "for", "if", "impl", "in", "let", "loop", "match", "mod", "move", "mut", "pub", "ref", "return", "static",
            "struct", "trait", "true", "try", "type", "unsafe", "use", "where", "while", "abstract", "become", "do",
            "final", "macro", "override", "priv", "typeof", "unsized", "virtual", "yield", "gen",
        };

        /// <summary>The Rust field names of C# members: snake_case, unique, keywords raw (<c>r#type</c>).</summary>
        internal static List<(string Field, string Plain)> FieldNames(IEnumerable<string> names)
        {
            var used = new Dictionary<string, int>(StringComparer.Ordinal);
            var list = new List<(string, string)>();
            foreach (var csharp in names)
            {
                string name = TypeNames.Snake(csharp);
                if (name.Length == 0 || !(char.IsLetter(name[0]) || name[0] == '_'))
                    name = "f_" + name;
                used[name] = used.TryGetValue(name, out int count) ? count + 1 : 0;
                if (used[name] != 0)
                    name += "_" + used[name].ToString(CultureInfo.InvariantCulture);
                // self, super and crate cannot be raw identifiers.
                if (name is "self" or "super" or "crate" or "_")
                    name += "_";
                list.Add((Keywords.Contains(name) ? "r#" + name : name, name));
            }

            return list;
        }

        /// <summary>serde's <c>rename_all</c> for the naming chosen, or null.</summary>
        private string? SerdeRule => _options.SerdeNaming switch
        {
            TagNaming.Camel => "camelCase",
            TagNaming.Original => "PascalCase",
            _ => null,
        };

        /// <summary>
        /// The serde name of a member, when <see cref="SerdeRule"/> applied to its Rust name would not give it (null
        /// otherwise): <c>Camel</c>, <c>Snake</c> or the C# name itself.
        /// </summary>
        internal string? SerdeRename(string field, string csharp)
        {
            if (!_options.Serde)
                return null;
            string wanted = _options.SerdeNaming switch
            {
                TagNaming.Snake => TypeNames.Snake(csharp),
                TagNaming.Original => csharp,
                _ => TypeNames.Camel(csharp),
            };
            var words = field.Split('_');
            string serde = _options.SerdeNaming switch
            {
                TagNaming.Camel => words[0] + string.Concat(words.Skip(1).Select(Capital)),
                TagNaming.Original => string.Concat(words.Select(Capital)),
                _ => field,
            };
            return serde == wanted ? null : wanted;

            static string Capital(string w) =>
                w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w.Substring(1);
        }

        /// <summary>A Rust string literal.</summary>
        private static string RustString(string text)
        {
            var sb = new System.Text.StringBuilder("\"");
            foreach (char c in text)
            {
                if (c is '"' or '\\')
                    sb.Append('\\').Append(c);
                else if (c < ' ' || c > '~')
                    sb.Append("\\u{").Append(((int)c).ToString("x", CultureInfo.InvariantCulture)).Append('}');
                else
                    sb.Append(c);
            }

            return sb.Append('"').ToString();
        }
    }

    /// <summary>
    /// How a value is written in Rust (<see cref="Kind"/>: <c>prim</c>, <c>enum</c>, <c>rawstruct</c>, <c>tuple</c>,
    /// <c>string</c>, <c>keystring</c>, <c>utf8</c>, <c>keyutf8</c>, <c>object</c>, <c>struct</c>, <c>class</c>,
    /// <c>slot</c>, <c>named</c>, <c>nullable</c>, <c>seq</c>, <c>map</c>, <c>grid</c>, <c>bigint</c>,
    /// <c>version</c>, <c>datetimeoffset</c>).
    /// </summary>
    public sealed class RustShape
    {
        internal RustShape(string kind) => Kind = kind;

        public string Kind { get; }

        /// <summary>The <see cref="PrimitiveKind"/> of a primitive.</summary>
        public string? Primitive { get; internal set; }

        public int Size { get; internal set; } = -1;

        /// <summary>The alignment of a raw value (<c>nino::Raw::ALIGN</c>).</summary>
        public int Align { get; internal set; } = 1;

        /// <summary>Whether it is <c>Eq + Hash</c>: a set element or map key.</summary>
        public bool Hashable { get; internal set; }

        /// <summary>Whether reading it may defer a class body: read through <c>read_in</c>.</summary>
        public bool MayDefer { get; internal set; }

        /// <summary>A string as <c>nino::WideString</c>.</summary>
        public bool Wide { get; internal set; }

        public EnumModel? Enum { get; internal set; }

        public RawStructModel? Struct { get; internal set; }

        public TypeModel? Type { get; internal set; }

        public RustNamed? Named { get; internal set; }

        public RustShape? Inner { get; internal set; }

        public RustShape? Element { get; internal set; }

        public RustShape? Key { get; internal set; }

        public RustShape? Value { get; internal set; }

        public List<RustShape> Items { get; internal set; } = new();

        /// <summary>A sequence's Rust type: <c>vec</c>, <c>set</c> (<c>IndexSet</c>) or <c>rawnullables</c>.</summary>
        public string? Mode { get; internal set; }

        public int Rank { get; internal set; }
    }

    /// <summary>A collection of itself (<c>class Tree : List&lt;Tree&gt;</c>): a newtype counting its nesting.</summary>
    public sealed class RustNamed
    {
        internal RustNamed(string name, string fullName)
        {
            Name = name;
            FullName = fullName;
        }

        public string Name { get; }

        public string FullName { get; }

        /// <summary>The collection (a <c>seq</c> or <c>map</c>).</summary>
        public RustShape Value { get; internal set; } = null!;
    }

    public sealed class RustEnum
    {
        internal RustEnum(string name, EnumModel model)
        {
            Name = name;
            Model = model;
        }

        public string Name { get; }

        public EnumModel Model { get; }

        /// <summary>The <see cref="PrimitiveKind"/> of its underlying integer.</summary>
        public string Underlying => Model.Underlying!.Value.ToString();
    }

    public sealed class RustField
    {
        internal RustField(string name, string csharp, int offset, RustShape shape)
        {
            Name = name;
            Csharp = csharp;
            Offset = offset;
            Shape = shape;
        }

        public string Name { get; }

        public string Csharp { get; }

        public int Offset { get; }

        public RustShape Shape { get; }

        public string? SerdeRename { get; internal set; }
    }

    /// <summary>A struct copied as raw memory: <c>nino::Raw</c> at its canonical offsets.</summary>
    public sealed class RustRawStruct
    {
        internal RustRawStruct(RustTarget target, RawStructModel model)
        {
            Name = target.Name(model);
            Model = model;
            var names = RustTarget.FieldNames(model.Fields.Select(f => f.Name));
            Shape = target.RawShapes[model];
            Fields = model.Fields.Select((f, i) =>
                new RustField(names[i].Field, f.Name, f.Offset, target.Shape(f.Value, false))
                    { SerdeRename = target.SerdeRename(names[i].Plain, f.Name) }).ToList();
            var used = new bool[Size];
            foreach (var f in Fields)
                for (int i = f.Offset; i < f.Offset + f.Shape.Size && i < Size; i++)
                    used[i] = true;
            Padding = Enumerable.Range(0, Size).Where(i => !used[i]).ToList();
        }

        public string Name { get; }

        public RawStructModel Model { get; }

        /// <summary>The struct as a value: its size, alignment and whether it hashes.</summary>
        internal RustShape Shape { get; }

        public int Size => Shape.Size;

        public int Align => Shape.Align;

        public bool Hashable => Shape.Hashable;

        public List<RustField> Fields { get; }

        /// <summary>The bytes no field covers: zero on the wire.</summary>
        public List<int> Padding { get; }

        /// <summary>The <c>[NinoType]</c> it is, if any: written behind its id in a polymorphic slot.</summary>
        public TypeModel? Type => Model.Layout;
    }

    public sealed class RustMember
    {
        internal RustMember(MemberModel model, string name, RustShape shape, string? serdeRename)
        {
            Model = model;
            Name = name;
            Shape = shape;
            SerdeRename = serdeRename;
        }

        public MemberModel Model { get; }

        public string Name { get; }

        public string Csharp => Model.Name;

        public RustShape Shape { get; }

        public string? SerdeRename { get; }

        /// <summary>A keyed member's key and former keys, as the reader matches them.</summary>
        public string Keys => string.Join(" | ", Model.AllKeys.Select(TypeNames.Hex));
    }

    /// <summary>A <c>[NinoType]</c> as the template writes it.</summary>
    public sealed class RustType
    {
        private readonly RustTarget _target;

        internal RustType(RustTarget target, TypeModel model)
        {
            _target = target;
            Model = model;
            Name = target.Name(model);
        }

        public TypeModel Model { get; }

        public string Name { get; }

        /// <summary><c>raw</c>, <c>struct</c>, <c>class</c> (a presence byte) or <c>typeid</c>.</summary>
        public string Kind => Model.Raw ? "raw" : Model.IsValueType ? "struct" : Model.Header == HeaderKind.TypeId ? "typeid" : "class";

        /// <summary>The id it is written behind (its name's hash for a closed generic type).</summary>
        public int Id => GoTarget.IdOf(Model);

        public string Format => Model.Format switch
        {
            BodyFormat.Keyed => "keyed",
            BodyFormat.VersionTolerant => "tolerant",
            _ => "positional",
        };

        public bool Deferrable => Model.Deferrable ?? true;

        public List<RustMember> Members => _members ??= BuildMembers();

        private List<RustMember>? _members;

        private List<RustMember> BuildMembers()
        {
            var names = RustTarget.FieldNames(Model.Members.Select(m => m.Name));
            return Model.Members.Select((m, i) => new RustMember(m, names[i].Field, _target.Shape(m.Value, false),
                _target.SerdeRename(names[i].Plain, m.Name))).ToList();
        }

        /// <summary>A struct that can hold itself through no class body: its nesting is counted.</summary>
        public bool Counted => Model.IsValueType && !Model.Raw && _target.HoldsItself(Model);

        /// <summary>Whether reading a member may defer a class body.</summary>
        public bool MayDefer => Members.Any(m => m.Shape.MayDefer);

        public string Layout => Model.LayoutText;

        public string RuntimeName => Model.RuntimeName;

        /// <summary>The Rust type a value declared as this type is read as on its own (the corpus's top-level value).</summary>
        public string Top => Model.Raw ? Name
            : Model.Header == HeaderKind.None ? Name
            : Model.Header == HeaderKind.TypeId ? "Option<" + Name + "Ref>"
            : "Option<Box<" + Name + ">>";
    }

    /// <summary>A slot written behind a type id: the enum of the types it allows.</summary>
    public sealed class RustRef
    {
        internal RustRef(string name, string declared, List<TypeModel> allowed)
        {
            Name = name;
            Declared = declared;
            Allowed = allowed;
        }

        public string Name { get; }

        /// <summary>The declared .NET type (<c>object</c> for any).</summary>
        public string Declared { get; }

        public List<TypeModel> Allowed { get; }
    }
}
