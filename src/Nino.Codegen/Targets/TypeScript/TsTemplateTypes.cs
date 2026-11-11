using System;
using System.Collections.Generic;
using System.Linq;
using Nino.Codegen.Model;

namespace Nino.Codegen.Targets
{
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

        public List<string> InterfaceNames => _target.SupportedInterfaces(Model).Select(_target.Name).ToList();

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
            HeaderKind.Presence => "null",
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
