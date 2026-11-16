using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using Nino.Core.Internal;

namespace Nino.Codegen.Model
{
    public sealed partial class CodegenModel
    {
        /// <summary>
        /// The cases of a C# union (wire-format spec §7.12): the types of its public one-argument constructors, which the
        /// compiler declares one per case, in declaration order. The layout lists the same types
        /// (<c>union(type(A),...)</c>, sorted by name, in C# spelling); the constructors give them as types, which the
        /// case ids and values need.
        /// </summary>
        private void Union(TypeModel type)
        {
            if (type.IsGenericDefinition)
            {
                type.UnionProblem = "a generic C# union is not supported: the type ids of its cases depend on its type arguments";
                return;
            }

            var constructors = type.Clr.GetConstructors(BindingFlags.Instance | BindingFlags.Public)
                .Where(c => c.GetParameters().Length == 1).OrderBy(c => c.MetadataToken).ToList();
            int published = type.Layout.Elements!.Arguments.Count;
            if (constructors.Count != published)
            {
                type.UnionProblem =
                    $"its layout lists {published} case(s) and its type {constructors.Count} constructor(s) of one argument";
                return;
            }

            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var constructor in constructors)
            {
                var clr = constructor.GetParameters()[0].ParameterType;
                string name = TypeNames.Closed(clr);
                for (int i = 2; !names.Add(name); i++)
                    name = TypeNames.Closed(clr) + i.ToString(CultureInfo.InvariantCulture);
                // A [NinoType]'s id is its layout's ([NinoFormerName] included); any other type's is its name's hash.
                var known = Find(clr);
                int id = known != null && !known.IsGenericDefinition && known.TypeId != 0
                    ? known.TypeId
                    : NinoHash.Compute(TypeNames.Runtime(clr));
                var value = UnionCaseValue(clr);
                type.Cases.Add(new UnionCaseModel(name, clr, id, value)
                {
                    Nullable = CanBeNull(value),
                    Leaf = IsLeaf(value),
                });
            }

            var ids = type.Cases.GroupBy(c => c.TypeId).FirstOrDefault(g => g.Count() > 1);
            if (ids != null)
                type.UnionProblem = "its cases " + string.Join(" and ", ids.Select(c => c.RuntimeName)) +
                                    " have the same type id";
        }

        /// <summary>
        /// The value of a case: what the formatter .NET finds for the case's type writes (<see cref="RootValue"/>). A
        /// tuple, pair or other generic struct without a layout is written as raw memory by the formatter of a member
        /// and item by item by the formatter of a type argument (<see cref="ArgumentEncoding"/>); the layout does
        /// not say which a case's is, so such a case is refused.
        /// </summary>
        private ValueModel UnionCaseValue(Type clr)
        {
            if (RawShapeIn(clr) is { } shape)
                return Unsupported(clr, "", $"the union case {TypeNames.Runtime(clr)} " +
                                            (shape == clr ? "is" : $"holds {TypeNames.Runtime(shape)},") +
                                            " a generic struct without a layout, whose encoding in a union case is not published");
            return RootValue(clr) ??
                   Unsupported(clr, "", $"the union case {TypeNames.Runtime(clr)} has no {{lang}} form");
        }

        /// <summary>A generic struct without a layout (a tuple, a pair...) that <paramref name="type"/> is or holds, or null.</summary>
        private Type? RawShapeIn(Type type)
        {
            if (type.IsArray)
                return RawShapeIn(type.GetElementType()!);
            if (!type.IsGenericType)
                return null;
            if (type.IsValueType && Generic(type) is not (Nullable or NinoNullable) && Find(type.GetGenericTypeDefinition()) == null)
                return type;
            return type.GetGenericArguments().Select(RawShapeIn).FirstOrDefault(t => t != null);
        }

        /// <summary>Whether a case's value can read as null.</summary>
        private static bool CanBeNull(ValueModel value) => value.Kind switch
        {
            ValueKind.String or ValueKind.Utf8 or ValueKind.Object or ValueKind.Nullable or ValueKind.Sequence
                or ValueKind.Map or ValueKind.Grid or ValueKind.Version or ValueKind.Named => true,
            ValueKind.Type => value.Type!.Header != HeaderKind.None,
            ValueKind.Generic => !value.Type!.IsValueType,
            _ => false,
        };

        /// <summary>
        /// Whether a case cannot hold its union again: a string, or a value Nino.Core copies as raw memory (a nullable
        /// one included), as the generator decides it (<c>UnionCase.Leaf</c>): .NET counts no depth for a union of
        /// such cases.
        /// </summary>
        private static bool IsLeaf(ValueModel value) => value.Kind switch
        {
            ValueKind.String or ValueKind.Primitive or ValueKind.Enum or ValueKind.RawStruct or ValueKind.RawTuple
                or ValueKind.RawNullable => true,
            ValueKind.Type => value.Type!.Raw,
            ValueKind.Nullable => value.Element != null && value.Element.Kind != ValueKind.String && IsLeaf(value.Element),
            _ => false,
        };
    }
}
