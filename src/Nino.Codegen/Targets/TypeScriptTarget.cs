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

        /// <summary>
        /// Whether arrays and lists of fixed-size numbers are typed arrays (<c>Int32Array</c>, <c>Float64Array</c>...;
        /// <c>--numeric-arrays typed</c>) rather than <c>number[]</c> and <c>bigint[]</c> (the default).
        /// </summary>
        public bool TypedArrays { get; set; }
    }

    /// <summary>
    /// TypeScript: a class per type, an <c>as const</c> object per enum and a <c>NinoCodec</c> per type, which read and
    /// write exactly the bytes the layout describes (<c>Templates/typescript/types.sbn</c>). A type whose layout it
    /// cannot express (encodings without a TypeScript form, reference tracking of a generic type) is left out with a
    /// warning, and so is every type holding one.
    /// </summary>
    public sealed class TypeScriptTarget : CodegenTarget
    {
        private readonly TypeScriptOptions _options;

        // Met while shaping the members of the supported types.
        private readonly List<EnumModel> _enums = new();
        private readonly List<(RawStructModel Struct, TsShape Shape)> _rawStructs = new();

        // The functions shared by every value of one shape, in the order they were first used.
        private readonly Dictionary<string, TsHelper> _helperByKey = new(StringComparer.Ordinal);
        private readonly List<TsHelper> _helpers = new();

        // Generic types: the codecs each one's codec builds, and those of the closed generic types the module builds.
        private readonly Dictionary<TypeModel, List<TsDerived>> _derived = new();
        private readonly Dictionary<string, TsDerived> _closedByKey = new(StringComparer.Ordinal);
        private readonly List<TsDerived> _closed = new();
        private readonly HashSet<ClosedTypeModel> _closedSupported = new();
        private readonly List<TsSlotClosed> _slotClosed = new();
        // The slots of closed generic types written behind type ids that the values hold (slot$0...).
        private readonly Dictionary<SlotModel, TsSlot> _slotByModel = new();
        private readonly List<TsSlot> _slots = new();
        private TypeModel? _context;

        public TypeScriptTarget(CodegenModel model, TypeScriptOptions options) : base(model) => _options = options;

        protected override string Language => "TypeScript";

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
            ResolveClosed();
            var layouts = _model.Types.Where(_supported.Contains).ToList();
            AssignNames(layouts.Select(t => (object)t).Concat(_enums).Concat(_rawStructs.Select(r => r.Struct)));
            NameClosed(layouts);

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
            var codecs = layouts.Where(t => !t.IsGenericDefinition).OrderBy(Name, StringComparer.Ordinal)
                .Select(t => new TsClass(this, t)).ToList();
            // Abstract generic classes and generic interfaces have no codec: their closed types are slots.
            var generics = layouts.Where(t => t.IsGenericDefinition && t.IsConcrete).OrderBy(Name, StringComparer.Ordinal)
                .Select(t => new TsClass(this, t)).ToList();
            // Every member shaped, so every codec the module and the generic codecs build is known.
            foreach (var c in classes.Concat(generics))
                _ = c.Derived;
            foreach (var slot in _slotClosed)
                slot.Shape = ClosedShape(slot.Closed);
            // The slots' closed types are named; a slot's own codec is shaped once all are.
            foreach (var slot in _slots)
            {
                slot.Closed = _slotClosed.Where(c => slot.Model.ClosedAssignable.Contains(c.Closed)).ToList();
                slot.Assignable = slot.Model.Assignable.Where(_supported.Contains).OrderBy(Name, StringComparer.Ordinal)
                    .Select(t => new TsClass(this, t)).ToList();
            }
            // A closed codec of a type a slot holds builds its subclass, so the values it reads can sit in the slot.
            foreach (var closed in _closed)
                closed.Subclass = _slotClosed.FirstOrDefault(c => c.Shape!.Identity == closed.Key)?.Name;

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
            renderer.Set("generics", generics);
            renderer.Set("closed", _closed);
            renderer.Set("slot_closed", _slotClosed);
            renderer.Set("slots", _slots);
            renderer.Function("name", new Func<object, string>(Name));
            renderer.Function("helper", new Func<TsShape, string, string>(Helper));
            renderer.Function("helper_count", new Func<int>(() => _helpers.Count));
            renderer.Function("helper_at", new Func<int, TsHelper>(i => _helpers[i]));
            return renderer.Render("types.sbn").TrimEnd('\n') + "\n";
        }

        // ---------------------------------------------------------------- which types

        // A generic type holding itself with other arguments is fine (no GenericProblem): its codecs build those of
        // the types it holds when the data first needs them.

        protected override void Reset()
        {
            _enums.Clear();
            _rawStructs.Clear();
            ResetCodecs();
        }

        /// <summary>The enums and raw structs met while checking stay; the codecs and slots are shaped again.</summary>
        protected override void ResetResolved()
        {
            ResetCodecs();
            _structOpens.Clear();
        }

        private void ResetCodecs()
        {
            _derived.Clear();
            _closedByKey.Clear();
            _closed.Clear();
            _slotByModel.Clear();
            _slots.Clear();
        }

        /// <summary>
        /// The closed generic types the assembly names (<see cref="CodegenModel.ClosedTypes"/>) that TypeScript can
        /// write behind their type ids, in the polymorphic slots (and the registry) they are assignable to.
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
                    WarnClosed(closed, e.Message);
                }
            }

            ResetResolved();
        }

        /// <summary>
        /// A closed generic type in a slot: the shapes of its arguments and of the slots its codecs need (its codecs
        /// built in the module).
        /// </summary>
        private TsShape ClosedShape(ClosedTypeModel closed) => new("generic")
        {
            Type = closed.Definition, Items = closed.Value.Arguments.Select(Shape).ToList(), Opens = true,
            Header = closed.Definition.Header == HeaderKind.None ? "none" : "null",
            Slots = closed.Value.SlotArguments.Select(Shape).ToList(),
        };

        /// <summary>
        /// Names the closed types a slot can hold (an <c>object</c> slot any, a typed slot those assignable to its
        /// type): a subclass each (<c>BoxInt32</c>), as TypeScript erases type arguments and a writer tells types
        /// apart by their constructors.
        /// </summary>
        private void NameClosed(List<TypeModel> layouts)
        {
            bool usesObject = layouts.Any(t => !t.Raw && t.Members.Any(m => m.Value.Walk().Any(v => v.Kind == ValueKind.Object)));
            var used = new HashSet<string>(Names, StringComparer.Ordinal);
            // The slots of closed generic types the supported types hold, through the codecs they build too.
            var slots = UsedSlots(layouts);
            foreach (var closed in _model.ClosedTypes.Where(c => _closedSupported.Contains(c) && (usesObject ||
                         layouts.Any(t => t.Header == HeaderKind.TypeId && t.ClosedAssignable.Contains(c)) ||
                         slots.Any(slot => slot.ClosedAssignable.Contains(c)))))
            {
                string name = TypeNames.Closed(closed.Clr);
                while (!used.Add(name))
                    name += "_";
                _slotClosed.Add(new TsSlotClosed(name, "closed$" + _slotClosed.Count, closed));
            }
        }

        /// <summary>
        /// The slots of closed generic types (<see cref="CodegenModel.Slots"/>) a value of the supported types may be
        /// read through: those their members hold, and those the codecs of the closed types in them need, until no
        /// more are added.
        /// </summary>
        private HashSet<SlotModel> UsedSlots(List<TypeModel> layouts)
        {
            var slots = new HashSet<SlotModel>();
            var values = new Queue<ValueModel>(layouts.Where(t => !t.Raw).SelectMany(CodegenModel.OwnValues));
            var closedSeen = new HashSet<ClosedTypeModel>();
            while (values.Count != 0)
            {
                foreach (var value in values.Dequeue().Walk())
                {
                    foreach (var argument in value.SlotArguments)
                        values.Enqueue(argument);
                    if (value.Slot is not { } slot || !_supported.Contains(slot.Definition) || !slots.Add(slot))
                        continue;
                    foreach (var closed in slot.ClosedAssignable.Where(c => _closedSupported.Contains(c) && closedSeen.Add(c)))
                        values.Enqueue(closed.Value);
                }
            }

            return slots;
        }

        /// <summary>The closed generic types a slot declared as <paramref name="type"/> allows.</summary>
        internal List<TsSlotClosed> AllowedClosed(TypeModel type) =>
            _slotClosed.Where(c => type.ClosedAssignable.Contains(c.Closed)).ToList();

        /// <summary>The closed types of a generic definition that sit in a slot.</summary>
        internal List<TsSlotClosed> ClosedOf(TypeModel definition) =>
            _slotClosed.Where(c => c.Closed.Definition == definition).ToList();

        protected override void CheckType(TypeModel type)
        {
            if (type.Raw)
                Shape(type.RawValue!);
            else
            {
                if (type.Elements != null)
                    ShapeIn(type, type.Elements);
                foreach (var member in type.Members)
                    ShapeIn(type, member.Value);
                if (type.BaseValue != null)
                    BaseShape(type, type.BaseValue);
            }
        }

        /// <summary>The concrete types of <paramref name="types"/> written behind a type id.</summary>
        private IEnumerable<TypeModel> Registered(IEnumerable<TypeModel> types) =>
            types.Where(t => t.TypeId != 0 && t.IsConcrete).OrderBy(Name, StringComparer.Ordinal);

        // ---------------------------------------------------------------- shapes

        /// <summary>
        /// The shape of a member of <paramref name="owner"/>: in a generic definition, the values holding its type
        /// parameters are read with the codecs of its arguments (<c>c</c>, the type's <c>X$Codecs</c>).
        /// </summary>
        internal TsShape ShapeIn(TypeModel owner, ValueModel value)
        {
            var outer = _context;
            _context = owner.IsGenericDefinition ? owner : null;
            try
            {
                return Shape(value);
            }
            finally
            {
                _context = outer;
            }
        }

        /// <summary>The codecs a generic type's codec builds (<c>c.d0</c>...), in the order they are built.</summary>
        internal List<TsDerived> DerivedOf(TypeModel type)
        {
            if (!_derived.TryGetValue(type, out var list))
                _derived[type] = list = new List<TsDerived>();
            return list;
        }

        /// <summary>A generic type with its arguments, read and written through a codec built once.</summary>
        private TsShape Generic(ValueModel value)
        {
            var definition = value.Type!;
            if (!_supported.Contains(definition))
                throw new UnsupportedException($"{definition.FullName} is not written to TypeScript");
            if (definition.Header == HeaderKind.TypeId)
                return SlotShape(value);
            var shape = new TsShape("generic")
            {
                Type = definition, Items = value.Arguments.Select(Shape).ToList(), Opens = true,
                Header = definition.Header == HeaderKind.None ? "none" : "null",
                Slots = value.SlotArguments.Select(Shape).ToList(),
            };
            if (_context != null && value.IsSelf(_context))
            {
                shape.Field = "c.self";
                return shape;
            }

            string key = shape.Identity;
            if (value.IsOpen && _context != null)
            {
                var list = DerivedOf(_context);
                var derived = list.FirstOrDefault(d => d.Key == key);
                if (derived == null)
                    list.Add(derived = new TsDerived("d" + list.Count, key, shape));
                shape.Field = "c." + derived.Name;
                return shape;
            }

            if (!_closedByKey.TryGetValue(key, out var closed))
            {
                closed = new TsDerived("codec$" + _closed.Count, key, shape);
                _closedByKey[key] = closed;
                _closed.Add(closed);
            }

            shape.Field = closed.Name;
            return shape;
        }

        /// <summary>
        /// A generic type written behind a type id (<c>Shelf&lt;int&gt;</c>): closed, the slot's codec the module builds
        /// (<c>slot$0</c>); in a generic definition, with its parameters, the slot its codec was given (<c>c.s0</c>).
        /// </summary>
        private TsShape SlotShape(ValueModel value)
        {
            var shape = new TsShape("generic")
            {
                Type = value.Type, Items = value.Arguments.Select(Shape).ToList(), Opens = true, Header = "typeid",
            };
            if (value.Slot is { } model)
            {
                if (!_slotByModel.TryGetValue(model, out var slot))
                {
                    slot = new TsSlot("slot$" + _slots.Count, model, shape);
                    _slotByModel[model] = slot;
                    _slots.Add(slot);
                    // Its closed types are shaped too, so a slot holding one that cannot be written is found here.
                    foreach (var closed in model.ClosedAssignable.Where(c => _closedSupported.Contains(c)))
                        ClosedShape(closed);
                }

                shape.Field = slot.Name;
                return shape;
            }

            int index = _context?.SlotParameters.FindIndex(p => p.TypeKey == value.TypeKey) ?? -1;
            if (index < 0)
                throw new UnsupportedException($"{value.ClrName} is a slot outside its generic type");
            shape.Field = "c.s" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
            shape.Context = _context;
            return shape;
        }

        /// <summary>A base class or interface with the arguments a type gives it: its TypeScript type.</summary>
        internal TsShape BaseShape(TypeModel owner, ValueModel value)
        {
            var outer = _context;
            _context = owner.IsGenericDefinition ? owner : null;
            try
            {
                return new TsShape("generic") { Type = value.Type, Items = value.Arguments.Select(Shape).ToList() };
            }
            finally
            {
                _context = outer;
            }
        }

        /// <summary>A value holding a type parameter of the type being written: its code takes that type's codecs.</summary>
        private TsShape InContext(ValueModel value, TsShape shape)
        {
            if (_context != null && value.IsOpen)
                shape.Context = _context;
            return shape;
        }

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
                case ValueKind.Parameter:
                    if (_context == null)
                        throw new UnsupportedException($"{value.ParameterName} is a type parameter outside its type");
                    return new TsShape("param")
                    {
                        Parameter = value.ParameterName, Field = "c.p" + value.Parameter, Opens = true,
                    };
                case ValueKind.Generic:
                    return Generic(value);
                case ValueKind.Named:
                    throw new UnsupportedException($"{value.ClrName} is not written to TypeScript");
                case ValueKind.Nullable:
                    return InContext(value, Nullable(Shape(value.Element!)));
                case ValueKind.Sequence:
                {
                    var element = Shape(value.Element!);
                    bool set = value.Collection is CollectionKind.HashSet or CollectionKind.SortedSet ||
                               CodegenModel.Implements(value.Clr, "System.Collections.Generic.ISet`1") != null;
                    if (value.IsByteArray)
                        return new TsShape("bytes");
                    string? array = null;
                    if (_options.TypedArrays && !set && element.Kind == "prim" &&
                        NumberArrays.TryGetValue(element.Primitive!, out array) && array == "Uint8Array")
                        return new TsShape("bytes");
                    // With --numeric-arrays typed, other fixed-size numbers are a typed array, read and written one by
                    // one while it fits V8's heap (64 bytes), else copied in one block.
                    return InContext(value, Holding(new TsShape("seq")
                    {
                        Element = element, Set = set, ArrayType = array,
                        InlineLength = array == null ? 0 : 64 / element.Size,
                    }, element));
                }
                case ValueKind.Map:
                {
                    var (key, mapValue) = (Shape(value.Key!), Shape(value.Value!));
                    return InContext(value, Holding(new TsShape("map") { Key = key, Value = mapValue }, key, mapValue));
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

                    return InContext(value, Holding(Tuple(items, offsets, items.All(i => i.Fixed) ? size : -1,
                        items.Any(i => i.Zeroed)), items.ToArray()));
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
                // A tracked sealed class: its reference header instead of the presence byte.
                HeaderKind.NullFlag when type.TrackReferences => new TsShape("type")
                {
                    Type = type, Header = "ref", Opens = body, Reaches = reaches,
                },
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

        /// <summary>
        /// The typed array an array or list (not a set) of a primitive is held in: the fixed-size numbers, whose bytes
        /// on the wire are those of the typed array on a little-endian host. <c>bool</c> (validated 0 or 1, read as
        /// <c>boolean</c>), <c>char</c> (a string), <c>decimal</c>, <c>TimeSpan</c> and enums (their constants' type)
        /// stay arrays.
        /// </summary>
        private static readonly Dictionary<string, string> NumberArrays = new()
        {
            ["i8"] = "Int8Array", ["u8"] = "Uint8Array", ["i16"] = "Int16Array", ["u16"] = "Uint16Array",
            ["i32"] = "Int32Array", ["u32"] = "Uint32Array", ["i64"] = "BigInt64Array", ["u64"] = "BigUint64Array",
            ["f32"] = "Float32Array", ["f64"] = "Float64Array",
        };

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
            opens = _supported.Contains(type) && Nests(type);
            _structOpens[type] = opens;
            return opens;
        }

        /// <summary>
        /// Whether a class body may hold other class bodies (<c>NinoBody.Nests</c>): it counts the depth for them, and
        /// at depth 0, ends with the bodies deferred below it. Saying so of one that does not changes no byte.
        /// </summary>
        internal bool Nests(TypeModel type) =>
            type.Members.Select(m => m.Value).Append(type.Elements).Any(v => v != null && Opens(ShapeIn(type, v)));

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
                foreach (var reached in next.Members.Select(m => m.Value).Append(next.Elements)
                             .Where(v => v != null).SelectMany(v => ShapeIn(next, v!).Reaches))
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

        /// <summary>Types are named as in C# (nested and generic types joined with _).</summary>
        protected override string BaseName(Type type) => TypeNames.Definition(type);

        /// <summary>With their namespace when two share a name.</summary>
        protected override string Qualified(Type type, string name) =>
            (type.Namespace ?? "").Replace('.', '_') + "_" + name;

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
                list.Add(new TsMember(member, name, ShapeIn(type, member.Value)));
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

            helper = new TsHelper((mode == "read" ? "read$" : mode == "readinto" ? "readInto$" : "write$") + _helpers.Count, mode,
                shape);
            if (shape.Context is { } context)
            {
                // The code of a value holding a type parameter takes the codecs of the type's arguments.
                helper.TypeParams = TypeParams(context);
                helper.Codecs = ", c: " + Name(context) + "$Codecs" + TypeParams(context);
            }
            _helperByKey[key] = helper;
            _helpers.Add(helper);
            return helper.Name;
        }

        /// <summary><c>&lt;T, U&gt;</c> of a generic definition, else empty.</summary>
        internal static string TypeParams(TypeModel type) =>
            type.TypeParameters.Count == 0 ? "" : "<" + string.Join(", ", type.TypeParameters) + ">";

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
    /// <c>nullable</c>, <c>bytes</c>, <c>seq</c>, <c>map</c>, <c>type</c>, <c>param</c>: a type parameter,
    /// <c>generic</c>: a generic type with its arguments in <see cref="Items"/>): the template turns it into a type, a
    /// default, and code reading and writing it, alone or at an offset.
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

        /// <summary>The name of a type parameter (<c>param</c>).</summary>
        public string? Parameter { get; internal set; }

        /// <summary>
        /// The codec it is read and written through: a type parameter's (<c>c.p0</c>), one the generic type's codec
        /// built (<c>c.d0</c>, <c>c.self</c>), or a closed generic type's (<c>codec$0</c>).
        /// </summary>
        public string? Field { get; internal set; }

        /// <summary>The generic definition whose codecs (<c>c</c>) the code of a value holding its parameters takes.</summary>
        public TypeModel? Context { get; internal set; }

        /// <summary><c>, c</c> after the arguments of the code of a value holding type parameters, else empty.</summary>
        public string CodecsArgument => Context == null ? "" : ", c";

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

        /// <summary>The typed array a sequence of fixed-size numbers is (<c>Int32Array</c>...), else null.</summary>
        public string? ArrayType { get; internal set; }

        /// <summary>
        /// The most elements of a typed array read and written one by one (those that fit in 64 bytes, which V8
        /// keeps on its heap); longer ones are copied in one block (<c>NinoReader.numbers</c>, <c>NinoWriter.numbers</c>).
        /// </summary>
        public int InlineLength { get; internal set; }

        public TsShape? Key { get; internal set; }

        public TsShape? Value { get; internal set; }

        public List<TsShape> Items { get; internal set; } = new();

        /// <summary>The slots a generic type's codec is built with, after the codecs of its arguments.</summary>
        public List<TsShape> Slots { get; internal set; } = new();

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
            yield return Parameter ?? "";
            yield return Context?.FullName ?? "";
            yield return Enum?.FullName ?? "";
            yield return Struct?.FullName ?? "";
            yield return Type?.FullName ?? "";
            yield return Header ?? "";
            yield return Set ? "set" : "";
            yield return ArrayType ?? "";
            yield return FlagOffset + "/" + ValueOffset;
            yield return string.Join("/", Offsets);
            foreach (var child in new[] { Inner, Element, Key, Value }.Concat(Items))
                yield return child?.Identity ?? "-";
            yield return string.Join("/", Slots.Select(slot => slot.Field));
        }
    }

    /// <summary>
    /// The slot of a closed generic type written behind a type id (<c>slot$0</c>, a codec the module builds): the type
    /// id, then the body of a type assignable to it.
    /// </summary>
    public sealed class TsSlot
    {
        internal TsSlot(string name, SlotModel model, TsShape shape)
        {
            Name = name;
            Model = model;
            Shape = shape;
        }

        public string Name { get; }

        public SlotModel Model { get; }

        /// <summary>The closed type as a generic shape (its TypeScript type).</summary>
        public TsShape Shape { get; }

        /// <summary>The closed generic types it allows, each a subclass with its codecs.</summary>
        public List<TsSlotClosed> Closed { get; internal set; } = new();

        /// <summary>The types of its own it allows (not generic).</summary>
        public List<TsClass> Assignable { get; internal set; } = new();

        public string RuntimeName => Model.RuntimeName;
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

        /// <summary>The type parameters of the code of a value holding them (<c>&lt;T&gt;</c>), else empty.</summary>
        public string TypeParams { get; internal set; } = "";

        /// <summary>The codecs parameter of such code (<c>, c: Bag$Codecs&lt;T&gt;</c>), else empty.</summary>
        public string Codecs { get; internal set; } = "";
    }

    /// <summary>
    /// A codec built from the codecs of type arguments: one a generic type's codec builds (<c>c.d0</c>), or one of a
    /// closed generic type the module builds (<c>codec$0</c>).
    /// </summary>
    public sealed class TsDerived
    {
        internal TsDerived(string name, string key, TsShape shape)
        {
            Name = name;
            Key = key;
            Shape = shape;
        }

        public string Name { get; }

        internal string Key { get; }

        /// <summary>The generic type with its arguments.</summary>
        public TsShape Shape { get; }

        /// <summary>The subclass the codec builds, for a closed type a slot holds (<c>BoxInt32</c>), else null.</summary>
        public string? Subclass { get; internal set; }
    }

    /// <summary>
    /// A closed generic type in a polymorphic slot: a subclass (<c>BoxInt32 extends Box&lt;number&gt;</c>) whose
    /// constructor the writers switch on, and its codecs (<c>closed$0</c>) built in the module, read and written behind
    /// the id of its closed .NET name.
    /// </summary>
    public sealed class TsSlotClosed
    {
        internal TsSlotClosed(string name, string codecs, ClosedTypeModel closed)
        {
            Name = name;
            Codecs = codecs;
            Closed = closed;
        }

        /// <summary>The subclass.</summary>
        public string Name { get; }

        /// <summary>The variable of its codecs (an <c>X$Codecs</c>).</summary>
        public string Codecs { get; }

        public ClosedTypeModel Closed { get; }

        public TsShape? Shape { get; internal set; }

        public int Id => Closed.TypeId;

        public string RuntimeName => Closed.RuntimeName;
    }

    /// <summary>A base class or interface: its name, and its shape when it is generic.</summary>
    public sealed class TsBase
    {
        internal TsBase(string name, TsShape? shape)
        {
            Name = name;
            Shape = shape;
        }

        public string Name { get; }

        public TsShape? Shape { get; }
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

        /// <summary>A generic base class with the arguments this class gives it (<c>Shelf&lt;number&gt;</c>), else null.</summary>
        public TsShape? BaseShape => _target.BaseLayout(Model) is { } b && b == Model.BaseType && Model.BaseValue != null
            ? _target.BaseShape(Model, Model.BaseValue)
            : null;

        public List<string> InterfaceNames => _target.SupportedInterfaces(Model).Select(i => _target.Name(i)).ToList();

        /// <summary>The interfaces it implements, a generic one with its arguments (as a shape), else by name.</summary>
        public List<TsBase> Interfaces => Model.Interfaces.Select((i, n) => (i, n))
            .Where(x => _target.SupportedInterfaces(Model).Contains(x.i))
            .Select(x => new TsBase(_target.Name(x.i),
                Model.InterfaceValues[x.n] is { } value ? _target.BaseShape(Model, value) : null)).ToList();

        /// <summary>
        /// Members declared on a generic base class: set again in the constructor, where this class gives the base's
        /// type parameter a type with another default.
        /// </summary>
        public List<TsMember> Rebound => Model.BaseValue == null ? new List<TsMember>()
            : Members.Where(m => !Own.Contains(m) && m.Shape.Kind != "param").ToList();

        /// <summary>
        /// The elements of a <c>[NinoType]</c> collection (<c>$elements</c>): a sequence or a map, written before its
        /// members.
        /// </summary>
        public TsShape? Elements => Model.Elements == null ? null : _target.ShapeIn(Model, Model.Elements);

        /// <summary>Whether the class declares <c>$elements</c> (its base class does not).</summary>
        public bool DeclaresElements => Model.Elements != null && _target.BaseLayout(Model)?.Elements == null;

        /// <summary>Whether its values carry a reference header (<c>trackReferences</c>).</summary>
        public bool Tracked => Model.TrackReferences;

        /// <summary>
        /// A generic type holding itself with other type arguments: its codec builds the codecs of the types it holds
        /// when the data first needs them, as there is no end to them.
        /// </summary>
        public bool Lazy => CodegenModel.GenericProblem(Model) != null;

        /// <summary>The slots its codec is given (<c>s0</c>...): closed generic types written behind type ids.</summary>
        public List<TsShape> SlotParameters => Model.SlotParameters.Select(p => _target.ShapeIn(Model, p)).ToList();

        /// <summary>
        /// In a class hierarchy, V8 creates objects about twice as fast from constructors as from field initializers
        /// (one initializer per class), so the members are declared and set in the constructor there.
        /// </summary>
        public bool Hierarchy => _target.InHierarchy(Model);

        /// <summary>Whether the class body may hold other class bodies: it counts the depth for them.</summary>
        public bool Nests => _target.Nests(Model);

        /// <summary>
        /// A struct whose nesting is counted: one that can hold itself, or a generic struct (whose .NET formatter
        /// counts every one).
        /// </summary>
        public bool Counted => !Model.IsClassBody && (Model.IsGenericDefinition || _target.HoldsItself(Model));

        /// <summary>The type parameters of a generic definition (empty otherwise).</summary>
        public IReadOnlyList<string> Parameters => Model.TypeParameters;

        /// <summary><c>&lt;T, U&gt;</c> of a generic definition, else empty.</summary>
        public string TypeParams => TypeScriptTarget.TypeParams(Model);

        /// <summary>The type as its code names it: <c>Box&lt;T&gt;</c>.</summary>
        public string Self => Name + TypeParams;

        /// <summary>The codecs parameter of a generic type's code (<c>, c: Box$Codecs&lt;T&gt;</c>), else empty.</summary>
        public string Codecs => Parameters.Count == 0 ? "" : ", c: " + Name + "$Codecs" + TypeParams;

        /// <summary><c>, c</c> where a generic type's code passes its codecs on, else empty.</summary>
        public string CodecsArgument => Parameters.Count == 0 ? "" : ", c";

        /// <summary>The codecs a generic type's codec builds, in order.</summary>
        public List<TsDerived> Derived
        {
            get
            {
                _ = Members;
                return _target.DerivedOf(Model);
            }
        }

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

        /// <summary>The closed generic types a slot declared as this type allows.</summary>
        public List<TsSlotClosed> AssignableClosed => _target.AllowedClosed(Model);

        /// <summary>A generic definition's closed types that sit in a slot: its codecs then build their subclasses.</summary>
        public List<TsSlotClosed> Closed => _target.ClosedOf(Model);
    }
}
