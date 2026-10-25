using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Nino.Codegen.Model;

namespace Nino.Codegen.Targets
{
    /// <summary>How the extra struct tags name a member (runtimes/go/README.md, "Tags on generated structs").</summary>
    public enum TagNaming
    {
        Camel,
        Snake,
        Original,
    }

    /// <summary>Options of the generated Go.</summary>
    public sealed class GoOptions
    {
        /// <summary>The package of the generated file.</summary>
        public string Package { get; set; } = "ninotypes";

        /// <summary>The struct tags written after <c>nino</c>, in this order (<c>json</c>, <c>yaml</c>).</summary>
        public List<string> Tags { get; set; } = new() { "json", "yaml" };

        public TagNaming TagNaming { get; set; } = TagNaming.Camel;
    }

    /// <summary>
    /// Go: a struct per type with its <c>nino</c> tags and <c>NinoLayout</c>, and codecs in the style of
    /// <c>runtimes/go/internal/sample</c> (<c>Templates/go/types.sbn</c>): a class through <c>nino.Class</c>, a
    /// polymorphic slot as a Go interface switching over the ids its declared type allows (a generic one as a generic
    /// interface, marked with the type arguments: <c>Shelf[int32]</c>), a run of fixed-size members with one bounds
    /// check, tracked types behind reference headers, a <c>[NinoType]</c> collection with its elements in a field. A
    /// type it cannot express (a generic type holding itself with other type arguments, which Go's generics reject) is
    /// left out with a warning, and so is every type holding one.
    /// </summary>
    public sealed class GoTarget
    {
        public const string RuntimeImport = "github.com/JasonXuDeveloper/Nino/runtimes/go/v4";

        private readonly CodegenModel _model;
        private readonly GoOptions _options;
        private readonly HashSet<TypeModel> _supported = new();
        private readonly Dictionary<Type, string> _names = new();
        private readonly List<EnumModel> _enums = new();
        private readonly List<RawStructModel> _rawStructs = new();
        private readonly List<GoNamed> _namedCollections = new();
        private readonly Dictionary<string, GoHelper> _helperByKey = new(StringComparer.Ordinal);
        private readonly List<GoHelper> _helpers = new();
        private readonly HashSet<int> _tuples = new();
        private readonly Dictionary<TypeModel, GoGeneric> _generics = new();
        private readonly Dictionary<string, GoClosed> _closedByKey = new(StringComparer.Ordinal);
        private readonly List<GoClosed> _closed = new();
        private readonly HashSet<ClosedTypeModel> _closedSupported = new();
        private readonly List<GoSlotClosed> _slotClosed = new();
        private readonly Dictionary<SlotModel, GoSlot> _slotByModel = new();
        private readonly List<GoSlot> _slots = new();
        private GoGeneric? _context;
        private bool _usesObject;
        private bool _usesGrid;

        public List<string> Warnings { get; } = new();

        public GoTarget(CodegenModel model, GoOptions options)
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
            ResolveClosed();
            var types = _model.Types.Where(_supported.Contains).ToList();
            // A raw type no member holds (one only a polymorphic slot can hold) is declared too.
            foreach (var raw in types.Where(t => t.Raw))
                Raw(raw.RawValue!);
            AssignNames(types.Cast<object>().Concat(_enums).Concat(_rawStructs));

            var classes = types.Select(t => new GoType(this, t)).ToList();
            // Shape every member once more, so every helper, tuple, enum and slot is known before the template asks.
            foreach (var c in classes)
            {
                _ = c.Members;
                _ = c.ElementsMember;
                _ = c.Markers;
            }

            // Those a slot can hold: an object slot any, a typed slot those assignable to its type, a slot of a closed
            // generic type those assignable to it (shaping one may add slots its codec needs: until none is added).
            var added = new HashSet<ClosedTypeModel>();
            var goTypes = new Dictionary<string, ClosedTypeModel>(StringComparer.Ordinal);
            for (bool more = true; more;)
            {
                more = false;
                foreach (var closed in _model.ClosedTypes.Where(c => _closedSupported.Contains(c) && !added.Contains(c) &&
                             (_usesObject || types.Any(t => t.Header == HeaderKind.TypeId && t.ClosedAssignable.Contains(c)) ||
                              _slots.Any(slot => slot.Model.ClosedAssignable.Contains(c)))).ToList())
                {
                    added.Add(closed);
                    var shape = ClosedShape(closed);
                    // Two .NET types of one Go type (Box<int?[]> and Box<List<int?>> are both Box[[]*int32]): a
                    // writer cannot tell them apart, so only the first is in the slots.
                    if (goTypes.TryGetValue(GoTypeKey(shape), out var same))
                    {
                        Warnings.Add($"{closed.RuntimeName} is left out of the polymorphic slots in Go: it is the same " +
                                     $"Go type as {same.RuntimeName}.");
                        continue;
                    }

                    goTypes[GoTypeKey(shape)] = closed;
                    _slotClosed.Add(new GoSlotClosed(
                        "typeV" + _slotClosed.Count.ToString(CultureInfo.InvariantCulture), closed, shape));
                    more = true;
                }
            }

            var renderer = new TemplateRenderer("go");
            renderer.Set("source", source);
            renderer.Set("package", _options.Package);
            renderer.Set("runtime_import", RuntimeImport);
            renderer.Set("enums", _enums.OrderBy(e => Name(e), StringComparer.Ordinal)
                .Select(e => new GoEnum(Name(e), e, Primitives[e.Underlying!.Value].Name)).ToList());
            renderer.Set("extra_tags", _options.Tags);
            renderer.Set("raw_structs", _rawStructs.OrderBy(s => Name(s), StringComparer.Ordinal)
                .Select(s => new GoRawStruct(this, s)).ToList());
            renderer.Set("types", classes.OrderBy(c => c.Name, StringComparer.Ordinal).ToList());
            renderer.Set("named", _namedCollections.OrderBy(n => n.Name, StringComparer.Ordinal).ToList());
            renderer.Set("tuples", _tuples.OrderBy(n => n).ToList());
            renderer.Set("uses_object", _usesObject);
            renderer.Set("uses_grid", _usesGrid);
            renderer.Set("closed", _closed.ToList());
            renderer.Set("uses_args", _closed.Count != 0 || _slots.Count != 0 || types.Any(t => t.IsGenericDefinition));
            renderer.Set("objects", ObjectTypes().Select(t => new GoType(this, t)).ToList());
            renderer.Set("slot_closed", _slotClosed.ToList());
            renderer.Set("generic_slots", _slots.ToList());
            renderer.Set("uses_elements", types.Any(t => t.Elements != null));
            renderer.Function("helper", new Func<GoShape, string, string>(Helper));
            renderer.Function("helper_count", new Func<int>(() => _helpers.Count));
            renderer.Function("helper_at", new Func<int, GoHelper>(i => _helpers[i]));
            renderer.Function("tag_name", new Func<string, string>(TagName));
            renderer.Function("hex", new Func<int, string>(TypeNames.Hex));
            renderer.Function("type_of", new Func<TypeModel, GoType>(t => new GoType(this, t)));
            renderer.Function("name", new Func<object, string>(Name));
            renderer.Function("lower", new Func<string, string>(n =>
                n.Length == 0 ? n : char.ToLowerInvariant(n[0]) + n.Substring(1)));
            // A sum as gofmt prints it among a call's arguments.
            renderer.Function("compact", new Func<string, string>(e => e.Replace(" + ", "+")));
            renderer.Function("upper_first", new Func<string, string>(TypeNames.Pascal));
            renderer.Function("go_helper", new Func<string, string, GoShape, GoHelper>((n, mode, shape) =>
                new GoHelper(n, mode, shape)));
            // Columns as gofmt aligns them: the text, then spaces up to the width.
            renderer.Function("pad", new Func<string, int, string>((text, width) => text.PadRight(width)));
            renderer.Function("go_string", new Func<string, string>(GoString));
            renderer.Function("plus", new Func<GoShape, GoShape, string>((a, b) =>
                a.MinExpression == null && b.MinExpression == null
                    ? (a.MinSize + b.MinSize).ToString(CultureInfo.InvariantCulture)
                    : a.Min + " + " + b.Min));
            return renderer.Render("types.sbn").TrimEnd('\n') + "\n";
        }

        // ---------------------------------------------------------------- which types

        private void ResolveSupported()
        {
            foreach (var type in _model.Types)
            {
                string? reason = CodegenModel.GenericProblem(type) ?? type.SlotProblem ?? MarkerProblem(type)
                    ?? (type.TrackReferences && type.IsGenericDefinition
                        ? "a generic type with trackReferences is not supported"
                        : null);
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

        /// <summary>
        /// Why a type cannot be marked as a value of the generic slots it sits in (<see cref="TypeModel.SlotBases"/>): Go
        /// marks it with one method per generic definition, taking the type arguments, so it cannot sit in two slots of
        /// one definition with other arguments (<c>IHolder&lt;int&gt;</c> and <c>IHolder&lt;string&gt;</c>).
        /// </summary>
        private static string? MarkerProblem(TypeModel type) => type.SlotBases.GroupBy(b => b.Type)
            .Where(g => g.Select(b => b.TypeKey).Distinct(StringComparer.Ordinal).Count() > 1)
            .Select(g => $"it implements {g.Key!.FullName} with two sets of type arguments, which one Go method " +
                         "cannot mark").FirstOrDefault();

        /// <summary>
        /// The closed generic types the assembly names (<see cref="CodegenModel.ClosedTypes"/>) that Go can write
        /// behind their type ids, in the polymorphic slots they are assignable to.
        /// </summary>
        private void ResolveClosed()
        {
            foreach (var closed in _model.ClosedTypes)
            {
                if (!_supported.Contains(closed.Definition))
                    continue;
                try
                {
                    ClosedShape(closed);
                    _closedSupported.Add(closed);
                }
                catch (UnsupportedException e)
                {
                    Warnings.Add($"{closed.RuntimeName} is left out of the polymorphic slots in Go: {e.Message}.");
                }
            }

            Reset();
        }

        /// <summary>A closed generic type in a slot: the shapes of its arguments, its codec built in <c>init</c>.</summary>
        private GoShape ClosedShape(ClosedTypeModel closed) => new("generic")
        {
            Type = closed.Definition, Items = closed.Value.Arguments.Select(a => Shape(a, false)).ToList(),
            Slots = closed.Value.SlotArguments.Select(a => Shape(a, false)).ToList(),
            MinSize = closed.IsValueType ? 0 : 1,
        };

        /// <summary>The slots of closed generic types and the types and closed types each allows.</summary>
        internal List<GoType> SlotAllowed(GoSlot slot) => slot.Model.Assignable.Where(_supported.Contains)
            .OrderBy(Name, StringComparer.Ordinal).Select(t => new GoType(this, t)).ToList();

        internal List<GoSlotClosed> SlotAllowedClosed(GoSlot slot) =>
            _slotClosed.Where(c => slot.Model.ClosedAssignable.Contains(c.Closed)).ToList();

        /// <summary>The closed generic types a slot declared as <paramref name="type"/> allows.</summary>
        internal List<GoSlotClosed> AllowedClosed(TypeModel type) =>
            _slotClosed.Where(c => type.ClosedAssignable.Contains(c.Closed)).ToList();

        /// <summary>The closed types of a generic definition that sit in a slot.</summary>
        internal List<GoSlotClosed> ClosedOf(TypeModel definition) =>
            _slotClosed.Where(c => c.Closed.Definition == definition).ToList();

        private void Reset()
        {
            _enums.Clear();
            _rawStructs.Clear();
            _namedCollections.Clear();
            _helperByKey.Clear();
            _helpers.Clear();
            _tuples.Clear();
            _generics.Clear();
            _closedByKey.Clear();
            _closed.Clear();
            _slotByModel.Clear();
            _slots.Clear();
            _context = null;
            _usesObject = false;
            _usesGrid = false;
        }

        private string? Check(TypeModel type)
        {
            try
            {
                if (type.Raw)
                    Shape(type.RawValue!, false);
                else
                {
                    if (type.Elements != null)
                        ShapeIn(type, type.Elements);
                    foreach (var member in type.Members)
                        ShapeIn(type, member.Value);
                    foreach (var slot in type.SlotBases)
                        Markers(type, slot);
                }

                return null;
            }
            catch (UnsupportedException e)
            {
                return e.Message;
            }
        }

        private void Warn(TypeModel type, string reason) =>
            Warnings.Add($"{type.FullName} is not written to Go: {reason}.");

        /// <summary>The type id a type is written behind: its published one, or (a closed generic) its name's.</summary>
        internal static int IdOf(TypeModel type) => type.TypeId != 0 ? type.TypeId : TypeNames.Hash(type.RuntimeName);

        /// <summary>
        /// The shape of a member of <paramref name="owner"/>: in a generic definition, the values holding its type
        /// parameters are read through the codecs its own codec holds (<see cref="GoGeneric"/>).
        /// </summary>
        internal GoShape ShapeIn(TypeModel owner, ValueModel value)
        {
            var outer = _context;
            _context = owner.IsGenericDefinition ? GenericOf(owner) : null;
            try
            {
                return Shape(value, false);
            }
            finally
            {
                _context = outer;
            }
        }

        internal GoGeneric GenericOf(TypeModel type)
        {
            if (!_generics.TryGetValue(type, out var generic))
                _generics[type] = generic = new GoGeneric(type);
            return generic;
        }

        /// <summary>
        /// A value holding a type parameter, in a generic definition: read and written through a codec its codec
        /// builds once (a field <c>c.dN</c>), from the codecs of its arguments.
        /// </summary>
        private GoShape Open(ValueModel value, GoShape shape, bool generic)
        {
            if (_context == null || !value.IsOpen)
                return shape;
            string key = shape.Identity;
            if (!_context.ByKey.TryGetValue(key, out var derived))
            {
                derived = new GoDerived("d" + _context.Derived.Count.ToString(CultureInfo.InvariantCulture), shape,
                    generic);
                _context.ByKey[key] = derived;
                _context.Derived.Add(derived);
            }

            shape.Field = "c." + derived.Name;
            return shape;
        }

        /// <summary>A closed generic type: its codec, built once in the package (a variable <c>codecVn</c>).</summary>
        private GoShape Closed(GoShape shape)
        {
            string key = shape.Identity;
            if (!_closedByKey.TryGetValue(key, out var closed))
            {
                closed = new GoClosed("codecV" + _closed.Count.ToString(CultureInfo.InvariantCulture), shape);
                _closedByKey[key] = closed;
                _closed.Add(closed);
            }

            shape.Field = closed.Name;
            return shape;
        }

        /// <summary>The concrete types an <c>object</c> slot allows: every one written behind a type id.</summary>
        private IEnumerable<TypeModel> ObjectTypes() =>
            _model.Types.Where(t => _supported.Contains(t) && t.IsConcrete && !t.IsGenericDefinition)
                .OrderBy(Name, StringComparer.Ordinal);

        /// <summary>The concrete types a slot declared as <paramref name="type"/> allows.</summary>
        internal List<TypeModel> Allowed(TypeModel type) =>
            _model.Types.Where(t => _supported.Contains(t) && t.IsConcrete && !t.IsGenericDefinition &&
                                    type.IsAssignableFrom(t))
                .OrderBy(Name, StringComparer.Ordinal).ToList();

        /// <summary>The slots (types written behind a type id with more than themselves) a type can sit in.</summary>
        internal List<TypeModel> SlotsOf(TypeModel type) =>
            _model.Types.Where(t => _supported.Contains(t) && t.Header == HeaderKind.TypeId && IsInterfaceSlot(t) &&
                                    (type.IsGenericDefinition
                                        ? t.ClosedAssignable.Any(c => c.Definition == type && _closedSupported.Contains(c))
                                        : t.IsAssignableFrom(type)))
                .OrderBy(Name, StringComparer.Ordinal).ToList();

        /// <summary>
        /// Whether a slot declared as <paramref name="type"/> is a Go interface: it can hold another type than itself
        /// (an abstract class, an interface, a class with subtypes). A class without subtypes is a <c>*T</c>.
        /// </summary>
        internal bool IsInterfaceSlot(TypeModel type)
        {
            var allowed = Allowed(type);
            return !(allowed.Count == 1 && allowed[0] == type &&
                     !type.ClosedAssignable.Any(_closedSupported.Contains));
        }

        /// <summary>
        /// The non-raw structs a value holds without a class body between: itself, or (behind a type id, in an
        /// <c>object</c> or interface slot) any struct the slot allows.
        /// </summary>
        internal IEnumerable<TypeModel> StructsIn(ValueModel value)
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

        /// <summary>The Go interface of a slot: the type's name, or <c>XType</c> for a class with subtypes.</summary>
        internal string SlotName(TypeModel type) => type.IsConcrete ? Name(type) + "Type" : Name(type);

        // ---------------------------------------------------------------- shapes

        /// <summary>
        /// The Go shape of a value. <paramref name="key"/>: a dictionary key, where a string is a <c>string</c> (null
        /// is invalid) rather than a <c>*string</c>.
        /// </summary>
        internal GoShape Shape(ValueModel value, bool key)
        {
            switch (value.Kind)
            {
                case ValueKind.Primitive:
                case ValueKind.Enum:
                case ValueKind.RawStruct:
                case ValueKind.RawNullable:
                case ValueKind.RawTuple:
                    return Raw(value);
                case ValueKind.String:
                    return new GoShape(key ? "keystring" : "string") { MinSize = 1 };
                case ValueKind.Utf8:
                    return new GoShape(key ? "keyutf8" : "utf8") { MinSize = 1 };
                case ValueKind.Object:
                    _usesObject = true;
                    return new GoShape("object") { MinSize = 4 };
                case ValueKind.Type:
                    return TypeShape(value.Type!);
                case ValueKind.Parameter:
                {
                    if (_context == null)
                        throw new UnsupportedException($"{value.ParameterName} is a type parameter outside its type");
                    string field = "c.p" + value.Parameter.ToString(CultureInfo.InvariantCulture);
                    return new GoShape("param")
                        { Parameter = value.ParameterName, Field = field, MinExpression = field + ".MinSize" };
                }
                case ValueKind.Generic:
                {
                    var definition = value.Type!;
                    if (!_supported.Contains(definition))
                        throw new UnsupportedException($"{definition.FullName} is not written to Go");
                    if (definition.Header == HeaderKind.TypeId)
                        return SlotShape(value);
                    var shape = new GoShape("generic")
                    {
                        Type = definition, Items = value.Arguments.Select(a => Shape(a, false)).ToList(),
                        Slots = value.SlotArguments.Select(a => Shape(a, false)).ToList(),
                        MinSize = definition.IsValueType ? 0 : 1,
                    };
                    if (_context != null && value.IsSelf(_context.Type))
                    {
                        shape.Field = "c.self";
                        return shape;
                    }

                    return value.IsOpen ? Open(value, shape, true) : Closed(shape);
                }
                case ValueKind.Named:
                {
                    if (!_model.NamedCollections.TryGetValue(value.NamedTypeName ?? "", out var collection))
                        throw new UnsupportedException($"{value.ClrName} is not written to Go");
                    var named = _namedCollections.FirstOrDefault(n => n.FullName == value.NamedTypeName);
                    if (named == null)
                    {
                        named = new GoNamed(TypeNames.Pascal(string.Concat(
                            TypeNames.WithoutArity(value.NamedTypeName!.Split('.', '+').Last())
                                .Split('_').Select(TypeNames.Pascal))), value.NamedTypeName!);
                        _namedCollections.Add(named);
                        named.Value = Shape(collection, false);
                    }

                    return new GoShape("named") { Named = named, MinSize = 4 };
                }
                case ValueKind.Nullable:
                {
                    var inner = Shape(value.Element!, false);
                    return Open(value, new GoShape("nullable") { Inner = inner, MinSize = 1 }, false);
                }
                case ValueKind.Sequence:
                {
                    var element = Shape(value.Element!, false);
                    bool set = value.Collection is CollectionKind.HashSet or CollectionKind.SortedSet ||
                               CodegenModel.Implements(value.Clr, "System.Collections.Generic.ISet`1") != null;
                    string mode =
                        element.Kind == "prim" && element.Primitive == "U8" ? "bytes"
                        : set && Comparable(element) ? "unique"
                        : set && element.Kind is "string" or "utf8" ? "stringset"
                        : element.Kind == "prim" && element.Primitive == "Bool" ? "bools"
                        : element.Block ? "block"
                        : element.Kind is "rawstruct" or "rawnullable" or "rawtuple" ? "fixed"
                        : "slice";
                    return Open(value, new GoShape("seq") { Element = element, Mode = mode, MinSize = 4 }, false);
                }
                case ValueKind.Map:
                {
                    var (k, v) = (Shape(value.Key!, true), Shape(value.Value!, false));
                    return Open(value, new GoShape("map")
                    {
                        Key = k, Value = v, Mode = Comparable(k) || k.Kind is "keystring" or "keyutf8" ? "unique" : "entries",
                        MinSize = 4,
                    }, false);
                }
                case ValueKind.Tuple:
                {
                    var items = value.Items.Select(i => Shape(i, false)).ToList();
                    if (!value.IsPair)
                        _tuples.Add(items.Count);
                    var tuple = new GoShape("tuple") { Items = items, Pair = value.IsPair, MinSize = items.Sum(i => i.MinSize) };
                    if (items.Any(i => i.MinExpression != null))
                        tuple.MinExpression = string.Join(" + ", items.Select(i => i.Min));
                    return Open(value, tuple, false);
                }
                case ValueKind.Grid:
                    _usesGrid = true;
                    return Open(value,
                        new GoShape("grid") { Element = Shape(value.Element!, false), Rank = value.Rank, MinSize = 4 }, false);
                case ValueKind.BigInteger:
                    return new GoShape("bigint") { MinSize = 4 };
                case ValueKind.Version:
                    return new GoShape("version") { MinSize = 4 };
                case ValueKind.DateTimeOffset:
                    return new GoShape("datetimeoffset") { MinSize = 10 };
                case ValueKind.Unsupported:
                    throw new UnsupportedException(value.Problem!.Replace("{lang}", "Go"));
                default:
                    throw new UnsupportedException($"{value.ClrName} ({value.Encoding}) has no Go form");
            }
        }

        /// <summary>
        /// A generic type written behind a type id (<c>Shelf&lt;int&gt;</c>), a value of its generic interface
        /// (<c>Shelf[int32]</c>): closed, read and written by the slot's functions (<c>readSlot0</c>); in a generic
        /// definition, with its parameters, by the slot codec its codec was given (<c>c.s0</c>).
        /// </summary>
        private GoShape SlotShape(ValueModel value)
        {
            var shape = new GoShape("genslot")
            {
                Type = value.Type, Items = value.Arguments.Select(a => Shape(a, false)).ToList(), MinSize = 4,
            };
            if (value.Slot is { } model)
            {
                if (!_slotByModel.TryGetValue(model, out var slot))
                {
                    slot = new GoSlot(this, "slot" + _slots.Count.ToString(CultureInfo.InvariantCulture), model, shape);
                    _slotByModel[model] = slot;
                    _slots.Add(slot);
                    // Its closed types are shaped too, so a slot holding one that cannot be written is found here.
                    foreach (var closed in model.ClosedAssignable.Where(_closedSupported.Contains))
                        ClosedShape(closed);
                }

                shape.Slot = slot;
                return shape;
            }

            int index = _context?.Type.SlotParameters.FindIndex(p => p.TypeKey == value.TypeKey) ?? -1;
            if (index < 0)
                throw new UnsupportedException($"{value.ClrName} is a slot outside its generic type");
            shape.Field = "c.s" + index.ToString(CultureInfo.InvariantCulture);
            return shape;
        }

        /// <summary>
        /// A generic slot a type sits in (one of <see cref="TypeModel.SlotBases"/>): the arguments its marker method
        /// takes (<c>isShelf(int32)</c>), in the type's own terms.
        /// </summary>
        internal GoMarker Markers(TypeModel type, ValueModel slot)
        {
            var definition = slot.Type!;
            if (!_supported.Contains(definition))
                throw new UnsupportedException($"{definition.FullName} is not written to Go");
            var outer = _context;
            _context = type.IsGenericDefinition ? GenericOf(type) : null;
            try
            {
                var items = slot.Arguments.Select(a => Shape(a, false)).ToList();
                return new GoMarker(SlotName(definition), items);
            }
            finally
            {
                _context = outer;
            }
        }

        /// <summary>The Go type of a shape, as text that is equal for equal Go types (the template spells it).</summary>
        private static string GoTypeKey(GoShape s) => s.Kind switch
        {
            "prim" => s.Primitive!,
            "enum" => "enum:" + s.Enum!.FullName,
            "rawstruct" or "emptystruct" => "struct:" + s.Struct!.FullName,
            "rawnullable" or "nullable" => "*" + GoTypeKey(s.Inner!),
            "rawtuple" or "tuple" => (s.Pair ? "entry" : "tuple") + "[" + string.Join(",", s.Items.Select(GoTypeKey)) + "]",
            "string" or "utf8" => "*string",
            "keystring" or "keyutf8" => "string",
            "struct" or "class" or "slot" => s.Kind + ":" + s.Type!.FullName,
            "param" => "param:" + s.Parameter,
            "generic" or "genslot" => s.Kind + ":" + s.Type!.FullName + "[" + string.Join(",", s.Items.Select(GoTypeKey)) + "]",
            "named" => "named:" + s.Named!.FullName,
            "seq" => "[]" + GoTypeKey(s.Element!),
            "map" => "[]entry[" + GoTypeKey(s.Key!) + "," + GoTypeKey(s.Value!) + "]",
            "grid" => "grid[" + GoTypeKey(s.Element!) + "]",
            _ => s.Kind,
        };

        /// <summary>Whether values of the shape are Go-comparable and compared by value in .NET (sets, keys).</summary>
        private static bool Comparable(GoShape shape) => shape.Kind is "prim" or "enum" or "rawstruct" or "rawtuple";

        /// <summary>A <c>[NinoType]</c> member, with the header of its kind.</summary>
        private GoShape TypeShape(TypeModel type)
        {
            if (!_supported.Contains(type))
                throw new UnsupportedException($"{type.FullName} is not written to Go");
            if (type.Raw)
                return Raw(type.RawValue!);
            return type.Header switch
            {
                HeaderKind.None => new GoShape("struct") { Type = type, MinSize = StructMinSize(type) },
                HeaderKind.NullFlag => new GoShape("class") { Type = type, MinSize = type.TrackReferences ? 4 : 1 },
                _ => new GoShape("slot") { Type = type, MinSize = 4 },
            };
        }

        private readonly HashSet<TypeModel> _sizing = new();

        /// <summary>The fewest bytes a struct's body takes (a collection's elements are checked against it).</summary>
        private int StructMinSize(TypeModel type)
        {
            if (type.Format != BodyFormat.Positional)
                return 4;
            // A generic struct's members are sized by its arguments: none is counted on.
            if (type.IsGenericDefinition)
                return 0;
            if (!_sizing.Add(type))
                return 0;
            try
            {
                return type.Members.Sum(m => Shape(m.Value, false).MinSize);
            }
            finally
            {
                _sizing.Remove(type);
            }
        }

        private static readonly Dictionary<PrimitiveKind, (string Name, bool Block, bool Validated)> Primitives = new()
        {
            [PrimitiveKind.Bool] = ("Bool", false, true),
            [PrimitiveKind.I8] = ("I8", true, false),
            [PrimitiveKind.U8] = ("U8", true, false),
            [PrimitiveKind.I16] = ("I16", true, false),
            [PrimitiveKind.U16] = ("U16", true, false),
            [PrimitiveKind.I32] = ("I32", true, false),
            [PrimitiveKind.U32] = ("U32", true, false),
            [PrimitiveKind.I64] = ("I64", true, false),
            [PrimitiveKind.U64] = ("U64", true, false),
            [PrimitiveKind.NInt] = ("I64", true, false),
            [PrimitiveKind.NUInt] = ("U64", true, false),
            [PrimitiveKind.F16] = ("Half", true, false),
            [PrimitiveKind.F32] = ("F32", true, false),
            [PrimitiveKind.F64] = ("F64", true, false),
            [PrimitiveKind.Char] = ("Char", true, false),
            [PrimitiveKind.I128] = ("Int128", false, false),
            [PrimitiveKind.U128] = ("UInt128", false, false),
            [PrimitiveKind.Guid] = ("Guid", false, false),
            [PrimitiveKind.TimeSpan] = ("TimeSpan", true, false),
            [PrimitiveKind.Decimal] = ("Decimal", false, true),
            [PrimitiveKind.DateTime] = ("DateTime", false, true),
            [PrimitiveKind.DateOnly] = ("DateOnly", false, true),
            [PrimitiveKind.TimeOnly] = ("TimeOnly", false, true),
        };

        /// <summary>A value copied as raw memory.</summary>
        private GoShape Raw(ValueModel value)
        {
            switch (value.Kind)
            {
                case ValueKind.Primitive:
                    if (Primitives.TryGetValue(value.Primitive, out var p))
                        return new GoShape("prim")
                        {
                            Primitive = p.Name, Size = value.Size, MinSize = value.Size, Block = p.Block,
                            // Read in a run of fixed-size members: any bit pattern is valid.
                            Runnable = !p.Validated,
                        };
                    if (value.Fallback != null)
                        return Raw(value.Fallback);
                    throw new UnsupportedException($"{value.ClrName} ({value.Encoding}) has no Go form");
                case ValueKind.Enum:
                {
                    var model = value.Enum!;
                    if (!_enums.Contains(model))
                        _enums.Add(model);
                    if (!Primitives.TryGetValue(value.Primitive, out var u))
                        throw new UnsupportedException($"{model.FullName}'s underlying type has no Go form");
                    var inner = new GoShape("prim") { Primitive = u.Name, Size = value.Size, MinSize = value.Size };
                    return new GoShape("enum")
                    {
                        Enum = model, Inner = inner, Size = inner.Size, MinSize = inner.Size, Block = true, Runnable = true,
                    };
                }
                case ValueKind.RawStruct:
                {
                    var model = value.Struct!;
                    if (!_rawStructs.Contains(model))
                    {
                        _rawStructs.Add(model);
                        foreach (var field in model.Fields)
                            Raw(field.Value);
                    }

                    if (model.Empty)
                        return new GoShape("emptystruct") { Struct = model, Size = 1, MinSize = 1 };
                    return new GoShape("rawstruct") { Struct = model, Size = model.Size, MinSize = model.Size };
                }
                case ValueKind.RawNullable:
                {
                    var inner = Raw(value.Element!);
                    return new GoShape("rawnullable")
                    {
                        Inner = inner, FlagOffset = value.FlagOffset, ValueOffset = value.ValueOffset,
                        Size = value.Size, MinSize = value.Size,
                    };
                }
                case ValueKind.RawTuple:
                {
                    var items = value.Items.Select(Raw).ToList();
                    if (!value.IsPair)
                        _tuples.Add(items.Count);
                    return new GoShape("rawtuple")
                    {
                        Items = items, Offsets = value.Offsets.ToList(), Pair = value.IsPair, Size = value.Size,
                        MinSize = value.Size,
                    };
                }
                case ValueKind.Unsupported:
                    throw new UnsupportedException(value.Problem!.Replace("{lang}", "Go"));
                default:
                    throw new UnsupportedException($"{value.ClrName} ({value.Encoding}) has no Go form");
            }
        }

        // ---------------------------------------------------------------- names

        private void AssignNames(IEnumerable<object> types)
        {
            var all = types.Select(Clr).Distinct().ToList();
            var simple = all.ToLookup(t => Go(SimpleName(t)));
            foreach (var type in all)
            {
                string name = Go(SimpleName(type));
                if (simple[name].Count() > 1)
                    name = Go((type.Namespace ?? "").Replace('.', '_')) + name;
                // The generated code's own names (NinoCodec, NinoGrid, NinoTuple2...).
                if (name.StartsWith("Nino", StringComparison.Ordinal))
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

        /// <summary>
        /// <see cref="TypeNames.Simple"/>, but a generic definition by its name alone (<c>Box</c>, not <c>Box_T</c>):
        /// its parameters are the generated type's.
        /// </summary>
        internal static string SimpleName(Type type) => type.IsGenericTypeDefinition
            ? (type.IsNested ? SimpleName(type.DeclaringType!) + "_" : "") + TypeNames.WithoutArity(type.Name)
            : TypeNames.Simple(type);

        /// <summary>An exported Go name: the parts of the C# name, each with a capital (<c>Outer_Inner</c> → <c>OuterInner</c>).</summary>
        internal static string Go(string name) =>
            string.Concat(name.Split(Underscore, StringSplitOptions.RemoveEmptyEntries).Select(TypeNames.Pascal));

        private static readonly char[] Underscore = { '_' };

        /// <summary>
        /// The name of a closed generic type in a slot, as each language names one (<c>BoxInt32</c>,
        /// <c>BoxInt32Array</c> for <c>Box&lt;int[]&gt;</c>): an identifier in every one.
        /// </summary>
        internal static string ClosedName(Type type) => Go(new string(TypeNames.Simple(type).Replace("[", "Array_")
            .Where(c => char.IsAsciiLetterOrDigit(c) || c == '_').ToArray()));

        internal string Name(object type) =>
            _names.TryGetValue(Clr(type), out var name) ? name : Go(SimpleName(Clr(type)));

        /// <summary>The Go field name of a C# member: exported, and unique in its struct.</summary>
        internal static List<string> FieldNames(IEnumerable<string> names)
        {
            var used = new Dictionary<string, int>(StringComparer.Ordinal);
            var list = new List<string>();
            foreach (var csharp in names)
            {
                string name = Go(csharp);
                if (name.Length == 0 || !char.IsLetter(name[0]))
                    name = "F" + name;
                used[name] = used.TryGetValue(name, out int count) ? count + 1 : 0;
                if (used[name] != 0)
                    name += "_" + used[name].ToString(CultureInfo.InvariantCulture);
                list.Add(name);
            }

            return list;
        }

        /// <summary>A member's name in the extra struct tags.</summary>
        private string TagName(string name) => _options.TagNaming switch
        {
            TagNaming.Snake => TypeNames.Snake(name),
            TagNaming.Original => name,
            _ => TypeNames.Camel(name),
        };

        internal IReadOnlyList<string> ExtraTags => _options.Tags;

        /// <summary>A Go string literal.</summary>
        private static string GoString(string text)
        {
            var sb = new System.Text.StringBuilder("\"");
            foreach (char c in text)
            {
                if (c is '"' or '\\')
                    sb.Append('\\').Append(c);
                else if (c < ' ' || c > '~')
                    sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                else
                    sb.Append(c);
            }

            return sb.Append('"').ToString();
        }

        // ---------------------------------------------------------------- helpers

        /// <summary>
        /// The name of the function reading (<c>read</c>), writing (<c>write</c>), decoding (<c>decode</c>, from its
        /// bytes) or encoding (<c>encode</c>) values of <paramref name="shape"/>, shared by every value of the same
        /// shape; the template writes them all after the types.
        /// </summary>
        private string Helper(GoShape shape, string mode)
        {
            string key = mode + ":" + shape.Identity;
            if (_helperByKey.TryGetValue(key, out var helper))
                return helper.Name;
            helper = new GoHelper(mode + "V" + _helpers.Count.ToString(CultureInfo.InvariantCulture), mode, shape);
            _helperByKey[key] = helper;
            _helpers.Add(helper);
            return helper.Name;
        }
    }

    /// <summary>
    /// How a value is written in Go (<see cref="Kind"/>: <c>prim</c>, <c>enum</c>, <c>rawstruct</c>,
    /// <c>emptystruct</c>, <c>rawnullable</c>, <c>rawtuple</c>, <c>string</c>, <c>utf8</c>, <c>keystring</c>,
    /// <c>keyutf8</c>, <c>object</c>, <c>struct</c>, <c>class</c>, <c>slot</c>, <c>named</c>, <c>nullable</c>,
    /// <c>seq</c>, <c>map</c>, <c>tuple</c>, <c>grid</c>, <c>bigint</c>, <c>version</c>, <c>datetimeoffset</c>,
    /// <c>param</c>: a type parameter, <c>generic</c>: a generic type with its arguments in <see cref="Items"/>).
    /// </summary>
    public sealed class GoShape
    {
        internal GoShape(string kind) => Kind = kind;

        public string Kind { get; }

        /// <summary>The reader's and writer's method of a primitive (<c>I32</c>, <c>Half</c>, <c>Decimal</c>...).</summary>
        public string? Primitive { get; internal set; }

        /// <summary>The bytes of a raw value, else -1.</summary>
        public int Size { get; internal set; } = -1;

        /// <summary>The fewest bytes a value takes: a collection's count is checked against it.</summary>
        public int MinSize { get; internal set; }

        /// <summary>An expression for the fewest bytes, where the arguments of a generic type decide them.</summary>
        internal string? MinExpression { get; set; }

        /// <summary><see cref="MinSize"/>, or the expression giving it.</summary>
        public string Min => MinExpression ?? MinSize.ToString(CultureInfo.InvariantCulture);

        /// <summary>The name of a type parameter (<c>param</c>).</summary>
        public string? Parameter { get; internal set; }

        /// <summary>
        /// The codec (<c>NinoArg</c>) it is read and written through: a type parameter's (<c>c.p0</c>), one its
        /// generic type's codec built (<c>c.d0</c>, <c>c.self</c>), or a closed generic type's (<c>codecV0</c>).
        /// </summary>
        public string? Field { get; internal set; }

        /// <summary>Whether arrays of it are one block (<c>nino.ReadPrimitives</c>).</summary>
        public bool Block { get; internal set; }

        /// <summary>Whether it is read in a run of fixed-size members: every bit pattern is a valid value.</summary>
        public bool Runnable { get; internal set; }

        public EnumModel? Enum { get; internal set; }

        public RawStructModel? Struct { get; internal set; }

        public TypeModel? Type { get; internal set; }

        public GoNamed? Named { get; internal set; }

        public GoShape? Inner { get; internal set; }

        public GoShape? Element { get; internal set; }

        public GoShape? Key { get; internal set; }

        public GoShape? Value { get; internal set; }

        public List<GoShape> Items { get; internal set; } = new();

        /// <summary>
        /// The slot codecs a generic type's codec is given (<see cref="ValueModel.SlotArguments"/>), after the codecs
        /// of its type arguments.
        /// </summary>
        public List<GoShape> Slots { get; internal set; } = new();

        /// <summary>The slot of a closed generic type written behind a type id (<c>genslot</c>), with its functions.</summary>
        public GoSlot? Slot { get; internal set; }

        public List<int> Offsets { get; internal set; } = new();

        /// <summary>A <c>KeyValuePair</c>: a <c>nino.Entry</c>.</summary>
        public bool Pair { get; internal set; }

        /// <summary>
        /// A sequence's way: <c>bytes</c>, <c>block</c>, <c>bools</c>, <c>fixed</c> (raw values at their stride),
        /// <c>unique</c> (a set of comparable values), <c>stringset</c>, <c>slice</c>; a map's: <c>unique</c>
        /// (repeated keys fail) or <c>entries</c>.
        /// </summary>
        public string? Mode { get; internal set; }

        public int FlagOffset { get; internal set; }

        public int ValueOffset { get; internal set; }

        public int Rank { get; internal set; }

        internal string Identity => _key ??= Kind + "(" + string.Join(",", Parts()) + ")";

        private string? _key;

        private IEnumerable<string> Parts()
        {
            yield return Size.ToString(CultureInfo.InvariantCulture);
            yield return Primitive ?? "";
            yield return Parameter ?? "";
            yield return Enum?.FullName ?? "";
            yield return Struct?.FullName ?? "";
            yield return Type?.FullName ?? "";
            yield return Named?.FullName ?? "";
            yield return Mode ?? "";
            yield return Pair ? "pair" : "";
            yield return FlagOffset + "/" + ValueOffset + "/" + Rank;
            yield return string.Join("/", Offsets);
            yield return Slot?.Name ?? "";
            yield return Field ?? "";
            foreach (var child in new[] { Inner, Element, Key, Value }.Concat(Items).Concat(Slots))
                yield return child?.Identity ?? "-";
        }
    }

    /// <summary>
    /// The codec of a generic definition (<c>boxCodec[T]</c>): the codecs of its parameters (<c>p0</c>...), and those it
    /// builds once from them for the values holding them (<c>d0</c>...), in the order they are built.
    /// </summary>
    public sealed class GoGeneric
    {
        internal GoGeneric(TypeModel type) => Type = type;

        public TypeModel Type { get; }

        public List<GoDerived> Derived { get; } = new();

        internal Dictionary<string, GoDerived> ByKey { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// A codec a generic codec builds: of a generic type (<see cref="Generic"/>, its constructor called with the
    /// codecs of its arguments) or of a collection, tuple or nullable value (its own read and write methods).
    /// </summary>
    public sealed class GoDerived
    {
        internal GoDerived(string name, GoShape shape, bool generic)
        {
            Name = name;
            Shape = shape;
            Generic = generic;
        }

        public string Name { get; }

        public GoShape Shape { get; }

        public bool Generic { get; }
    }

    /// <summary>The codec of a closed generic type, a package variable set in <c>init</c>.</summary>
    public sealed class GoClosed
    {
        internal GoClosed(string name, GoShape shape)
        {
            Name = name;
            Shape = shape;
        }

        public string Name { get; }

        public GoShape Shape { get; }
    }

    /// <summary>
    /// A closed generic type in a polymorphic slot: its codec (<c>typeVn</c>, a <c>*boxCodec[int32]</c>) is built in
    /// <c>init</c>, and the slots' dispatches read and write it behind the id of its closed .NET name.
    /// </summary>
    public sealed class GoSlotClosed
    {
        internal GoSlotClosed(string name, ClosedTypeModel closed, GoShape shape)
        {
            Name = name;
            Closed = closed;
            Shape = shape;
        }

        public string Name { get; }

        public ClosedTypeModel Closed { get; }

        public GoShape Shape { get; }

        public int Id => Closed.TypeId;

        public bool IsStruct => Closed.IsValueType;

        public string RuntimeName => Closed.RuntimeName;
    }

    /// <summary>
    /// The slot of a closed generic type written behind a type id (<c>Shelf&lt;int&gt;</c>): its functions
    /// (<c>readSlot0</c>, <c>writeSlot0</c>) switch over the ids of the types and closed types assignable to it.
    /// </summary>
    public sealed class GoSlot
    {
        private readonly GoTarget _target;

        internal GoSlot(GoTarget target, string name, SlotModel model, GoShape shape)
        {
            _target = target;
            Name = name;
            Model = model;
            Shape = shape;
        }

        public string Name { get; }

        public SlotModel Model { get; }

        /// <summary>The slot's type, its generic interface with the arguments.</summary>
        public GoShape Shape { get; }

        public string RuntimeName => Model.RuntimeName;

        public List<GoType> Allowed => _target.SlotAllowed(this);

        public List<GoSlotClosed> AllowedClosed => _target.SlotAllowedClosed(this);
    }

    /// <summary>A generic slot a type sits in: the marker method of its interface, with the arguments.</summary>
    public sealed class GoMarker
    {
        internal GoMarker(string slotName, List<GoShape> items)
        {
            SlotName = slotName;
            Items = items;
        }

        /// <summary>The generic interface (<c>Shelf</c>, <c>CrateType</c>): the method is <c>is</c> and its name.</summary>
        public string SlotName { get; }

        public List<GoShape> Items { get; }
    }

    public sealed class GoHelper
    {
        public GoHelper(string name, string mode, GoShape shape)
        {
            Name = name;
            Mode = mode;
            Shape = shape;
        }

        public string Name { get; }

        /// <summary><c>read</c>, <c>write</c>, <c>decode</c> or <c>encode</c>.</summary>
        public string Mode { get; }

        public GoShape Shape { get; }
    }

    /// <summary>A collection of itself (<c>class Tree : List&lt;Tree&gt;</c>): a named Go slice type.</summary>
    public sealed class GoNamed
    {
        internal GoNamed(string name, string fullName)
        {
            Name = name;
            FullName = fullName;
        }

        public string Name { get; }

        public string FullName { get; }

        public GoShape Value { get; internal set; } = null!;
    }

    public sealed class GoEnum
    {
        internal GoEnum(string name, EnumModel model, string underlying)
        {
            Name = name;
            Model = model;
            Underlying = underlying;
            Constants = model.Constants.Select(c => new GoConstant(name + GoTarget.Go(c.Name), c.Value)).ToList();
        }

        public string Name { get; }

        public EnumModel Model { get; }

        /// <summary>The reader's method of its underlying integer (<c>I8</c>, <c>U64</c>...).</summary>
        public string Underlying { get; }

        public List<GoConstant> Constants { get; }
    }

    public sealed class GoConstant
    {
        internal GoConstant(string name, string value)
        {
            Name = name;
            Value = value;
        }

        public string Name { get; }

        public string Value { get; }
    }

    public sealed class GoField
    {
        internal GoField(string name, string csharpName, int offset, GoShape shape)
        {
            Name = name;
            Csharp = csharpName;
            Offset = offset;
            Shape = shape;
        }

        public string Name { get; }

        /// <summary>The C# name.</summary>
        public string Csharp { get; }

        public int Offset { get; }

        public GoShape Shape { get; }
    }

    public sealed class GoRawStruct
    {
        internal GoRawStruct(GoTarget target, RawStructModel model)
        {
            Name = target.Name(model);
            Model = model;
            var names = GoTarget.FieldNames(model.Fields.Select(f => f.Name));
            Fields = model.Fields.Select((f, i) =>
                new GoField(names[i], f.Name, f.Offset, target.Shape(f.Value, false))).ToList();
        }

        public string Name { get; }

        public RawStructModel Model { get; }

        public List<GoField> Fields { get; }
    }

    public sealed class GoMember
    {
        internal GoMember(MemberModel model, string name, GoShape shape)
        {
            Model = model;
            Name = name;
            Shape = shape;
        }

        public MemberModel Model { get; }

        public string Name { get; }

        /// <summary>The C# name.</summary>
        public string Csharp => Model.Name;

        public GoShape Shape { get; }
    }

    /// <summary>Fixed-size members next to one another, read with one bounds check, or one other member.</summary>
    public sealed class GoRun
    {
        public List<GoRunItem> Items { get; } = new();

        public int Size { get; internal set; }
    }

    public sealed class GoRunItem
    {
        internal GoRunItem(GoMember member, int offset)
        {
            Member = member;
            Offset = offset;
        }

        public GoMember Member { get; }

        public int Offset { get; }
    }

    /// <summary>A <c>[NinoType]</c> as the template writes it.</summary>
    public sealed class GoType
    {
        private readonly GoTarget _target;

        internal GoType(GoTarget target, TypeModel model)
        {
            _target = target;
            Model = model;
            Name = target.Name(model);
        }

        public TypeModel Model { get; }

        public string Name { get; }

        /// <summary><c>raw</c>, <c>struct</c>, <c>class</c> (a presence byte) or <c>typeid</c>.</summary>
        public string Kind => Model.Raw ? "raw" : Model.IsValueType ? "struct" : Model.Header == HeaderKind.TypeId ? "typeid" : "class";

        /// <summary>An abstract class or interface: a Go interface only.</summary>
        public bool IsAbstract => !Model.IsConcrete;

        public bool IsStruct => Model.IsValueType;

        /// <summary>The id it is written behind (its name's hash for a closed generic type).</summary>
        public int Id => GoTarget.IdOf(Model);

        /// <summary>Whether the type's id is published (a closed generic type's is 0).</summary>
        public bool PublishedId => Model.TypeId != 0;

        public string Format => Model.Format switch
        {
            BodyFormat.Keyed => "keyed",
            BodyFormat.VersionTolerant => "tolerant",
            _ => "positional",
        };

        public bool Deferrable => Model.Deferrable ?? true;

        /// <summary>
        /// A struct whose nesting is counted: one that can hold itself through no class body, or a generic struct
        /// (whose .NET formatter counts every one).
        /// </summary>
        public bool Counted => Model.IsValueType && (Model.IsGenericDefinition || HoldsItself());

        /// <summary>The fewest bytes of a generic struct's body: its members', its arguments' as its codec has them.</summary>
        public string MinBody => Model.Format != BodyFormat.Positional ? "4"
            : Members.Count == 0 ? "0"
            : string.Join(" + ", Members.Select(m => m.Shape.Min));

        public List<GoMember> Members => _members ??= BuildMembers();

        private List<GoMember>? _members;

        private List<GoMember> BuildMembers()
        {
            var names = FieldNames();
            int skip = Model.Elements != null ? 1 : 0;
            return Model.Members.Select((m, i) => new GoMember(m, names[i + skip], _target.ShapeIn(Model, m.Value))).ToList();
        }

        /// <summary>The Go field names: <c>Elements</c> first for a <c>[NinoType]</c> collection, then the members'.</summary>
        private List<string> FieldNames()
        {
            var names = Model.Members.Select(m => m.Name);
            return GoTarget.FieldNames(Model.Elements != null ? names.Prepend("Elements") : names);
        }

        /// <summary>The elements of a <c>[NinoType]</c> collection (a field before the members), or null.</summary>
        public GoMember? ElementsMember => Model.Elements == null ? null
            : _elements ??= new GoMember(new MemberModel("$elements", "", Model.Elements), FieldNames()[0],
                _target.ShapeIn(Model, Model.Elements));

        private GoMember? _elements;

        /// <summary>Its fields: the elements (if a collection), then the members.</summary>
        public List<GoMember> Fields => ElementsMember == null ? Members : new[] { ElementsMember }.Concat(Members).ToList();

        /// <summary>A class written behind reference headers (trackReferences).</summary>
        public bool Tracked => Model.TrackReferences;

        /// <summary>The generic slots it sits in: a marker method each.</summary>
        public List<GoMarker> Markers => _markers ??= Model.SlotBases.Select(b => _target.Markers(Model, b)).ToList();

        private List<GoMarker>? _markers;

        /// <summary>The generic interface of a generic definition written behind a type id (<c>Shelf</c>, <c>CrateType</c>).</summary>
        public string GenericSlotName => _target.SlotName(Model);

        /// <summary>The slots its codec is given (<see cref="TypeModel.SlotParameters"/>), as values of its own parameters.</summary>
        public List<GoShape> SlotParameters => Model.SlotParameters.Select(p => _target.ShapeIn(Model, p)).ToList();

        /// <summary>The type parameters of a generic definition (empty otherwise).</summary>
        public IReadOnlyList<string> Parameters => Model.TypeParameters;

        /// <summary><c>[T any, U any]</c> after the name of a generic type's declaration, else empty.</summary>
        public string TypeParams => Parameters.Count == 0 ? "" : "[" + string.Join(", ", Parameters.Select(p => p + " any")) + "]";

        /// <summary><c>[T, U]</c> after the name of a generic type where it is used, else empty.</summary>
        public string TypeArgs => Parameters.Count == 0 ? "" : "[" + string.Join(", ", Parameters) + "]";

        /// <summary>The codecs a generic codec builds, in order (members shaped first).</summary>
        public List<GoDerived> Derived
        {
            get
            {
                _ = Members;
                return _target.GenericOf(Model).Derived;
            }
        }

        public GoShape? RawShape => Model.Raw ? _target.Shape(Model.RawValue!, false) : null;

        /// <summary>The members in runs: runnable fixed-size members next to one another together.</summary>
        public List<GoRun> Runs
        {
            get
            {
                var runs = new List<GoRun>();
                foreach (var member in Members)
                {
                    bool runnable = member.Shape.Runnable;
                    if (!(runnable && runs.Count != 0 && runs[^1].Items[^1].Member.Shape.Runnable))
                        runs.Add(new GoRun());
                    var run = runs[^1];
                    run.Items.Add(new GoRunItem(member, run.Size));
                    if (runnable)
                        run.Size += member.Shape.Size;
                }

                return runs;
            }
        }

        /// <summary>The words of bits a keyed body's reader marks its members in (64 a word).</summary>
        public int SeenWords => (Members.Count + 63) / 64;

        /// <summary>Whether its slot is a Go interface; else a <c>*T</c> (a class without subtypes).</summary>
        public bool InterfaceSlot => Model.Header == HeaderKind.TypeId && _target.IsInterfaceSlot(Model);

        /// <summary>The Go type of a slot declared as this type.</summary>
        public string SlotName => InterfaceSlot ? _target.SlotName(Model) : "*" + Name;

        /// <summary>The name of a slot's read and write functions (<c>ReadAnimal</c>).</summary>
        public string SlotFunctions => InterfaceSlot ? _target.SlotName(Model) : Name;

        /// <summary>The concrete types a slot declared as this type allows.</summary>
        public List<GoType> Allowed => _target.Allowed(Model).Select(t => new GoType(_target, t)).ToList();

        /// <summary>The closed generic types a slot declared as this type allows.</summary>
        public List<GoSlotClosed> AllowedClosed => _target.AllowedClosed(Model);

        /// <summary>A generic definition's closed types that sit in a slot (their ids, in its <c>NinoTypeID</c>).</summary>
        public List<GoSlotClosed> Closed => _target.ClosedOf(Model);

        /// <summary>The interface slots this type can sit in (their marker methods).</summary>
        public List<GoType> Slots => _target.SlotsOf(Model).Select(t => new GoType(_target, t)).ToList();

        public string Layout => Model.LayoutText;

        public string RuntimeName => Model.RuntimeName;

        private bool HoldsItself()
        {
            var seen = new HashSet<TypeModel>();
            var pending = new Stack<TypeModel>();
            pending.Push(Model);
            while (pending.Count != 0)
            {
                var next = pending.Pop();
                foreach (var reached in next.Members.SelectMany(m => m.Value.Walk()).SelectMany(_target.StructsIn))
                {
                    if (reached == Model)
                        return true;
                    if (seen.Add(reached))
                        pending.Push(reached);
                }
            }

            return false;
        }
    }
}
