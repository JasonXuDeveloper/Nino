using System;
using System.Collections.Generic;
using System.Linq;
using Nino.Codegen.Model;

namespace Nino.Codegen.Targets
{
    /// <summary>
    /// TypeScript: a class per type, an <c>as const</c> object per enum and a <c>NinoCodec</c> per type, which read and
    /// write exactly the bytes the layout describes (<c>Templates/typescript/types.sbn</c>). A type whose layout it
    /// cannot express (encodings without a TypeScript form, reference tracking of a generic type) is left out with a
    /// warning, and so is every type holding one.
    /// </summary>
    public sealed partial class TypeScriptTarget : CodegenTarget
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
            var layouts = ResolveTypes();
            NameClosed(layouts);

            var enums = _enums.OrderBy(Name, StringComparer.Ordinal)
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

        protected override void ShapeClosed(ClosedTypeModel closed) => ClosedShape(closed);

        protected override IEnumerable<object> Declared =>
            _enums.Cast<object>().Concat(_rawStructs.Select(r => r.Struct));

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
        /// The name of the function that reads (<paramref name="mode"/> <c>read</c>), reads into an existing value
        /// (<c>readinto</c>) or writes a value of <paramref name="shape"/>, shared by every value of the same shape and
        /// numbered by the shape (<c>read$3</c>, <c>readInto$3</c> and <c>write$3</c> are one shape's). A new one is
        /// listed after the functions its own code calls, and the template writes them all in that order after the
        /// types.
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

            helper = new TsHelper((mode == "read" ? "read$" : mode == "readinto" ? "readInto$" : "write$") +
                ShapeNumber(shape.Identity), mode, shape);
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
}
