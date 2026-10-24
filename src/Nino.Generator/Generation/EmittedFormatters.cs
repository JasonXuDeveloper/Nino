using System.Collections.Generic;
using System.Text;
using Microsoft.CodeAnalysis.Text;

namespace Nino.Generator.Generation;

/// <summary>
/// The formatter files emitted for recent models, by value: a model equal to one emitted before gets the same
/// <see cref="SourceText"/> back without being emitted again.
/// <para>
/// Roslyn matches the values of an incremental step to the previous run's by position, not by value: a type inserted
/// ahead of others (earlier in a file) shifts the ones after it, which then compare unequal to what was in their
/// position before and reach the output step again, although their models did not change. Their files are then a
/// lookup here, and the same text instance lets the compiler keep the syntax tree it parsed from it.
/// </para>
/// </summary>
internal static class EmittedFormatters
{
    /// <summary>Entries per generation: when the current one is full, it becomes the previous one.</summary>
    private const int Generation = 2048;

    private static readonly object Lock = new();
    private static Dictionary<TypeModel, SourceText> _current = new();
    private static Dictionary<TypeModel, SourceText> _previous = new();

    /// <summary>The file of <paramref name="model"/>, emitted by <paramref name="emit"/> unless an equal model's is known.</summary>
    public static SourceText Get(TypeModel model, System.Func<TypeModel, string> emit)
    {
        var key = model;
        lock (Lock)
        {
            if (_current.TryGetValue(key, out var text))
                return text;
            if (_previous.TryGetValue(key, out text))
            {
                Add(key, text);
                return text;
            }
        }

        var emitted = SourceText.From(emit(model), Encoding.UTF8);
        lock (Lock)
            Add(key, emitted);
        return emitted;
    }

    /// <summary>Adds an entry to the current generation, starting a new one when it is full. Guarded by <see cref="Lock"/>.</summary>
    private static void Add(TypeModel key, SourceText text)
    {
        if (_current.Count >= Generation)
        {
            _previous = _current;
            _current = new Dictionary<TypeModel, SourceText>();
        }

        _current[key] = text;
    }
}
