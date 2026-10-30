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
        /// (which reads one as U+FFFD).
        /// </summary>
        public bool WideStrings { get; set; }

        /// <summary>
        /// Maps and sets as <c>std::collections::HashMap</c> and <c>HashSet</c> instead of <c>indexmap</c>'s
        /// <c>IndexMap</c> and <c>IndexSet</c> (no dependency, but written in the hash table's order, not the order
        /// read).
        /// </summary>
        public bool StdMaps { get; set; }
    }

    /// <summary>
    /// Rust: a struct per type and its codecs written out (<c>Templates/rust/types.sbn</c>), in the shape of
    /// <c>runtimes/rust/tests/conformance/types.rs</c>: <c>NinoReadBody</c>/<c>NinoWriteBody</c> for bodies, the
    /// runtime's own impls for collections, strings and the BCL values, a raw struct as one fixed-size array, and an
    /// enum per type written behind a type id holding only the types it allows (<c>XRef</c>). A type it cannot express
    /// is left out with a warning, and so is every type holding one.
    /// </summary>
    public sealed class RustTarget : CodegenTarget
    {
        private readonly RustOptions _options;
        private readonly List<EnumModel> _enums = new();
        private readonly List<RawStructModel> _rawStructs = new();
        internal Dictionary<RawStructModel, RustShape> RawShapes { get; } = new();
        private readonly List<RustNamed> _namedCollections = new();
        private readonly Dictionary<TypeModel, HashSet<int>> _hashedParameters = new();
        private readonly Dictionary<SlotModel, string> _slotNames = new();
        private bool _usesObject;

        /// <summary>The generic definition whose members are being shaped: its slot parameters name its open slots.</summary>
        private TypeModel? _context;

        public RustTarget(CodegenModel model, RustOptions options) : base(model) => _options = options;

        protected override string Language => "Rust";

        public string Render(string source)
        {
            ResolveSupported();
            ResolveClosed();
            var types = _model.Types.Where(_supported.Contains).ToList();
            // A raw type no member holds (one only a polymorphic slot can hold) is declared too.
            foreach (var raw in types.Where(t => t.Raw))
                Raw(raw.RawValue!);
            AssignNames(types.Cast<object>().Concat(_enums).Concat(_rawStructs));

            var all = types.Select(t => new RustType(this, t)).ToList();
            foreach (var t in all)
            {
                _ = t.Members;
                _ = t.ElementsMember;
            }

            // The slots of closed generic types: shaping their closed types may name more (until none is added).
            for (int count = -1; count != _slotNames.Count;)
            {
                count = _slotNames.Count;
                foreach (var slot in _slotNames.Keys.ToList())
                    foreach (var closed in slot.ClosedAssignable.Where(_closed.ContainsKey))
                        ClosedShape(closed);
            }

            var rawStructs = _rawStructs.Select(s => new RustRawStruct(this, s)).ToList();

            var refs = types.Where(t => t.Header == HeaderKind.TypeId && !t.Raw && !t.IsGenericDefinition)
                .Select(t => new RustRef(Name(t) + "Ref", t.FullName, Variants(Allowed(t), t.ClosedAssignable, t.TrackReferences))
                    { Tracked = t.TrackReferences })
                .ToList();
            refs.AddRange(_slotNames.Select(p => new RustRef(p.Value, p.Key.RuntimeName,
                Variants(p.Key.Assignable.Where(_supported.Contains), p.Key.ClosedAssignable, false))));
            if (_usesObject)
                refs.Add(new RustRef("AnyObject", "object", Variants(ObjectTypes(), _closed.Keys, false)));

            var renderer = new TemplateRenderer("rust");
            renderer.Set("source", source);
            renderer.Set("serde", _options.Serde);
            renderer.Set("std_maps", _options.StdMaps);
            renderer.Set("serde_rename_all", SerdeRule);
            renderer.Set("enums", _enums.OrderBy(e => Name(e), StringComparer.Ordinal)
                .Select(e => new RustEnum(Name(e), e)).ToList());
            renderer.Set("raw_structs", rawStructs.OrderBy(s => s.Name, StringComparer.Ordinal).ToList());
            renderer.Set("types", all.Where(t => !t.Model.Raw && t.Model.IsConcrete)
                .OrderBy(t => t.Name, StringComparer.Ordinal).ToList());
            renderer.Set("refs", refs.OrderBy(r => r.Name, StringComparer.Ordinal).ToList());
            renderer.Set("named", _namedCollections.OrderBy(n => n.Name, StringComparer.Ordinal).ToList());
            renderer.Set("tops", all.Where(t => !t.Model.IsGenericDefinition)
                .OrderBy(t => t.RuntimeName, StringComparer.Ordinal).ToList());
            renderer.Set("uses_any", refs.Count != 0);
            renderer.Function("hex", new Func<int, string>(TypeNames.Hex));
            renderer.Function("name", new Func<object, string>(Name));
            renderer.Function("type_of", new Func<TypeModel, RustType>(t => new RustType(this, t)));
            renderer.Function("rust_string", new Func<string, string>(RustString));
            return renderer.Render("types.sbn").TrimEnd('\n') + "\n";
        }

        // ---------------------------------------------------------------- which types

        protected override string? Problem(TypeModel type) => CodegenModel.GenericProblem(type) ?? base.Problem(type);

        /// <summary>
        /// The closed generic types the assembly names that Rust can write (<see cref="CodegenModel.ClosedTypes"/>),
        /// with their shapes: a variant each in the slots that allow them.
        /// </summary>
        private readonly Dictionary<ClosedTypeModel, RustShape> _closed = new();

        private void ResolveClosed()
        {
            _closed.Clear();
            foreach (var closed in _model.ClosedTypes.Where(c => _supported.Contains(c.Definition)))
            {
                try
                {
                    _closed[closed] = ClosedShape(closed);
                }
                catch (UnsupportedException e)
                {
                    WarnClosed(closed, e.Message);
                }
            }
        }

        /// <summary>A closed generic type's body: the generic class or struct with its arguments.</summary>
        private RustShape ClosedShape(ClosedTypeModel closed) => GenericShape(closed.Value);

        /// <summary>
        /// The variants of a slot: its types with layouts, then its closed generic types, by name. A tracked class is
        /// a <c>nino::Shared</c>; <paramref name="tracked"/>: the slot's declared type tracks (the reference header
        /// comes before the type id), else a tracked class has its header after its id.
        /// </summary>
        private List<RustVariant> Variants(IEnumerable<TypeModel> types, IEnumerable<ClosedTypeModel> closed, bool tracked)
        {
            var variants = types.OrderBy(Name, StringComparer.Ordinal)
                .Select(t => new RustVariant(Name(t), t, null, Name(t) + "::TYPE_ID", t.IsValueType)
                    { Shared = t.TrackReferences && !t.IsValueType })
                .ToList();
            var taken = new HashSet<string>(variants.Select(v => v.Name), StringComparer.Ordinal);
            foreach (var c in closed.Where(_closed.ContainsKey).OrderBy(c => c.RuntimeName, StringComparer.Ordinal))
            {
                string name = TypeNames.Closed(c.Clr);
                while (!taken.Add(name))
                    name += "_";
                variants.Add(new RustVariant(name, null, _closed[c],
                    c.TypeId.ToString(CultureInfo.InvariantCulture), c.IsValueType) { RuntimeName = c.RuntimeName });
            }

            return variants;
        }

        protected override void Reset()
        {
            _enums.Clear();
            _rawStructs.Clear();
            RawShapes.Clear();
            _namedCollections.Clear();
            _slotNames.Clear();
            _usesObject = false;
        }

        protected override void CheckType(TypeModel type)
        {
            if (type.Raw)
                Raw(type.RawValue!);
            else
            {
                if (type.Elements != null)
                    ShapeIn(type, type.Elements);
                foreach (var member in type.Members)
                    ShapeIn(type, member.Value);
            }
        }

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
                case ValueKind.Parameter:
                    // A type parameter: its argument's NinoRead decides (read_in asks it whether it may defer); a
                    // parameter a map key or set element holds is bound by Eq + Hash, and checked where it is closed.
                    return new RustShape("param") { Parameter = value.ParameterName, MayDefer = true, Hashable = true };
                case ValueKind.Generic:
                {
                    var definition = value.Type!;
                    if (!_supported.Contains(definition))
                        throw new UnsupportedException($"{definition.FullName} is not written to Rust");
                    if (definition.Header == HeaderKind.TypeId)
                        return SlotShape(value);
                    return GenericShape(value);
                }
                case ValueKind.Named:
                {
                    if (!_model.NamedCollections.TryGetValue(value.NamedTypeName ?? "", out var collection))
                        throw new UnsupportedException($"{value.ClrName} is not written to Rust");
                    var named = _namedCollections.FirstOrDefault(n => n.FullName == value.NamedTypeName);
                    if (named == null)
                    {
                        named = new RustNamed(TypeNames.Joined(TypeNames.WithoutArity(
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
                            $"{value.ClrName}: a set of values Rust cannot hash (a set needs Eq and Hash)");
                    if (!set)
                        element = Shape(value.Element, false);
                    return new RustShape("seq") { Element = element, Mode = set ? "set" : "vec", MayDefer = element.MayDefer };
                }
                case ValueKind.Map:
                {
                    var (k, v) = (Shape(value.Key!, true), Shape(value.Value!, false));
                    if (!k.Hashable)
                        throw new UnsupportedException(
                            $"{value.ClrName}: keys Rust cannot hash (a map needs Eq and Hash)");
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

        /// <summary>The shape of a value of <paramref name="type"/>'s, whose open slots are its slot parameters.</summary>
        internal RustShape ShapeIn(TypeModel type, ValueModel value)
        {
            var outer = _context;
            _context = type;
            try
            {
                return Shape(value, false);
            }
            finally
            {
                _context = outer;
            }
        }

        /// <summary>
        /// The names of a generic definition's slot parameters (<see cref="TypeModel.SlotParameters"/>): type parameters
        /// after its own, bound to the enum of each slot where it is closed (<c>S0</c>, <c>S1</c>, ...).
        /// </summary>
        internal static List<string> SlotParameterNames(TypeModel type)
        {
            var taken = new HashSet<string>(type.TypeParameters, StringComparer.Ordinal);
            var names = new List<string>();
            for (int i = 0; i < type.SlotParameters.Count; i++)
            {
                string name = "S" + i.ToString(CultureInfo.InvariantCulture);
                while (!taken.Add(name))
                    name += "_";
                names.Add(name);
            }

            return names;
        }

        /// <summary>
        /// A generic struct or class with its arguments, then the enums of the slots it holds (its slot arguments).
        /// </summary>
        private RustShape GenericShape(ValueModel value)
        {
            var definition = value.Type!;
            var arguments = value.Arguments.Select(a => Shape(a, false)).ToList();
            foreach (int i in HashedParameters(definition))
                if (!arguments[i].Hashable)
                    throw new UnsupportedException(
                        $"{TypeNames.Runtime(value.Clr)}: {definition.TypeParameters[i]} is a key or set element, which Rust cannot hash");
            return new RustShape(definition.IsValueType ? "generic" : "genericclass")
            {
                Type = definition, Items = arguments, MayDefer = true,
                Slots = value.SlotArguments.Select(SlotShape).ToList(),
            };
        }

        /// <summary>
        /// A generic type written behind a type id with its arguments (<c>Shelf&lt;int&gt;</c>): the enum of the types
        /// that slot allows (<c>ShelfInt32Ref</c>), its closed types among them.
        /// </summary>
        private RustShape SlotShape(ValueModel value)
        {
            if (value.Slot is not { } slot)
            {
                // Open (Shelf<T> in a generic definition): a type parameter of the definition holding it, which each
                // closed use binds to the enum of its closed slot.
                int index = _context?.SlotParameters.FindIndex(p => p.TypeKey == value.TypeKey) ?? -1;
                if (index < 0)
                    throw new UnsupportedException($"{TypeNames.Runtime(value.Clr)} is a slot no type parameter names");
                return new RustShape("genslot") { RefName = SlotParameterNames(_context!)[index], MayDefer = true };
            }

            if (!_slotNames.TryGetValue(slot, out var name))
            {
                name = TypeNames.Closed(slot.Clr) + "Ref";
                _slotNames[slot] = name;
            }

            return new RustShape("genslot") { RefName = name, MayDefer = true };
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
                HeaderKind.NullFlag when type.TrackReferences => new RustShape("shared") { Type = type, MayDefer = true },
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

        /// <summary>
        /// The parameters of a generic definition that are map keys or set elements in it (directly, or through the
        /// generic types it holds): bound by <c>Eq + Hash</c>, and given only arguments Rust can hash.
        /// </summary>
        internal HashSet<int> HashedParameters(TypeModel type)
        {
            if (_hashedParameters.TryGetValue(type, out var hashed))
                return hashed;
            hashed = new HashSet<int>();
            _hashedParameters[type] = hashed;
            foreach (var value in type.Members.SelectMany(m => m.Value.Walk()))
            {
                IEnumerable<ValueModel> keys = value.Kind switch
                {
                    ValueKind.Map => new[] { value.Key! },
                    ValueKind.Sequence when value.Collection is CollectionKind.HashSet or CollectionKind.SortedSet ||
                                            CodegenModel.Implements(value.Clr, "System.Collections.Generic.ISet`1") != null
                        => new[] { value.Element! },
                    ValueKind.Generic when value.Type != type => HashedParameters(value.Type!).Select(i => value.Arguments[i]),
                    _ => Array.Empty<ValueModel>(),
                };
                foreach (var parameter in keys.SelectMany(k => k.Walk()).Where(v => v.Kind == ValueKind.Parameter))
                    hashed.Add(parameter.Parameter);
            }

            return hashed;
        }

        // ---------------------------------------------------------------- names

        /// <summary>Names a generated type must not take: the prelude's and the generated code's own.</summary>
        private static readonly HashSet<string> ReservedNames = new(StringComparer.Ordinal)
        {
            "Option", "Some", "None", "Result", "Ok", "Err", "Vec", "String", "Box", "Default", "Clone", "Copy",
            "Debug", "PartialEq", "Eq", "Hash", "Self", "AnyObject", "NinoVisit", "Any",
        };

        protected override string BaseName(Type type) => TypeNames.Joined(TypeNames.Definition(type));

        protected override string Qualified(Type type, string name) =>
            TypeNames.Joined((type.Namespace ?? "").Replace('.', '_')) + name;

        protected override bool Reserved(string name) => ReservedNames.Contains(name);

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
    /// <c>version</c>, <c>datetimeoffset</c>, <c>param</c>: a type parameter, <c>generic</c> and <c>genericclass</c>:
    /// a generic struct or class with its arguments in <see cref="Items"/>).
    /// </summary>
    public sealed class RustShape
    {
        internal RustShape(string kind) => Kind = kind;

        public string Kind { get; }

        /// <summary>The <see cref="PrimitiveKind"/> of a primitive.</summary>
        public string? Primitive { get; internal set; }

        /// <summary>The name of a type parameter (<c>param</c>).</summary>
        public string? Parameter { get; internal set; }

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

        /// <summary>
        /// The enum of a slot of a closed generic type (<c>genslot</c>), or the type parameter standing for it in a
        /// generic definition.
        /// </summary>
        public string? RefName { get; internal set; }

        /// <summary>A generic type's slot arguments (<c>genslot</c> shapes), after its type arguments.</summary>
        public List<RustShape> Slots { get; internal set; } = new();

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

        /// <summary>Whether a type parameter is inside it.</summary>
        public bool Open => Model.Value.IsOpen;

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
            var names = FieldNames();
            int skip = Model.Elements != null ? 1 : 0;
            return Model.Members.Select((m, i) => new RustMember(m, names[i + skip].Field, _target.ShapeIn(Model, m.Value),
                _target.SerdeRename(names[i + skip].Plain, m.Name))).ToList();
        }

        /// <summary>The Rust field names: <c>elements</c> first for a <c>[NinoType]</c> collection, then the members'.</summary>
        private List<(string Field, string Plain)> FieldNames()
        {
            var names = Model.Members.Select(m => m.Name);
            return RustTarget.FieldNames(Model.Elements != null ? names.Prepend("Elements") : names);
        }

        /// <summary>
        /// The elements of a <c>[NinoType]</c> collection: a field before the members holding the collection itself
        /// (never null: the collection is the object), or null.
        /// </summary>
        public RustMember? ElementsMember => Model.Elements == null ? null
            : _elements ??= new RustMember(new MemberModel("$elements", "", Model.Elements), FieldNames()[0].Field,
                _target.ShapeIn(Model, Model.Elements), _target.SerdeRename(FieldNames()[0].Plain, "$elements"));

        private RustMember? _elements;

        /// <summary>Whether reading its elements or a member may defer a class body.</summary>
        public bool ReadsMayDefer => MayDefer || ElementsMember?.Shape.MayDefer == true;

        /// <summary>A class written behind reference headers (trackReferences): held as a <c>nino::Shared</c>.</summary>
        public bool Tracked => Model.TrackReferences;

        /// <summary>
        /// A struct whose nesting is counted: one that can hold itself through no class body, or a generic struct
        /// (whose .NET formatter counts every one).
        /// </summary>
        public bool Counted => Model.IsValueType && !Model.Raw && (Model.IsGenericDefinition || _target.HoldsItself(Model));

        /// <summary>
        /// A struct whose members are all of fixed size (<see cref="TypeModel.FixedSize"/>): its <c>FIXED_SIZE</c> is
        /// their sizes added up (0 when a type argument is not of fixed size), so a list of it is one block.
        /// </summary>
        public bool Fixed => Model.FixedSize;

        /// <summary>Whether reading a member may defer a class body.</summary>
        public bool MayDefer => Members.Any(m => m.Shape.MayDefer) || ElementsMember?.Shape.MayDefer == true;

        /// <summary>The type parameters of a generic definition (empty otherwise).</summary>
        public IReadOnlyList<string> Parameters => Model.TypeParameters;

        /// <summary>
        /// The type parameters standing for the enums of its open slots (<see cref="TypeModel.SlotParameters"/>), after
        /// its own: <c>OpenHolder&lt;T, S0&gt;</c>, used as <c>OpenHolder&lt;i64, OpenInt64Ref&gt;</c>.
        /// </summary>
        public IReadOnlyList<string> SlotParameters => RustTarget.SlotParameterNames(Model);

        private IEnumerable<string> AllParameters => Parameters.Concat(SlotParameters);

        /// <summary>
        /// The marker field for type parameters no field holds (<c>T</c> of <c>OpenHolder&lt;T, S0&gt;</c>, held only
        /// through its slots' enums), which Rust requires to be used: <c>PhantomData&lt;fn() -&gt; (T,)&gt;</c>, or null.
        /// </summary>
        public string? Phantom
        {
            get
            {
                var shapes = Members.Select(m => m.Shape).Concat(ElementsMember != null ? new[] { ElementsMember.Shape } : Array.Empty<RustShape>());
                var used = new HashSet<string>(shapes.SelectMany(Walk).Where(s => s.Kind == "param").Select(s => s.Parameter!),
                    StringComparer.Ordinal);
                var unused = Parameters.Where(p => !used.Contains(p)).ToList();
                return unused.Count == 0 ? null
                    : "core::marker::PhantomData<fn() -> (" + string.Join(", ", unused) + ",)>";
            }
        }

        private static IEnumerable<RustShape> Walk(RustShape s) => new[] { s }
            .Concat(new[] { s.Inner, s.Element, s.Key, s.Value }.Where(c => c != null).SelectMany(c => Walk(c!)))
            .Concat(s.Items.SelectMany(Walk));

        /// <summary><c>&lt;T, U&gt;</c> after the name of a generic type, else empty.</summary>
        public string Generics => Parameters.Count == 0 ? "" : "<" + string.Join(", ", AllParameters) + ">";

        /// <summary>The bounds the struct declares: <c>Eq + Hash</c> on the parameters it hashes (its derives need them).</summary>
        public string DeclaredGenerics => Parameters.Count == 0 ? "" :
            "<" + string.Join(", ", Parameters.Select((p, i) => _target.HashedParameters(Model).Contains(i)
                ? p + ": Eq + core::hash::Hash"
                : p).Concat(SlotParameters)) + ">";

        /// <summary>
        /// The parameters of an impl of a generic type, with every bound its codecs need (a slot's enum has no
        /// <c>Default</c>: its field is an <c>Option</c>).
        /// </summary>
        public string ImplGenerics => Parameters.Count == 0 ? "" :
            "<" + string.Join(", ", Parameters.Select((p, i) => p + ": NinoRead + NinoWrite + Default" +
                (_target.HashedParameters(Model).Contains(i) ? " + Eq + core::hash::Hash + Clone" : "") +
                " + 'static").Concat(SlotParameters.Select(p => p + ": NinoRead + NinoWrite + 'static"))) + ">";

        /// <summary>
        /// The bounds of its <c>Default</c> impl, written out for a type with slot parameters (a derive would ask the
        /// slots' enums for one), else null.
        /// </summary>
        public string? DefaultGenerics => SlotParameters.Count == 0 ? null :
            "<" + string.Join(", ", Parameters.Select((p, i) => p + ": Default" +
                (_target.HashedParameters(Model).Contains(i) ? " + Eq + core::hash::Hash" : "")).Concat(SlotParameters)) + ">";


        public string Layout => Model.LayoutText;

        public string RuntimeName => Model.RuntimeName;

        /// <summary>The Rust type a value declared as this type is read as on its own (the corpus's top-level value).</summary>
        public string Top => Model.Raw ? Name
            : Model.Header == HeaderKind.None ? Name
            : Model.Header == HeaderKind.TypeId ? "Option<" + Name + "Ref>"
            : Model.TrackReferences ? "Option<nino::Shared<" + Name + ">>"
            : "Option<Box<" + Name + ">>";
    }

    /// <summary>A slot written behind a type id: the enum of the types it allows.</summary>
    public sealed class RustRef
    {
        internal RustRef(string name, string declared, List<RustVariant> allowed)
        {
            Name = name;
            Declared = declared;
            Allowed = allowed;
        }

        public string Name { get; }

        /// <summary>The declared .NET type (<c>object</c> for any).</summary>
        public string Declared { get; }

        public List<RustVariant> Allowed { get; }

        /// <summary>Its declared type tracks references: the reference header comes first, then the type id.</summary>
        public bool Tracked { get; internal set; }
    }

    /// <summary>
    /// A type a slot allows: a type with a layout (<see cref="Type"/>), or a closed generic type (<see cref="Shape"/>,
    /// the generic type with its arguments) behind the id of its closed .NET name.
    /// </summary>
    public sealed class RustVariant
    {
        internal RustVariant(string name, TypeModel? type, RustShape? shape, string id, bool isValueType)
        {
            Name = name;
            Type = type;
            Shape = shape;
            Id = id;
            IsValueType = isValueType;
        }

        /// <summary>The enum variant's name.</summary>
        public string Name { get; }

        public TypeModel? Type { get; }

        public RustShape? Shape { get; }

        /// <summary>Its type id, as a Rust expression (<c>Cat::TYPE_ID</c>, or the closed type's id).</summary>
        public string Id { get; }

        public bool IsValueType { get; }

        /// <summary>The closed type's .NET name, for a closed generic type.</summary>
        public string? RuntimeName { get; internal set; }

        /// <summary>A tracked class: held as a <c>nino::Shared</c>.</summary>
        public bool Shared { get; internal set; }
    }
}
