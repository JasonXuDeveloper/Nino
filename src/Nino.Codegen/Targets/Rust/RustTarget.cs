using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Nino.Codegen.Model;

namespace Nino.Codegen.Targets
{
    /// <summary>
    /// Rust: a struct per type and its codecs written out (<c>Templates/rust/types.sbn</c>), in the shape of
    /// <c>runtimes/rust/tests/conformance/types.rs</c>: <c>NinoReadBody</c>/<c>NinoWriteBody</c> for bodies, the
    /// runtime's own impls for collections, strings and the BCL values, a raw struct as one fixed-size array, and an
    /// enum per type written behind a type id holding only the types it allows (<c>XRef</c>). A type it cannot express
    /// is left out with a warning, and so is every type holding one.
    /// </summary>
    public sealed partial class RustTarget : CodegenTarget
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
            var types = ResolveTypes();

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
                .Select(t => new RustRef(Name(t) + "Ref", t.FullName, Variants(Allowed(t), t.ClosedAssignable))
                    { Tracked = t.TrackReferences })
                .ToList();
            refs.AddRange(_slotNames.Select(p => new RustRef(p.Value, p.Key.RuntimeName,
                Variants(p.Key.Assignable.Where(_supported.Contains), p.Key.ClosedAssignable))));
            if (_usesObject)
                refs.Add(new RustRef("AnyObject", "object", Variants(ObjectTypes(), _closed.Keys)));

            var renderer = new TemplateRenderer("rust");
            renderer.Set("source", source);
            renderer.Set("serde", _options.Serde);
            renderer.Set("std_maps", _options.StdMaps);
            renderer.Set("serde_rename_all", SerdeRule);
            renderer.Set("enums", _enums.OrderBy(Name, StringComparer.Ordinal)
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

        protected override void ShapeClosed(ClosedTypeModel closed) => _closed[closed] = ClosedShape(closed);

        /// <summary>The closed types' shapes keep the enums, raw structs and slots they met.</summary>
        protected override void ResetClosed()
        {
        }

        protected override IEnumerable<object> Declared => _enums.Cast<object>().Concat(_rawStructs);

        protected override void DeclareRaw(TypeModel type) => Raw(type.RawValue!);

        /// <summary>A closed generic type's body: the generic class or struct with its arguments.</summary>
        private RustShape ClosedShape(ClosedTypeModel closed) => GenericShape(closed.Value);

        /// <summary>
        /// The variants of a slot: its types with layouts, then its closed generic types, by name. A tracked class is
        /// a <c>nino::Shared</c>.
        /// </summary>
        private List<RustVariant> Variants(IEnumerable<TypeModel> types, IEnumerable<ClosedTypeModel> closed)
        {
            var variants = types.OrderBy(Name, StringComparer.Ordinal)
                .Select(t => new RustVariant(Name(t), t, null, Name(t) + "::NINO_TYPE_ID", t.IsValueType)
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
}
