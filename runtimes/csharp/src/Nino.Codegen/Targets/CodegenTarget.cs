using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Nino.Codegen.Model;

namespace Nino.Codegen.Targets
{
    /// <summary>
    /// What every language's target does the same way: which of the model's types it writes (leaving out, with a
    /// warning, a type it cannot express and every type holding one), the warnings, and the names of the generated
    /// types. Each target says what it cannot express (<see cref="Problem"/>, <see cref="CheckType"/>) and how it
    /// names a type (<see cref="BaseName"/>, <see cref="Qualified"/>, <see cref="Reserved"/>).
    /// </summary>
    public abstract class CodegenTarget
    {
        private protected readonly CodegenModel _model;

        /// <summary>The types written, once <see cref="ResolveSupported"/> has run.</summary>
        private protected readonly HashSet<TypeModel> _supported = new();

        private readonly Dictionary<Type, string> _names = new();

        private readonly Dictionary<string, int> _shapeNumbers = new(StringComparer.Ordinal);

        protected CodegenTarget(CodegenModel model) => _model = model;

        /// <summary>
        /// The number in the names of the functions shared by every value of one shape (by its identity), the same for
        /// all of them (reading, writing...): the next one when the shape has none yet.
        /// </summary>
        private protected string ShapeNumber(string identity)
        {
            if (!_shapeNumbers.TryGetValue(identity, out int number))
                _shapeNumbers[identity] = number = _shapeNumbers.Count;
            return number.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        public List<string> Warnings { get; } = new();

        /// <summary>The top-level values to write codecs of (<c>--roots</c>: name and type), resolved by <see cref="ResolveRoots"/>.</summary>
        public IReadOnlyList<KeyValuePair<string, string>> RootRequests { get; set; } =
            Array.Empty<KeyValuePair<string, string>>();

        /// <summary>
        /// The roots requested (<see cref="RootRequests"/>) as the model sees them. Throws an
        /// <see cref="ArgumentException"/> for a type the assembly and its references do not have, a [NinoType]
        /// or a type named twice, since the command line asked for it by name; one whose formatter nino-gen does not model (a
        /// <c>Queue&lt;T&gt;</c>) is left out with a warning, as a type a language cannot express is.
        /// </summary>
        private protected List<RootModel> ResolveRoots()
        {
            var roots = new List<RootModel>();
            foreach (var r in RootRequests)
            {
                var type = _model.ResolveType(r.Value) ?? throw new ArgumentException(
                    $"--roots: {r.Key}={r.Value}: no such type in the assembly or the assemblies it references " +
                    "(write its runtime-form name: System.Int32[], System.Collections.Generic.List<Namespace.Item>).");
                if (_model.Types.Any(t => t.Clr == type))
                    throw new ArgumentException(
                        $"--roots: {r.Key}={r.Value}: a [NinoType], which has its codec already.");
                if (RootRequests.FirstOrDefault(o => _model.ResolveType(o.Value) == type).Key is { } first && first != r.Key)
                    throw new ArgumentException($"--roots: {r.Key} and {first} name the same type, {r.Value}.");
                if (_model.RootValue(type) is { } value)
                    roots.Add(new RootModel(r.Key, type, value));
                else
                    Warnings.Add($"root {r.Key} ({r.Value}) is not written to {Language}: nino-gen does not model its formatter.");
            }

            return roots;
        }

        /// <summary>
        /// Shapes each root with <paramref name="shape"/>, leaving out with a warning one the language cannot express;
        /// after <see cref="ResolveTypes"/>, whose names a root's may not take.
        /// </summary>
        private protected List<(RootModel Root, TShape Shape)> ShapeRoots<TShape>(Func<RootModel, TShape> shape)
        {
            var shaped = new List<(RootModel, TShape)>();
            foreach (var root in ResolveRoots())
            {
                if (Names.Contains(root.Name, StringComparer.Ordinal) || Reserved(root.Name))
                    throw new ArgumentException($"--roots: {root.Name} is the name of a generated type; choose another.");
                try
                {
                    shaped.Add((root, shape(root)));
                }
                catch (UnsupportedException e)
                {
                    Warnings.Add($"root {root.Name} ({root.RuntimeName}) is not written to {Language}: {e.Message}.");
                }
            }

            return shaped;
        }

        /// <summary>The language, as the warnings name it.</summary>
        protected abstract string Language { get; }

        /// <summary>What a target throws when shaping a value it cannot express: the message is the reason.</summary>
        protected sealed class UnsupportedException : Exception
        {
            public UnsupportedException(string message) : base(message)
            {
            }
        }

        protected void Warn(TypeModel type, string reason) =>
            Warnings.Add($"{type.FullName} is not written to {Language}: {reason}.");

        protected void WarnClosed(ClosedTypeModel closed, string reason) =>
            Warnings.Add($"{closed.RuntimeName} is left out of the polymorphic slots in {Language}: {reason}.");

        // ---------------------------------------------------------------- which types

        /// <summary>Why a type cannot be written whatever it holds (its declaration alone), or null.</summary>
        protected virtual string? Problem(TypeModel type) => type.SlotProblem ?? type.UnionProblem ?? TrackingProblem(type);

        /// <summary>Reference tracking of a generic type, which no target writes.</summary>
        protected static string? TrackingProblem(TypeModel type) => type.TrackReferences && type.IsGenericDefinition
            ? "reference tracking (trackReferences) of a generic type is not supported"
            : null;

        /// <summary>Shapes everything a type holds, throwing <see cref="UnsupportedException"/> for what it cannot.</summary>
        protected abstract void CheckType(TypeModel type);

        /// <summary>Forgets what shaping met (enums, helpers, slots...), before each check of the types.</summary>
        protected abstract void Reset();

        /// <summary>What is forgotten once the types are resolved: <see cref="Reset"/> unless a target keeps some.</summary>
        protected virtual void ResetResolved() => Reset();

        /// <summary>Why a type cannot be written, or null.</summary>
        private string? Check(TypeModel type)
        {
            try
            {
                CheckType(type);
                return null;
            }
            catch (UnsupportedException e)
            {
                return e.Message;
            }
        }

        /// <summary>
        /// Fills <see cref="_supported"/>: the types without a <see cref="Problem"/>, less those holding a type left
        /// out, until every type left can be written.
        /// </summary>
        protected void ResolveSupported()
        {
            foreach (var type in _model.Types)
            {
                string? reason = Problem(type);
                if (reason == null)
                    _supported.Add(type);
                else
                    Warn(type, reason);
            }

            while (true)
            {
                Reset();
                var failing = _model.Types.Where(_supported.Contains)
                    .Select(t => (Type: t, Reason: Check(t))).FirstOrDefault(f => f.Reason != null);
                if (failing.Type == null)
                    break;
                _supported.Remove(failing.Type);
                Warn(failing.Type, failing.Reason!);
            }

            ResetResolved();
        }

        /// <summary>
        /// The closed generic types the assembly names (<see cref="CodegenModel.ClosedTypes"/>) whose definition is
        /// written and whose arguments the target can express (<see cref="ShapeClosed"/>), once <see cref="ResolveTypes"/>
        /// has run: written behind their type ids, in the polymorphic slots they are assignable to.
        /// </summary>
        private protected readonly HashSet<ClosedTypeModel> _closedSupported = new();

        /// <summary>Shapes a closed generic type in a slot, throwing <see cref="UnsupportedException"/> for what it cannot.</summary>
        protected abstract void ShapeClosed(ClosedTypeModel closed);

        /// <summary>
        /// What is forgotten once the closed generic types are shaped: <see cref="ResetResolved"/> unless a target keeps
        /// their shapes.
        /// </summary>
        protected virtual void ResetClosed() => ResetResolved();

        /// <summary>The enums and raw structs shaping met, which are named with the types.</summary>
        protected abstract IEnumerable<object> Declared { get; }

        /// <summary>
        /// Declares a raw type no member holds (one only a polymorphic slot can hold), so that it is written too: by
        /// default nothing.
        /// </summary>
        protected virtual void DeclareRaw(TypeModel type)
        {
        }

        /// <summary>
        /// What every target's rendering starts with: the types written (<see cref="ResolveSupported"/>), the closed
        /// generic types (<see cref="_closedSupported"/>), the raw types no member holds, and the names of everything
        /// declared. Returns the types written, in the model's order.
        /// </summary>
        private protected List<TypeModel> ResolveTypes()
        {
            ResolveSupported();
            foreach (var closed in _model.ClosedTypes.Where(c => _supported.Contains(c.Definition)))
            {
                try
                {
                    ShapeClosed(closed);
                    _closedSupported.Add(closed);
                }
                catch (UnsupportedException e)
                {
                    WarnClosed(closed, e.Message);
                }
            }

            ResetClosed();
            var types = _model.Types.Where(_supported.Contains).ToList();
            foreach (var raw in types.Where(t => t.Raw))
                DeclareRaw(raw);
            AssignNames(types.Cast<object>().Concat(Declared));
            return types;
        }

        // ---------------------------------------------------------------- names

        /// <summary>A type's name before it is made unique (<see cref="TypeNames.Definition"/> in the language's form).</summary>
        protected abstract string BaseName(Type type);

        /// <summary>The name of a type that shares its <see cref="BaseName"/> with another: with its namespace.</summary>
        protected abstract string Qualified(Type type, string name);

        /// <summary>Whether a name is the language's or the generated code's own (then it takes a <c>Type</c> suffix).</summary>
        protected virtual bool Reserved(string name) => false;

        /// <summary>Names the generated types (types, enums, raw structs): unique, none reserved.</summary>
        protected void AssignNames(IEnumerable<object> types)
        {
            var all = types.Select(Clr).Distinct().ToList();
            var simple = all.ToLookup(BaseName);
            var used = new HashSet<string>(StringComparer.Ordinal);
            // Non-generic types first, so a generic type of the same name (Envelope and Envelope<T> in one namespace)
            // is the one that takes its arity (Envelope1).
            foreach (var type in all.OrderBy(t => t.IsGenericTypeDefinition))
            {
                string name = BaseName(type);
                if (simple[name].Count() > 1)
                    name = Qualified(type, name);
                if (used.Contains(Unreserved(name)) && type.IsGenericTypeDefinition)
                    name += type.GetGenericArguments().Length.ToString(CultureInfo.InvariantCulture);
                name = Unreserved(name);
                string unique = name;
                for (int n = 2; !used.Add(unique); n++)
                    unique = name + n.ToString(CultureInfo.InvariantCulture);
                _names[type] = unique;
            }
        }

        private string Unreserved(string name) => Reserved(name) ? name + "Type" : name;

        /// <summary>The generated name of a type, enum or raw struct.</summary>
        internal string Name(object type) =>
            _names.TryGetValue(Clr(type), out var name) ? name : BaseName(Clr(type));

        /// <summary>The names given so far.</summary>
        protected IEnumerable<string> Names => _names.Values;

        private static Type Clr(object type) => type switch
        {
            TypeModel t => t.Clr,
            EnumModel e => e.Clr,
            RawStructModel s => s.Clr,
            _ => throw new ArgumentException(type.ToString()),
        };
    }
}
