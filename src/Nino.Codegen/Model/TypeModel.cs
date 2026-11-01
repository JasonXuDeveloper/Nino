using System;
using System.Collections.Generic;
using System.Linq;
using Nino.Codegen.Layouts;
using Nino.Core.Internal;

namespace Nino.Codegen.Model
{
    /// <summary>The body format of a <c>[NinoType]</c> (<c>NinoFormat</c>).</summary>
    public enum BodyFormat
    {
        Positional = TypeLayout.Positional,
        VersionTolerant = TypeLayout.VersionTolerant,
        Keyed = TypeLayout.Keyed,
    }

    /// <summary>What is written in front of a body (<c>NinoHeader</c>).</summary>
    public enum HeaderKind
    {
        /// <summary>Nothing: a struct.</summary>
        None = TypeLayout.NoHeader,

        /// <summary>A presence byte: a sealed class.</summary>
        NullFlag = TypeLayout.NullFlag,

        /// <summary>The type id: a class that is not sealed, an abstract class, an interface.</summary>
        TypeId = TypeLayout.TypeId,
    }

    /// <summary>A <c>[NinoType]</c> as its published layout describes it.</summary>
    public sealed class TypeModel
    {
        internal TypeModel(TypeLayout layout)
        {
            Layout = layout;
            Clr = layout.Type;
        }

        internal TypeLayout Layout { get; }

        internal Type Clr { get; }

        /// <summary>The full name, as reflection gives it (<c>Conformance.Box`1</c>, <c>A.Outer+Inner</c>).</summary>
        public string FullName => Clr.FullName ?? Clr.Name;

        /// <summary>
        /// The runtime-form name its type id hashes and the corpus uses (<c>NinoTypeId.GetRuntimeName</c>:
        /// <c>Namespace.Outer+Inner</c>, generic arguments in <c>&lt;&gt;</c>).
        /// </summary>
        public string RuntimeName => TypeNames.Runtime(Clr);

        /// <summary>The name without namespace or arity (<c>Box</c>).</summary>
        public string Name => TypeNames.WithoutArity(Clr.Name);

        public string Namespace => Clr.Namespace ?? "";

        public BodyFormat Format => (BodyFormat)Layout.Format;

        public HeaderKind Header => (HeaderKind)Layout.Header;

        public bool TrackReferences => Layout.TrackReferences;

        /// <summary>An unmanaged struct written as its raw memory: <see cref="RawValue"/>.</summary>
        public bool Raw => Layout.Raw;

        /// <summary>The type id (0 for a generic definition).</summary>
        public int TypeId => Layout.Id;

        /// <summary>
        /// Whether .NET defers a body nested too deep (wire-format spec §7.10.1), or null for a layout from a generator
        /// that does not publish it.
        /// </summary>
        public bool? Deferrable => Layout.Deferrable;

        /// <summary>The layout as <c>NinoTypeLayoutAttribute.ToString()</c> writes it.</summary>
        public string LayoutText => Layout.Text;

        public bool IsValueType => Clr.IsValueType;

        public bool IsInterface => Clr.IsInterface;

        public bool IsAbstract => Clr.IsAbstract && !Clr.IsInterface;

        public bool IsSealed => Clr.IsSealed;

        public bool IsGenericDefinition => Clr.IsGenericTypeDefinition;

        public bool IsGeneric => Clr.IsGenericType;

        /// <summary>The type parameters of a generic definition (<c>T</c>, <c>TKey</c>), in order; empty otherwise.</summary>
        public IReadOnlyList<string> TypeParameters =>
            Clr.IsGenericTypeDefinition ? Clr.GetGenericArguments().Select(a => a.Name).ToList() : Array.Empty<string>();

        /// <summary>A class (or interface): its body counts toward the depth and may be deferred.</summary>
        public bool IsClassBody => !Clr.IsValueType;

        /// <summary>A class that can be created: not abstract, not an interface.</summary>
        public bool IsConcrete => !Clr.IsAbstract && !Clr.IsInterface;

        /// <summary>The number of base types (<c>object</c> is 0, a class deriving from it 1...).</summary>
        public int InheritanceDepth
        {
            get
            {
                int depth = 0;
                for (var t = Clr.BaseType; t != null; t = t.BaseType)
                    depth++;
                return depth;
            }
        }

        /// <summary>The members in the order they are written.</summary>
        public List<MemberModel> Members { get; } = new();

        /// <summary>
        /// Whether every value takes the same number of bytes, so that a list of it is one block: a struct that is not
        /// raw, positional and not a collection, whose members are all of fixed size (<see cref="ValueModel.IsFixed"/>),
        /// such structs, or type parameters (in a generic definition: of fixed size when their arguments are).
        /// </summary>
        public bool FixedSize => IsValueType && !Raw && Format == BodyFormat.Positional && Elements == null &&
            Members.Count != 0 && Members.All(m => m.Value.IsFixed || m.Value.Kind == ValueKind.Parameter ||
                m.Value is { Kind: ValueKind.Type, Type: { } t } && t != this && t.FixedSize);

        /// <summary>The value of a <see cref="Raw"/> type.</summary>
        public ValueModel? RawValue { get; internal set; }

        /// <summary>The elements of a <c>[NinoType]</c> collection, written before its members.</summary>
        public ValueModel? Elements { get; internal set; }

        /// <summary>The nearest base class with a layout (a generic one by its definition).</summary>
        public TypeModel? BaseType { get; internal set; }

        /// <summary>
        /// <see cref="BaseType"/> with the arguments this type gives it (<c>Shelf&lt;int&gt;</c>, <c>Shelf&lt;T&gt;</c>), as a
        /// <see cref="ValueKind.Generic"/> value; null when the base type is not generic.
        /// </summary>
        public ValueModel? BaseValue { get; internal set; }

        /// <summary>The interfaces with a layout that it implements (a generic one by its definition).</summary>
        public List<TypeModel> Interfaces { get; } = new();

        /// <summary>
        /// The generic interfaces of <see cref="Interfaces"/> with the arguments this type gives them, in the same
        /// order; null where the interface is not generic.
        /// </summary>
        public List<ValueModel?> InterfaceValues { get; } = new();

        /// <summary>
        /// A generic definition's slots of generic types written behind type ids that hold its own type parameters
        /// (<c>Crate&lt;T&gt;</c> in <c>Pair&lt;T&gt;</c>), its own or those of the generic types it holds: their type ids
        /// depend on the type arguments' .NET names, which only a closed use of the definition knows. Its codecs are
        /// given these slots (<see cref="ValueModel.SlotArguments"/>) with the codecs of its arguments.
        /// </summary>
        public List<ValueModel> SlotParameters { get; } = new();

        /// <summary>
        /// The generic types written behind type ids (non-sealed generic classes, abstract generic classes, generic
        /// interfaces) that a value of this type can sit in, with the arguments this type gives them, by definition:
        /// itself (a generic definition written behind its own id), its base classes and its interfaces
        /// (<c>Shelf&lt;int&gt;</c> for <c>IntShelf : Shelf&lt;int&gt;</c>, <c>Shelf&lt;T&gt;</c> for
        /// <c>ListShelf&lt;T&gt; : Shelf&lt;T&gt;</c>). Languages without inheritance mark a type with them, so a slot of a
        /// closed one (<see cref="SlotModel"/>) accepts it.
        /// </summary>
        public List<ValueModel> SlotBases { get; } = new();

        /// <summary>Why <see cref="SlotParameters"/> has no end (its slots grow with every level), or null.</summary>
        public string? SlotProblem { get; internal set; }

        /// <summary>
        /// The types a value written behind this type's id can be: every concrete type with a type id assignable to it
        /// (itself included), ordered by name. Empty for a type that is not written behind a type id.
        /// </summary>
        public List<TypeModel> Assignable { get; } = new();

        /// <summary>
        /// The closed generic types (<c>Box&lt;int&gt;</c>) a value written behind this type's id can be, of those the
        /// assembly names (<see cref="CodegenModel.ClosedTypes"/>), ordered by name. Empty for a type that is not
        /// written behind a type id.
        /// </summary>
        public List<ClosedTypeModel> ClosedAssignable { get; } = new();

        /// <summary>Whether a value of <paramref name="other"/> can sit where this type is declared.</summary>
        public bool IsAssignableFrom(TypeModel other) => Clr.IsAssignableFrom(other.Clr);

        public override string ToString() => FullName;
    }

    /// <summary>A member of a layout, in the order it is written.</summary>
    public sealed class MemberModel
    {
        internal MemberModel(string name, string typeName, ValueModel value)
        {
            Name = name;
            TypeName = typeName;
            Value = value;
        }

        /// <summary>The C# name.</summary>
        public string Name { get; }

        /// <summary>The C# type as the layout writes it (<c>int</c>, <c>System.Collections.Generic.List&lt;int&gt;</c>).</summary>
        public string TypeName { get; }

        public ValueModel Value { get; internal set; }

        /// <summary>The key of a keyed member.</summary>
        public int Key { get; internal set; }

        /// <summary>The keys of its former names, also read.</summary>
        public List<int> FormerKeys { get; } = new();

        /// <summary>The key, then the former keys.</summary>
        public IEnumerable<int> AllKeys => new[] { Key }.Concat(FormerKeys);

        /// <summary>The type that declares the member (the type itself, or a base class).</summary>
        internal Type? DeclaringClr { get; set; }

        /// <summary>The declaring type's layout, if it has one.</summary>
        public TypeModel? DeclaringType { get; internal set; }

        public override string ToString() => $"{Name}: {Value}";
    }

    /// <summary>
    /// A closed generic <c>[NinoType]</c> the assembly names (<c>Box&lt;int&gt;</c>, from <c>new Box&lt;int&gt;()</c>
    /// or <c>NinoPolymorphic.Register&lt;Box&lt;int&gt;&gt;()</c>): written behind its own type id, the hash of its
    /// closed .NET name, where a polymorphic slot holds it. Its body is its definition's with these arguments.
    /// </summary>
    public sealed class ClosedTypeModel
    {
        internal ClosedTypeModel(Type clr, ValueModel value)
        {
            Clr = clr;
            Value = value;
            RuntimeName = TypeNames.Runtime(clr);
            TypeId = NinoHash.Compute(RuntimeName);
        }

        public Type Clr { get; }

        /// <summary>The definition with its arguments (<see cref="ValueKind.Generic"/>).</summary>
        public ValueModel Value { get; }

        public TypeModel Definition => Value.Type!;

        /// <summary>The runtime-form name its type id hashes (<c>Conformance.Box&lt;System.Int32&gt;</c>).</summary>
        public string RuntimeName { get; }

        public int TypeId { get; }

        public bool IsValueType => Clr.IsValueType;

        public override string ToString() => RuntimeName;
    }
}
