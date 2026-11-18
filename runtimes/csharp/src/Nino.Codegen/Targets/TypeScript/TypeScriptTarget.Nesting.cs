using System;
using System.Collections.Generic;
using System.Linq;
using Nino.Codegen.Model;

namespace Nino.Codegen.Targets
{
    public sealed partial class TypeScriptTarget
    {
        /// <summary>
        /// The non-raw structs written behind a type id (for <c>object</c> or an interface) that
        /// <paramref name="assignable"/> accepts.
        /// </summary>
        private List<TypeModel> BodyStructs(Func<TypeModel, bool> assignable) =>
            _model.Types.Where(t => t.IsValueType && !t.Raw && t.TypeId != 0 && assignable(t)).ToList();

        private readonly Dictionary<TypeModel, bool> _structOpens = new();

        /// <summary>Whether a value of <paramref name="shape"/> may hold a class body, directly or in its structs.</summary>
        private bool Opens(TsShape shape) => shape.Opens || shape.Reaches.Any(StructOpens);

        private bool StructOpens(TypeModel type)
        {
            if (_structOpens.TryGetValue(type, out bool opens))
                return opens;
            // A struct that holds itself opens what its other members open.
            _structOpens[type] = false;
            opens = _supported.Contains(type) && Nests(type);
            _structOpens[type] = opens;
            return opens;
        }

        /// <summary>
        /// Whether a class body may hold other class bodies (<c>NinoBodyTraits.Nests</c>): it counts the depth for
        /// them, and at depth 0, ends with the bodies deferred below it. Saying so of one that does not changes no
        /// byte.
        /// </summary>
        internal bool Nests(TypeModel type) => BodyValues(type).Any(v => Opens(ShapeIn(type, v)));

        /// <summary>The values a type's body holds: its members, its elements, a union's cases.</summary>
        private static IEnumerable<ValueModel> BodyValues(TypeModel type) =>
            type.Members.Select(m => (ValueModel?)m.Value).Append(type.Elements)
                .Concat(type.Cases.Select(c => c.Value)).Where(v => v != null).Select(v => v!);

        private readonly HashSet<TypeModel> _warnedDeferrable = new();

        /// <summary>
        /// Whether .NET defers the body of the class <paramref name="type"/> past 128 open bodies (else it reads and
        /// writes it in place, up to 512): <c>Deferrable</c> in its published layout. A class built by its
        /// constructor, or compared by value, is not deferred. A layout from a generator older than the property
        /// leaves it out: such a class is taken to be deferred, as most are.
        /// </summary>
        internal bool Deferrable(TypeModel type)
        {
            if (type.Deferrable is { } deferrable)
                return deferrable;
            if (_warnedDeferrable.Add(type))
                Warnings.Add($"{type.FullName}: its layout does not say whether it is deferrable (built with an " +
                             "older Nino); its bodies are taken to be deferred past 128 nested bodies, as most " +
                             "classes are.");
            return true;
        }

        /// <summary>The supported layout of the nearest base class, whose TypeScript class this one extends.</summary>
        internal TypeModel? BaseLayout(TypeModel type)
        {
            for (var t = type.BaseType; t != null; t = t.BaseType)
            {
                if (_supported.Contains(t))
                    return t;
            }

            return null;
        }

        internal bool InHierarchy(TypeModel type) =>
            BaseLayout(type) != null ||
            _model.Types.Any(t => !t.IsInterface && _supported.Contains(t) && BaseLayout(t) == type);

        internal IEnumerable<TypeModel> SupportedInterfaces(TypeModel type) =>
            type.Interfaces.Where(_supported.Contains);

        /// <summary>The concrete types written behind the type id of <paramref name="type"/>.</summary>
        internal List<TypeModel> RegisteredAssignable(TypeModel type) =>
            Registered(_supported.Where(type.IsAssignableFrom)).ToList();
    }
}
