using System;
using System.Collections.Generic;
using System.Linq;
using Nino.Codegen.Layouts;

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
        /// The runtime-form name its type id hashes and the corpus uses (<c>NinoTypeId.GetName</c>:
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

        /// <summary>The value of a <see cref="Raw"/> type.</summary>
        public ValueModel? RawValue { get; internal set; }

        /// <summary>The elements of a <c>[NinoType]</c> collection, written before its members.</summary>
        public ValueModel? Elements { get; internal set; }

        /// <summary>The nearest base class with a layout.</summary>
        public TypeModel? BaseType { get; internal set; }

        /// <summary>The interfaces with a layout that it implements.</summary>
        public List<TypeModel> Interfaces { get; } = new();

        /// <summary>
        /// The types a value written behind this type's id can be: every concrete type with a type id assignable to it
        /// (itself included), ordered by name. Empty for a type that is not written behind a type id.
        /// </summary>
        public List<TypeModel> Assignable { get; } = new();

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
}
