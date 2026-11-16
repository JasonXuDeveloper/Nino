using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Nino.Codegen.Layouts;

namespace Nino.Codegen.Model
{
    public sealed partial class CodegenModel
    {
        /// <summary>
        /// The slot parameters of every generic definition (<see cref="TypeModel.SlotParameters"/>): the open generic
        /// types written behind type ids it holds, then, until none is added, those the generic types it holds need,
        /// with its arguments.
        /// </summary>
        private void FindSlotParameters()
        {
            var definitions = Types.Where(t => t.IsGenericDefinition).ToList();
            var keys = definitions.ToDictionary(t => t, _ => new HashSet<string>(StringComparer.Ordinal));
            foreach (var type in definitions)
                foreach (var value in OwnValues(type).SelectMany(v => v.Walk()))
                    if (value.Kind == ValueKind.Generic && value.IsOpen && value.Type!.Header == HeaderKind.TypeId &&
                        keys[type].Add(value.TypeKey))
                        type.SlotParameters.Add(value);

            for (bool changed = true; changed;)
            {
                changed = false;
                foreach (var type in definitions.Where(t => t.SlotProblem == null))
                {
                    foreach (var value in OwnValues(type).SelectMany(v => v.Walk()).ToList())
                    {
                        if (value.Kind != ValueKind.Generic || !value.IsOpen || value.IsSelf(type) ||
                            value.Type!.Header == HeaderKind.TypeId)
                            continue;
                        foreach (var parameter in value.Type.SlotParameters.ToList())
                        {
                            var clr = Substitute(parameter.Clr, value.Clr.GetGenericArguments());
                            if (!clr.ContainsGenericParameters || !keys[type].Add(TypeKey(clr)))
                                continue;
                            if (type.SlotParameters.Count == MaxSlotParameters)
                            {
                                type.SlotProblem = "it holds generic types written behind type ids with ever more " +
                                                   "type arguments, through " + value.Type.FullName;
                                break;
                            }

                            type.SlotParameters.Add(Value(WireEncoding.Parse("type(" + TypeNames.Runtime(clr) + ")"), clr));
                            changed = true;
                        }

                        if (type.SlotProblem != null)
                            break;
                    }
                }
            }
        }

        /// <summary>
        /// Gives every generic value its slot arguments, and adds the closed types and slots that needs, until it adds
        /// none: each slot allows the closed types of the generic definitions assignable to it, with the arguments that
        /// makes them assignable, whether the assembly names them or not (<c>ListShelf&lt;int&gt;</c> for a member of
        /// <c>Shelf&lt;int&gt;</c>); their codecs need more slots in turn.
        /// </summary>
        private void Close()
        {
            var pending = new Queue<ValueModel>();
            foreach (var type in Types)
                foreach (var value in OwnValues(type))
                    pending.Enqueue(value);
            foreach (var closed in ClosedTypes)
                pending.Enqueue(closed.Value);
            var named = new HashSet<string>(ClosedTypes.Select(c => c.RuntimeName), StringComparer.Ordinal);
            int added = 0;
            while (pending.Count != 0 || _newSlots.Count != 0)
            {
                while (pending.Count != 0)
                {
                    foreach (var value in pending.Dequeue().Walk())
                    {
                        if (value.Kind != ValueKind.Generic || value.Slot != null || value.SlotArguments.Count != 0 ||
                            value.Type!.SlotParameters.Count == 0)
                            continue;
                        var arguments = value.Clr.GetGenericArguments();
                        foreach (var parameter in value.Type.SlotParameters)
                        {
                            var clr = Substitute(parameter.Clr, arguments);
                            value.SlotArguments.Add(Value(WireEncoding.Parse("type(" + TypeNames.Runtime(clr) + ")"), clr));
                        }
                    }
                }

                if (_newSlots.Count == 0)
                    break;
                var slot = _newSlots.Dequeue();
                if (++added > MaxClosure)
                {
                    Warnings.Add($"The closed generic types written behind type ids have no end (past {slot.RuntimeName}): " +
                                 $"the slots after the first {MaxClosure} are left out.");
                    _newSlots.Clear();
                    break;
                }

                // The concrete generic definitions assignable to the slot's definition, closed to fit the slot.
                foreach (var definition in Types.Where(t => t.IsGenericDefinition && t.IsConcrete && !t.Raw))
                {
                    foreach (var candidate in BasesOf(definition.Clr))
                    {
                        if (!candidate.IsGenericType ||
                            Definition(candidate) != Definition(slot.Clr))
                            continue;
                        var bindings = new Type?[definition.Clr.GetGenericArguments().Length];
                        if (!Unify(candidate, slot.Clr, bindings) || bindings.Any(b => b == null))
                            continue;
                        Type clr;
                        try
                        {
                            clr = definition.Clr.MakeGenericType(bindings!);
                        }
                        catch (ArgumentException)
                        {
                            // The arguments break the definition's constraints: no such type.
                            continue;
                        }

                        if (!named.Add(TypeNames.Runtime(clr)))
                            continue;
                        var model = new ClosedTypeModel(clr, Generic(clr, definition, "type(" + TypeNames.Runtime(clr) + ")", false));
                        ClosedTypes.Add(model);
                        pending.Enqueue(model.Value);
                    }
                }
            }
        }

        /// <summary>The slot of a closed generic type written behind a type id, added the first time it is met.</summary>
        private SlotModel SlotOf(Type clr, ValueModel value)
        {
            string name = TypeNames.Runtime(clr);
            if (_slots.TryGetValue(name, out var slot))
                return slot;
            slot = new SlotModel(clr, value);
            _slots[name] = slot;
            _newSlots.Enqueue(slot);
            return slot;
        }

        /// <summary>A generic type's definition, or the type itself.</summary>
        private static Type Definition(Type type) =>
            type.IsGenericType && !type.IsGenericTypeDefinition ? type.GetGenericTypeDefinition() : type;

        /// <summary>The type, its base types and its interfaces.</summary>
        private static IEnumerable<Type> BasesOf(Type type)
        {
            for (var t = type; t != null; t = t.BaseType)
                yield return t;
            foreach (var i in type.GetInterfaces())
                yield return i;
        }

        /// <summary>Whether a value of <paramref name="type"/> sits in a slot of <paramref name="slot"/> (by name).</summary>
        internal static bool AssignableTo(Type type, Type slot)
        {
            string key = TypeKey(slot);
            return BasesOf(type).Any(b => TypeKey(b) == key);
        }

        /// <summary>
        /// <paramref name="type"/> with the type parameters of the generic definition it is written in replaced by
        /// <paramref name="arguments"/> (by position).
        /// </summary>
        internal static Type Substitute(Type type, Type[] arguments)
        {
            if (type.IsGenericParameter)
                return arguments[type.GenericParameterPosition];
            if (type.IsArray)
            {
                var element = Substitute(type.GetElementType()!, arguments);
                return type.GetArrayRank() == 1 && type.Name.EndsWith("[]", StringComparison.Ordinal)
                    ? element.MakeArrayType()
                    : element.MakeArrayType(type.GetArrayRank());
            }

            if (!type.IsGenericType || !type.ContainsGenericParameters)
                return type;
            return type.GetGenericTypeDefinition()
                .MakeGenericType(type.GetGenericArguments().Select(a => Substitute(a, arguments)).ToArray());
        }

        /// <summary>
        /// Binds the type parameters of <paramref name="pattern"/> (by position, into <paramref name="bindings"/>) so
        /// that it is <paramref name="actual"/>; false when no binding does.
        /// </summary>
        private static bool Unify(Type pattern, Type actual, Type?[] bindings)
        {
            if (pattern.IsGenericParameter)
            {
                ref var bound = ref bindings[pattern.GenericParameterPosition];
                if (bound == null)
                {
                    bound = actual;
                    return true;
                }

                return TypeKey(bound) == TypeKey(actual);
            }

            if (pattern.IsArray)
                return actual.IsArray && pattern.GetArrayRank() == actual.GetArrayRank() &&
                       Unify(pattern.GetElementType()!, actual.GetElementType()!, bindings);
            if (pattern.IsGenericType)
            {
                if (!actual.IsGenericType || TypeKey(Definition(pattern)) != TypeKey(Definition(actual)))
                    return false;
                var (p, a) = (pattern.GetGenericArguments(), actual.GetGenericArguments());
                for (int i = 0; i < p.Length; i++)
                    if (!Unify(p[i], a[i], bindings))
                        return false;
                return true;
            }

            return TypeKey(pattern) == TypeKey(actual);
        }

        /// <summary>A .NET type as text, type parameters by position (<c>Conformance.Crate`1[!0]</c>).</summary>
        internal static string TypeKey(Type type)
        {
            if (type.IsGenericParameter)
                return "!" + type.GenericParameterPosition.ToString(CultureInfo.InvariantCulture);
            if (type.IsArray)
                return TypeKey(type.GetElementType()!) + "[" + new string(',', type.GetArrayRank() - 1) + "]";
            // A definition is itself with its own parameters (Crate`1[!0]), as a value of it in its members is.
            if (type.IsGenericType)
                return (type.GetGenericTypeDefinition().FullName ?? type.Name) + "[" +
                       string.Join(",", type.GetGenericArguments().Select(TypeKey)) + "]";
            return type.FullName ?? type.Name;
        }
    }
}
