using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
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
/// lookup here, and the same text instance lets the compiler keep the syntax tree it parsed from it. The index
/// does not own the models or texts: an emission lives while Roslyn or another consumer holds its text.
/// </para>
/// </summary>
internal static class EmittedFormatters
{
    private const int GenerationSize = 2048;

    private static readonly object Lock = new();
    private static readonly ConditionalWeakTable<SourceText, Emission> Live = new();
    private static Index _current = new();
    private static Index _previous = new();

    private sealed record Emission(TypeModel Model, bool FileScoped, SourceText Text);

    private sealed class Index
    {
        private readonly Dictionary<int, List<WeakReference<Emission>>> _buckets = new();
        public int Count { get; private set; }

        public WeakReference<Emission>? Find(int hash, TypeModel model, bool fileScoped, out SourceText? text)
        {
            if (_buckets.TryGetValue(hash, out var bucket))
            {
                for (int i = bucket.Count - 1; i >= 0; i--)
                {
                    var weak = bucket[i];
                    if (!weak.TryGetTarget(out var entry))
                    {
                        bucket.RemoveAt(i);
                        Count--;
                    }
                    else if (entry.FileScoped == fileScoped && entry.Model.Equals(model))
                    {
                        text = entry.Text;
                        return weak;
                    }
                }

                if (bucket.Count == 0)
                    _buckets.Remove(hash);
            }

            text = null;
            return null;
        }

        public void Add(int hash, WeakReference<Emission> emission)
        {
            if (!_buckets.TryGetValue(hash, out var bucket))
                _buckets.Add(hash, bucket = new List<WeakReference<Emission>>());
            bucket.Add(emission);
            Count++;
        }
    }

    /// <summary>
    /// The file of <paramref name="model"/> in the layout <paramref name="fileScoped"/> selects, emitted by
    /// <paramref name="emit"/> unless an equal model's is known.
    /// </summary>
    public static SourceText Get(TypeModel model, bool fileScoped, System.Func<TypeModel, bool, string> emit)
    {
        // The index compares the full model on a hit; bucket it without hashing every member and registration.
        int count = model.Members.Length;
        string? lastType = count == 0 ? null : model.Members[count - 1].TypeName;
        int hash = (model.FormatterName, model.Format, count, lastType, fileScoped).GetHashCode();
        lock (Lock)
        {
            if (_current.Find(hash, model, fileScoped, out var text) != null)
                return text!;
            if (_previous.Find(hash, model, fileScoped, out text) is { } previous)
            {
                Add(hash, previous);
                return text!;
            }
        }

        var emitted = SourceText.From(emit(model, fileScoped), Encoding.UTF8);
        var entry = new Emission(model, fileScoped, emitted);
        lock (Lock)
        {
            // Another generator may have finished an equal emission while this one rendered.
            if (_current.Find(hash, model, fileScoped, out var text) != null)
                return text!;
            Live.Add(emitted, entry);
            Add(hash, new WeakReference<Emission>(entry));
        }
        return emitted;
    }

    /// <summary>Adds an entry to the current generation, starting a new one when it is full. Guarded by <see cref="Lock"/>.</summary>
    private static void Add(int hash, WeakReference<Emission> entry)
    {
        if (_current.Count >= GenerationSize)
        {
            _previous = _current;
            _current = new Index();
        }

        _current.Add(hash, entry);
    }
}
