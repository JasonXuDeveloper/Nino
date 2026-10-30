using System;
using System.Collections.Generic;
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

        protected CodegenTarget(CodegenModel model) => _model = model;

        public List<string> Warnings { get; } = new();

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
        protected virtual string? Problem(TypeModel type) => type.SlotProblem ?? TrackingProblem(type);

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
            foreach (var type in all)
            {
                string name = BaseName(type);
                if (simple[name].Count() > 1)
                    name = Qualified(type, name);
                if (Reserved(name))
                    name += "Type";
                _names[type] = name;
            }
        }

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
