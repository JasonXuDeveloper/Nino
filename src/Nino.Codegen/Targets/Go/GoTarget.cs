using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Nino.Codegen.Model;
using Nino.Core.Internal;

namespace Nino.Codegen.Targets
{
    /// <summary>
    /// Go: a struct per type with its <c>nino</c> tags and <c>NinoLayout</c>, and codecs in the style of
    /// <c>runtimes/go/internal/sample</c> (<c>Templates/go/types.sbn</c>): a class through <c>nino.Class</c>, a
    /// polymorphic slot as a Go interface switching over the ids its declared type allows (a generic one as a generic
    /// interface, marked with the type arguments: <c>Shelf[int32]</c>), a run of fixed-size members with one bounds
    /// check, tracked types behind reference headers, a <c>[NinoType]</c> collection with its elements in a field. A
    /// type it cannot express (a generic type holding itself with other type arguments, which Go's generics reject) is
    /// left out with a warning, and so is every type holding one.
    /// </summary>
    public sealed partial class GoTarget : CodegenTarget
    {
        public const string RuntimeImport = "github.com/JasonXuDeveloper/Nino/runtimes/go/v4";

        private readonly GoOptions _options;
        private readonly List<EnumModel> _enums = new();
        private readonly List<RawStructModel> _rawStructs = new();
        private readonly Dictionary<RawStructModel, GoRawBlock?> _rawBlocks = new();
        private readonly List<GoNamed> _namedCollections = new();
        private readonly Dictionary<string, GoHelper> _helperByKey = new(StringComparer.Ordinal);
        private readonly List<GoHelper> _helpers = new();
        private readonly HashSet<int> _tuples = new();
        private readonly Dictionary<TypeModel, GoGeneric> _generics = new();
        private readonly Dictionary<string, GoClosed> _closedByKey = new(StringComparer.Ordinal);
        private readonly List<GoClosed> _closed = new();
        private readonly List<GoSlotClosed> _slotClosed = new();
        private readonly Dictionary<SlotModel, GoSlot> _slotByModel = new();
        private readonly List<GoSlot> _slots = new();
        private GoGeneric? _context;
        private bool _usesObject;
        private bool _usesGrid;

        public GoTarget(CodegenModel model, GoOptions options) : base(model) => _options = options;

        protected override string Language => "Go";

        public string Render(string source)
        {
            var types = ResolveTypes();

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
            renderer.Set("enums", _enums.OrderBy(Name, StringComparer.Ordinal)
                .Select(e => new GoEnum(Name(e), e, Primitives[e.Underlying!.Value].Name)).ToList());
            renderer.Set("extra_tags", _options.Tags);
            renderer.Set("raw_structs", _rawStructs.OrderBy(Name, StringComparer.Ordinal)
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

        protected override string? Problem(TypeModel type) =>
            CodegenModel.GenericProblem(type) ?? type.SlotProblem ?? MarkerProblem(type) ?? TrackingProblem(type);

        /// <summary>
        /// Why a type cannot be marked as a value of the generic slots it sits in (<see cref="TypeModel.SlotBases"/>): Go
        /// marks it with one method per generic definition, taking the type arguments, so it cannot sit in two slots of
        /// one definition with other arguments (<c>IHolder&lt;int&gt;</c> and <c>IHolder&lt;string&gt;</c>).
        /// </summary>
        private static string? MarkerProblem(TypeModel type) => type.SlotBases.GroupBy(b => b.Type)
            .Where(g => g.Select(b => b.TypeKey).Distinct(StringComparer.Ordinal).Count() > 1)
            .Select(g => $"it implements {g.Key!.FullName} with two sets of type arguments, which one Go method " +
                         "cannot mark").FirstOrDefault();

        protected override void ShapeClosed(ClosedTypeModel closed) => ClosedShape(closed);

        protected override IEnumerable<object> Declared => _enums.Cast<object>().Concat(_rawStructs);

        protected override void DeclareRaw(TypeModel type) => Raw(type.RawValue!);

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

        protected override void Reset()
        {
            _enums.Clear();
            _rawStructs.Clear();
            _rawBlocks.Clear();
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

        protected override void CheckType(TypeModel type)
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
        }

        /// <summary>The type id a type is written behind: its published one, or (a closed generic) its name's.</summary>
        internal static int IdOf(TypeModel type) => type.TypeId != 0 ? type.TypeId : NinoHash.Compute(type.RuntimeName);

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

        // ---------------------------------------------------------------- names

        protected override string BaseName(Type type) => Go(TypeNames.Definition(type));

        protected override string Qualified(Type type, string name) =>
            Go((type.Namespace ?? "").Replace('.', '_')) + name;

        /// <summary>The generated code's own names (NinoCodec, NinoGrid, NinoTuple2...).</summary>
        protected override bool Reserved(string name) => name.StartsWith("Nino", StringComparison.Ordinal);

        /// <summary>An exported Go name: the parts of the C# name, each with a capital (<see cref="TypeNames.Joined"/>).</summary>
        internal static string Go(string name) => TypeNames.Joined(name);

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
        /// shape and numbered by the shape (<c>readV3</c> and <c>writeV3</c> are one shape's); the template writes
        /// them all after the types.
        /// </summary>
        private string Helper(GoShape shape, string mode)
        {
            string key = mode + ":" + shape.Identity;
            if (_helperByKey.TryGetValue(key, out var helper))
                return helper.Name;
            helper = new GoHelper(mode + "V" + ShapeNumber(shape.Identity), mode, shape);
            _helperByKey[key] = helper;
            _helpers.Add(helper);
            return helper.Name;
        }
    }
}
