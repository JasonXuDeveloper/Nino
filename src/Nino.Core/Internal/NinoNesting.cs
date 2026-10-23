using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace Nino.Core
{
    /// <summary>
    /// Which types can hold themselves through collections, with no <c>[NinoType]</c> body in between
    /// (<c>class Tree : List&lt;Tree&gt;</c>, <c>class Map : Dictionary&lt;string, Map&gt;</c>). Class bodies count toward
    /// the read depth (<see cref="NinoDepth"/>) and generated struct bodies call <see cref="Reader.EnterStruct"/>, but a
    /// collection formatter counts nothing: data nesting such a type a million deep would overflow the stack. Their
    /// formatters count each level instead (<see cref="NestingFormatter{T,TFormatter}"/>); every other type keeps its
    /// formatter as it is.
    /// </summary>
    internal static class NinoNesting
    {
        /// <summary>
        /// Whether <paramref name="type"/> reaches itself through its base types, the elements of the collections it
        /// is (<c>IEnumerable&lt;T&gt;</c>), array elements and generic arguments, and is not a <c>[NinoType]</c>. Called
        /// once per published formatter.
        /// </summary>
        public static bool Holds(Type type)
        {
            if (type.IsValueType || Ends(type))
                return false;
            var seen = new HashSet<Type>();
            var pending = new Stack<Type>();
            pending.Push(type);
            while (pending.Count != 0)
            {
                var current = pending.Pop();
                if (current.HasElementType)
                {
                    if (Visit(current.GetElementType()))
                        return true;
                    continue;
                }

                if (current.IsGenericType)
                {
                    foreach (var argument in current.GetGenericArguments())
                    {
                        if (Visit(argument))
                            return true;
                    }
                }

                if (current.BaseType != null && Visit(current.BaseType))
                    return true;
                foreach (var element in Elements(current))
                {
                    if (Visit(element))
                        return true;
                }
            }

            return false;

            bool Visit(Type next)
            {
                // A [NinoType] (class Tree : List<Tree>, generated) counts its own body already.
                if (next == type)
                    return !NinoReflection.IsNinoType(type);
                if (!Ends(next) && seen.Add(next))
                    pending.Push(next);
                return false;
            }
        }

        /// <summary>Types that hold nothing a formatter reads: the walk stops there.</summary>
        private static bool Ends(Type type) =>
            type.IsPrimitive || type.IsEnum || type.IsPointer || type.IsGenericParameter || type == typeof(string) ||
            type == typeof(object) || type == typeof(ValueType);

        /// <summary>The element types of the <c>IEnumerable&lt;T&gt;</c>s <paramref name="type"/> implements.</summary>
        [UnconditionalSuppressMessage("Trimming", "IL2070",
            Justification = NinoReflection.InterfacesJustification)]
        private static IEnumerable<Type> Elements(Type type)
        {
            if (type.IsInterface && type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                yield break; // its argument was visited already
            foreach (var i in type.GetInterfaces())
            {
                if (i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEnumerable<>))
                    yield return i.GetGenericArguments()[0];
            }
        }
    }
}
