using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Nino.Codegen.Layouts;

namespace Nino.Codegen.Model
{
    /// <summary>
    /// The layout intermediate representation of an assembly: every <c>[NinoType]</c> its published layouts (and those
    /// of the assemblies it references) describe, each member's encoding resolved against its .NET type into a
    /// <see cref="ValueModel"/>. Language-neutral: what a target can express, and how, is the target's decision.
    /// Holds the metadata context the types live in: dispose it after rendering.
    /// </summary>
    public sealed partial class CodegenModel : IDisposable
    {
        private readonly LayoutReader _reader;
        private readonly Dictionary<Type, TypeModel> _byClr = new();
        private readonly Dictionary<Type, EnumModel> _enums = new();
        private readonly Dictionary<Type, RawStructModel> _structs = new();
        private readonly Dictionary<string, SlotModel> _slots = new(StringComparer.Ordinal);
        private readonly Queue<SlotModel> _newSlots = new();

        /// <summary>The most closed generic types and slots <see cref="Close"/> adds before it gives up.</summary>
        private const int MaxClosure = 1024;

        /// <summary>The most slot parameters a generic definition takes (<see cref="TypeModel.SlotParameters"/>).</summary>
        private const int MaxSlotParameters = 32;

        private CodegenModel(LayoutReader reader)
        {
            _reader = reader;
        }

        /// <summary>The name of the assembly the model was read from.</summary>
        public string AssemblyName => _reader.Assembly.GetName().Name ?? "";

        /// <summary>Every <c>[NinoType]</c> with a layout, in the order the layouts were read.</summary>
        public List<TypeModel> Types { get; } = new();

        /// <summary>
        /// The closed generic <c>[NinoType]</c>s the assembly names that can be created (not abstract, not
        /// interfaces), by name: what a polymorphic slot can hold besides the types with layouts.
        /// </summary>
        public List<ClosedTypeModel> ClosedTypes { get; } = new();

        /// <summary>
        /// The closed generic types written behind type ids (<see cref="SlotModel"/>) that the values hold, or that the
        /// codecs of the generic types they hold need (<see cref="ValueModel.SlotArguments"/>), ordered by name.
        /// </summary>
        public List<SlotModel> Slots { get; } = new();

        /// <summary>What the model could not describe, in every language.</summary>
        public List<string> Warnings { get; } = new();

        /// <summary>The enums the values hold, in the order they were met.</summary>
        public IEnumerable<EnumModel> Enums => _enums.Values;

        /// <summary>The raw structs the values hold (<c>[NinoType]</c> or not), in the order they were met.</summary>
        public IEnumerable<RawStructModel> RawStructs => _structs.Values;

        /// <summary>
        /// The collections of themselves (<c>class Tree : List&lt;Tree&gt;</c>, no layout of their own) by their type:
        /// the value a member of the type is written as, whose elements are <see cref="ValueKind.Named"/> values of
        /// the same type.
        /// </summary>
        public Dictionary<string, ValueModel> NamedCollections { get; } = new(StringComparer.Ordinal);

        public void Dispose() => _reader.Dispose();

        /// <summary>Reads the layouts of <paramref name="assemblyPath"/> without loading it.</summary>
        public static CodegenModel Load(string assemblyPath, IEnumerable<string> references)
        {
            var reader = new LayoutReader(assemblyPath, references);
            try
            {
                var model = new CodegenModel(reader);
                model.Build(reader.ReadAll(), reader.ClosedTypes());
                return model;
            }
            catch
            {
                reader.Dispose();
                throw;
            }
        }

        public TypeModel? Find(string fullName) => Types.FirstOrDefault(t => t.FullName == fullName);

        internal TypeModel? Find(Type type) => _byClr.TryGetValue(type, out var model) ? model : null;

        /// <summary>
        /// Why a generic definition cannot be generated as a generic type in a language whose generics are
        /// instantiated ahead of time (Go, Rust), or null when it can. A generic type that holds, through other generic
        /// types, itself with other arguments (<c>Node&lt;T&gt;</c> holding <c>Node&lt;List&lt;T&gt;&gt;</c>) has no end to the
        /// types its codecs are built from: Go rejects the instantiation cycle, Rust overflows checking it. Holding
        /// itself with its own parameters (<c>Node&lt;T&gt;</c> holding <c>Node&lt;T&gt;</c>) is fine. A language that builds
        /// those codecs when the data needs them (TypeScript) can write it.
        /// </summary>
        public static string? GenericProblem(TypeModel type)
        {
            if (!type.IsGenericDefinition)
                return null;
            var seen = new HashSet<TypeModel>();
            var pending = new Stack<TypeModel>();
            pending.Push(type);
            while (pending.Count != 0)
            {
                var next = pending.Pop();
                foreach (var used in OpenUses(next))
                {
                    if (used == type)
                        return "it holds itself with other type arguments, through " + next.FullName;
                    if (seen.Add(used))
                        pending.Push(used);
                }
            }

            return null;
        }

        /// <summary>
        /// The generic definitions a generic definition holds with arguments made of its own parameters, other than
        /// itself with its parameters in order: the codecs it builds from the codecs of its arguments.
        /// </summary>
        private static IEnumerable<TypeModel> OpenUses(TypeModel type) =>
            OwnValues(type).SelectMany(v => v.Walk())
                .Where(v => v.Kind == ValueKind.Generic && v.IsOpen && !v.IsSelf(type))
                .Select(v => v.Type!);

        /// <summary>The values a type's body is made of: its members', its elements', or its raw value.</summary>
        internal static IEnumerable<ValueModel> OwnValues(TypeModel type)
        {
            if (type.RawValue != null)
                yield return type.RawValue;
            if (type.Elements != null)
                yield return type.Elements;
            foreach (var member in type.Members)
                yield return member.Value;
        }

        // ---------------------------------------------------------------- types named at run time (nino-gen inspect)

        /// <summary>
        /// The type a runtime-form name names (<c>NinoTypeId.GetRuntimeName</c>: <c>Conformance.Box&lt;System.Int32&gt;</c>,
        /// <c>A.Outer+Inner</c>, <c>System.Int32[]</c>), the form type ids hash and the corpus uses; null when it names
        /// no type of the assembly, the core library or the assemblies it references.
        /// </summary>
        internal Type? ResolveType(string name)
        {
            int pos = 0;
            var type = ResolveType(name.Replace(" ", ""), ref pos);
            return type != null && pos == name.Replace(" ", "").Length ? type : null;
        }

        private Type? ResolveType(string name, ref int pos)
        {
            int start = pos;
            while (pos < name.Length && name[pos] is not ('<' or ',' or '>' or '['))
                pos++;
            string definition = name.Substring(start, pos - start);
            var arguments = new List<Type>();
            if (pos < name.Length && name[pos] == '<')
            {
                do
                {
                    pos++;
                    if (ResolveType(name, ref pos) is not { } argument)
                        return null;
                    arguments.Add(argument);
                } while (pos < name.Length && name[pos] == ',');

                if (pos == name.Length || name[pos++] != '>')
                    return null;
            }

            string clrName = arguments.Count == 0
                ? definition
                : definition + "`" + arguments.Count.ToString(CultureInfo.InvariantCulture);
            var type = Types.FirstOrDefault(t => t.Clr.FullName == clrName)?.Clr ?? _reader.FindType(clrName);
            if (type == null || type.IsGenericTypeDefinition != (arguments.Count != 0))
                return null;
            try
            {
                if (arguments.Count != 0)
                    type = type.MakeGenericType(arguments.ToArray());
            }
            catch (ArgumentException)
            {
                return null;
            }

            while (pos < name.Length && name[pos] == '[')
            {
                int close = name.IndexOf(']', pos);
                if (close < 0 || name.AsSpan(pos + 1, close - pos - 1).Trim(',').Length != 0)
                    return null;
                int rank = close - pos;
                type = rank == 1 ? type.MakeArrayType() : type.MakeArrayType(rank);
                pos = close + 1;
            }

            return type;
        }

        /// <summary>
        /// A value of <paramref name="type"/> as a whole payload, which Nino.Core reads with the formatter it finds for
        /// the type (<see cref="ArgumentEncoding"/>); null when that formatter is not known here.
        /// </summary>
        internal ValueModel? RootValue(Type type) =>
            ArgumentEncoding(type) is { } encoding ? Value(WireEncoding.Parse(encoding), type) : null;

        /// <summary>
        /// Whether a struct can hold itself through no class body (a collection, a nullable, an interface or
        /// <c>object</c>): .NET counts the nesting of its bodies toward the depth limit (<c>Reader.EnterStruct</c>,
        /// wire-format spec §9.9), as nothing else bounds it.
        /// </summary>
        internal bool HoldsItself(TypeModel type)
        {
            var seen = new HashSet<TypeModel>();
            var pending = new Stack<TypeModel>();
            pending.Push(type);
            while (pending.Count != 0)
            {
                foreach (var reached in OwnValues(pending.Pop()).SelectMany(v => v.Walk()).SelectMany(StructsIn))
                {
                    if (reached == type)
                        return true;
                    if (seen.Add(reached))
                        pending.Push(reached);
                }
            }

            return false;
        }

        /// <summary>The non-raw struct bodies a value can open directly (behind a type id, any of those assignable).</summary>
        private IEnumerable<TypeModel> StructsIn(ValueModel value)
        {
            var candidates = value.Kind switch
            {
                ValueKind.Type when value.Type!.Header == HeaderKind.TypeId => value.Type.Assignable,
                ValueKind.Type or ValueKind.Generic => new List<TypeModel> { value.Type! },
                ValueKind.Object => Types,
                _ => new List<TypeModel>(),
            };
            return candidates.Where(t => t.IsValueType && !t.Raw);
        }

        // ---------------------------------------------------------------- building

        private void Build(Dictionary<Type, TypeLayout> layouts, IReadOnlyCollection<Type> closedTypes)
        {
            foreach (var layout in layouts.Values)
            {
                var model = new TypeModel(layout);
                Types.Add(model);
                _byClr[layout.Type] = model;
            }

            foreach (var type in Types)
            {
                var layout = type.Layout;
                for (var t = type.Clr.BaseType; t != null; t = t.BaseType)
                {
                    if (_byClr.TryGetValue(Definition(t), out var baseType))
                    {
                        type.BaseType = baseType;
                        type.BaseValue = t.IsGenericType ? Generic(t, baseType, "type(" + TypeNames.Runtime(t) + ")", false) : null;
                        break;
                    }
                }

                foreach (var i in type.Clr.GetInterfaces())
                {
                    if (_byClr.TryGetValue(Definition(i), out var interfaceType))
                    {
                        type.Interfaces.Add(interfaceType);
                        type.InterfaceValues.Add(i.IsGenericType
                            ? Generic(i, interfaceType, "type(" + TypeNames.Runtime(i) + ")", false)
                            : null);
                    }
                }

                if (layout.Raw)
                    type.RawValue = layout.RawEncoding == null
                        ? Unsupported(type.Clr, "", $"{type.FullName} has no {{lang}} form")
                        : Raw(layout.RawEncoding, type.Clr);
                if (layout.Elements != null)
                {
                    // The collection's own encoding (seq(...) or map(...)), resolved against the type itself; its
                    // kind is that of the collection it derives from.
                    var elements = Value(layout.Elements, type.Clr);
                    if (elements.Kind is ValueKind.Sequence or ValueKind.Map && elements.Collection == CollectionKind.Other)
                        elements.Collection = CollectionOf(type.Clr, elements.Kind == ValueKind.Map);
                    type.Elements = elements.Kind is ValueKind.Sequence or ValueKind.Map or ValueKind.Unsupported
                        ? elements
                        : Unsupported(type.Clr, layout.Elements.Source, $"{type.FullName}'s elements have no {{lang}} form");
                }

                foreach (var member in layout.Members)
                {
                    var value = member.Type == null
                        ? Unsupported(type.Clr, member.Encoding.Source, $"member {member.Name} was not found")
                        : Value(member.Encoding, member.Type);
                    var model = new MemberModel(member.Name, member.TypeName, value)
                    {
                        Key = member.Key,
                        DeclaringClr = member.DeclaringType,
                    };
                    model.FormerKeys.AddRange(member.FormerKeys);
                    if (member.DeclaringType != null)
                        model.DeclaringType = Find(member.DeclaringType);
                    type.Members.Add(model);
                }
            }

            foreach (var type in Types.Where(t => t.Header == HeaderKind.TypeId))
                type.Assignable.AddRange(Types.Where(t =>
                    t.TypeId != 0 && t.IsConcrete && type.Clr.IsAssignableFrom(t.Clr)));

            // The generic slots each type can sit in, with the arguments it gives them.
            foreach (var type in Types.Where(t => t.IsConcrete))
            {
                foreach (var candidate in BasesOf(type.Clr).Where(b => b.IsGenericType))
                {
                    if (Find(Definition(candidate)) is not { Header: HeaderKind.TypeId, IsGenericDefinition: true } definition)
                        continue;
                    // A generic definition itself: its parameters are the arguments.
                    type.SlotBases.Add(Generic(candidate, definition, "type(" + TypeNames.Runtime(candidate) + ")", false));
                }
            }

            // The closed generic types the assembly names whose definitions have layouts and can be created.
            foreach (var clr in closedTypes.Where(t => !t.ContainsGenericParameters))
            {
                var definition = Find(clr.GetGenericTypeDefinition());
                if (definition == null || !definition.IsConcrete || definition.Raw)
                    continue;
                ClosedTypes.Add(new ClosedTypeModel(clr,
                    Generic(clr, definition, "type(" + TypeNames.Runtime(clr) + ")", false)));
            }

            FindSlotParameters();
            Close();

            ClosedTypes.Sort((a, b) => string.CompareOrdinal(a.RuntimeName, b.RuntimeName));
            foreach (var type in Types.Where(t => t.Header == HeaderKind.TypeId))
                type.ClosedAssignable.AddRange(ClosedTypes.Where(c => type.Clr.IsAssignableFrom(c.Clr)));
            Slots.AddRange(_slots.Values.OrderBy(v => v.RuntimeName, StringComparer.Ordinal));
            foreach (var slot in Slots)
            {
                slot.Assignable.AddRange(Types.Where(t => t.TypeId != 0 && t.IsConcrete && !t.IsGenericDefinition &&
                                                          AssignableTo(t.Clr, slot.Clr))
                    .OrderBy(t => t.RuntimeName, StringComparer.Ordinal));
                slot.ClosedAssignable.AddRange(ClosedTypes.Where(c => AssignableTo(c.Clr, slot.Clr)));
            }
        }
    }
}
