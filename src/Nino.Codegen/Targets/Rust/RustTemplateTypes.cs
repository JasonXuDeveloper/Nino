using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Nino.Codegen.Model;

namespace Nino.Codegen.Targets
{
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
                new RustField(names[i].Field, f.Name, f.Offset,
                        target.Shape(f.Value is { Kind: ValueKind.RawTuple, Fallback: { } items } ? items : f.Value, false))
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

        /// <summary>Its type id, as a Rust expression (<c>Cat::NINO_TYPE_ID</c>, or the closed type's id).</summary>
        public string Id { get; }

        public bool IsValueType { get; }

        /// <summary>The closed type's .NET name, for a closed generic type.</summary>
        public string? RuntimeName { get; internal set; }

        /// <summary>A tracked class: held as a <c>nino::Shared</c>.</summary>
        public bool Shared { get; internal set; }
    }
}
