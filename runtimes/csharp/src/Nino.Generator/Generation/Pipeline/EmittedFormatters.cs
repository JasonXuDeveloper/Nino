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
    private const int MinSweep = 1024;

    private static readonly object Lock = new();

    /// <summary>Keeps each emission alive exactly as long as its text, which Roslyn or another consumer holds.</summary>
    private static readonly ConditionalWeakTable<SourceText, Emission> Live = new();

    /// <summary>
    /// The latest emission per bucket key, compared by the full model on a hit. Two live models rarely share a key
    /// (an older version of the same type, another project's), and the one emitted last wins it.
    /// </summary>
    private static readonly Dictionary<int, WeakReference<Emission>> Index = new();

    /// <summary>The <see cref="Index"/> size at which its collected entries are swept out.</summary>
    private static int _sweepAt = MinSweep;

    private sealed record Emission(TypeModel Model, bool FileScoped, SourceText Text);

    /// <summary>
    /// The file of <paramref name="model"/> in the layout <paramref name="fileScoped"/> selects, emitted by
    /// <paramref name="emit"/> unless an equal model's is known.
    /// </summary>
    public static SourceText Get(TypeModel model, bool fileScoped, Func<TypeModel, bool, string> emit)
    {
        // Compared by the full model on a hit: bucket it without hashing every member and registration.
        int count = model.Members.Length;
        string? lastType = count == 0 ? null : model.Members[count - 1].TypeName;
        int hash = (model.FormatterName, model.Format, count, lastType, fileScoped).GetHashCode();
        lock (Lock)
        {
            if (Find(hash, model, fileScoped) is { } text)
                return text;
        }

        var emitted = SourceText.From(emit(model, fileScoped), Encoding.UTF8);
        var entry = new Emission(model, fileScoped, emitted);
        lock (Lock)
        {
            // Another generator may have finished an equal emission while this one rendered.
            if (Find(hash, model, fileScoped) is { } text)
                return text;
            Live.Add(emitted, entry);
            Index[hash] = new WeakReference<Emission>(entry);
            if (Index.Count >= _sweepAt)
                Sweep();
        }

        return emitted;
    }

    /// <summary>The text of an equal model's emission, if it is still alive. Guarded by <see cref="Lock"/>.</summary>
    private static SourceText? Find(int hash, TypeModel model, bool fileScoped) =>
        Index.TryGetValue(hash, out var weak) && weak.TryGetTarget(out var entry) &&
        entry.FileScoped == fileScoped && entry.Model.Equals(model)
            ? entry.Text
            : null;

    /// <summary>Drops the entries whose emission was collected. Guarded by <see cref="Lock"/>.</summary>
    private static void Sweep()
    {
        var dead = new List<int>();
        foreach (var pair in Index)
            if (!pair.Value.TryGetTarget(out _))
                dead.Add(pair.Key);
        foreach (int key in dead)
            Index.Remove(key);
        // Doubling keeps the sweeps' cost proportional to the entries added.
        _sweepAt = Math.Max(MinSweep, 2 * Index.Count);
    }
}
