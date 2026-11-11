using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Nino.Codegen.Model;
using Nino.Core.Internal;

namespace Nino.Codegen.Targets
{
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

    /// <summary>
    /// A raw struct whose Go memory may be its bytes: one whose fields are all values every bit pattern of which is
    /// valid, but bools (see <see cref="GoTarget.RawBlock"/>). Whether its fields sit at their wire offsets in Go
    /// is the Go compiler's to say: <see cref="Expression"/> is a constant of <c>unsafe.Sizeof</c> and
    /// <c>unsafe.Offsetof</c> (false on a 32-bit host that aligns an <c>int64</c> to 4 bytes), named
    /// <see cref="AsIs"/>, which <c>nino.ReadRaw</c> and <c>nino.WriteRaw</c> take with the offsets of its bools.
    /// </summary>
    public sealed class GoRawBlock
    {
        private readonly GoTarget _target;

        internal GoRawBlock(GoTarget target, RawStructModel model)
        {
            _target = target;
            Model = model;
        }

        internal RawStructModel Model { get; }

        /// <summary>The offsets of its bools (those of nested raw structs too): their bytes must be 0 or 1.</summary>
        internal List<int> BoolOffsets { get; } = new();

        /// <summary>The offsets of the bytes its fields fill: fewer than its size when it has padding.</summary>
        internal SortedSet<int> Filled { get; } = new();

        internal void Fill(int offset, int size) => Filled.UnionWith(Enumerable.Range(offset, size));

        internal List<GoRawBlock> Nested { get; } = new();

        /// <summary>The constant's name: <c>asIs</c> and the struct's.</summary>
        public string AsIs => "asIs" + _target.Name(Model);

        /// <summary>
        /// The offsets of its padding bytes as a Go slice, or <c>nil</c>: <c>nino.WriteRaw</c> writes them as zeros.
        /// </summary>
        public string Pads => Offsets(Enumerable.Range(0, Model.Size).Where(o => !Filled.Contains(o)));

        /// <summary>The offsets of its bools as a Go slice, or <c>nil</c>.</summary>
        public string Bools => Offsets(BoolOffsets);

        private static string Offsets(IEnumerable<int> offsets)
        {
            var list = offsets.ToList();
            return list.Count == 0
                ? "nil"
                : "[]int{" + string.Join(", ", list.Select(o => o.ToString(CultureInfo.InvariantCulture))) + "}";
        }

        /// <summary>The constant: its size and each field's offset as in the wire, and so for nested raw structs.</summary>
        public string Expression
        {
            get
            {
                string name = _target.Name(Model);
                var names = GoTarget.FieldNames(Model.Fields.Select(f => f.Name));
                var parts = new List<string>
                    { "unsafe.Sizeof(" + name + "{}) == " + Model.Size.ToString(CultureInfo.InvariantCulture) };
                for (int i = 0; i < names.Count; i++)
                    parts.Add("unsafe.Offsetof(" + name + "{}." + names[i] + ") == " +
                              Model.Fields[i].Offset.ToString(CultureInfo.InvariantCulture));
                parts.AddRange(Nested.Select(n => n.AsIs).Distinct(StringComparer.Ordinal));
                return string.Join(" &&\n\t", parts);
            }
        }
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
            Block = target.RawBlock(model);
        }

        /// <summary>Whether it can be copied as memory, and how; else null.</summary>
        public GoRawBlock? Block { get; }

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
