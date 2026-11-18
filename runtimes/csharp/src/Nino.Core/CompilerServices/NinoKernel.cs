using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nino.Core.CompilerServices
{
    /// <summary>
    /// The write of one value that a kernel of <see cref="NinoKernel"/> calls per element, through a constrained call on
    /// the struct: a direct call on every runtime, inlined where the JIT's budget allows. Only called while the
    /// writer is not deep (<see cref="NinoObject.IsDeep"/>): the kernel checks the depth once per collection. A
    /// non-generic member on a generic interface of its own, never a default member of <see cref="INinoFormatter{T}"/>
    /// (IL2CPP 2022.3 fails on generic default members, #405).
    /// </summary>
    /// <remarks>Experimental (#355 PR 3 probe): generated as <c>__Step</c> of a polymorphic base under
    /// <c>NINO_PROBE_STEPS</c>; nothing in Nino.Core calls it yet.</remarks>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public interface INinoStep<T>
    {
        /// <summary>Writes <paramref name="value"/> (null included) as its formatter's Serialize would, shallow.</summary>
        void Write(T value, ref Writer writer);
    }

    /// <summary>
    /// A dictionary's pair as one step: the key by its formatter, then the value by <typeparamref name="TValueStep"/>
    /// (a <c>Dictionary&lt;int, IShape&gt;</c> is <see cref="NinoKernel.WriteEntries"/> with the base's step for the
    /// value).
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public readonly struct NinoPairStep<TKey, TKeyFormatter, TValue, TValueStep> : INinoStep<KeyValuePair<TKey, TValue>>
        where TKeyFormatter : struct, INinoFormatter<TKey>
        where TValueStep : struct, INinoStep<TValue>
    {
        /// <inheritdoc/>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Write(KeyValuePair<TKey, TValue> value, ref Writer writer)
        {
            default(TKeyFormatter).Serialize(value.Key, ref writer);
            default(TValueStep).Write(value.Value, ref writer);
        }
    }

    /// <summary>
    /// The kernels a collection's elements are written through, each generic over a step
    /// (<see cref="INinoStep{T}"/>) that writes one value: the kernel holds the writer in a local across the loop
    /// and checks the depth once per collection; the step holds what is per type (a polymorphic base's chain of
    /// exact-type compares). Experimental (#355 PR 3 probe): only the benchmark probe and the tests call them.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static class NinoKernel
    {
        /// <summary>
        /// Writes the elements of <paramref name="values"/> (no length: the caller's) through
        /// <typeparamref name="TStep"/>, or, when the writer is deep, each through <typeparamref name="TFormatter"/>.
        /// </summary>
        public static void WriteEach<T, TFormatter, TStep>(ReadOnlySpan<T> values, ref Writer target)
            where TFormatter : struct, INinoFormatter<T>
            where TStep : struct, INinoStep<T>
        {
            // By reference, not by index (see NinoElements.WriteOnLocal).
            ref T item = ref MemoryMarshal.GetReference(values);
            int left = values.Length;
            if (NinoObject.IsDeep(ref target))
            {
                for (; left > 0; left--, item = ref Unsafe.Add(ref item, 1))
                    default(TFormatter).Serialize(item, ref target);
                return;
            }

            // A local the JIT keeps in registers, as the generated element loops do (DispatchEmitter).
            Writer writer = default;
            target.CopyTo(ref writer);
            for (; left > 0; left--, item = ref Unsafe.Add(ref item, 1))
                default(TStep).Write(item, ref writer);
            writer.CopyTo(ref target);
        }

        /// <summary>
        /// Writes the pairs of <paramref name="dictionary"/> (no count: the caller's) through <typeparamref name="TStep"/>
        /// over its entries in place, or, with removed entries or a deep writer, each through the key's and the value's
        /// formatters over its enumerator.
        /// </summary>
        public static void WriteEntries<TKey, TKeyFormatter, TValue, TValueFormatter, TStep>(
            Dictionary<TKey, TValue> dictionary, ref Writer target)
            where TKeyFormatter : struct, INinoFormatter<TKey>
            where TValueFormatter : struct, INinoFormatter<TValue>
            where TStep : struct, INinoStep<KeyValuePair<TKey, TValue>>
        {
            var entries = NinoDictionaryEntries.InUse(dictionary, out int left);
            if (entries == null || NinoObject.IsDeep(ref target))
            {
                WriteEntriesSlow<TKey, TKeyFormatter, TValue, TValueFormatter>(dictionary, ref target);
                return;
            }

            Writer writer = default;
            target.CopyTo(ref writer);
            // A reference and a count, as the generated pair loops (a span's index kept a bounds check per pair).
            ref var entry = ref NinoUnsafe.GetArrayDataReference(entries);
            for (; left > 0; left--, entry = ref Unsafe.Add(ref entry, 1))
                default(TStep).Write(new KeyValuePair<TKey, TValue>(entry.Key, entry.Value), ref writer);
            writer.CopyTo(ref target);
        }

        /// <summary>
        /// <see cref="WriteEntries{TKey,TKeyFormatter,TValue,TValueFormatter,TStep}"/> with the key written by its
        /// formatter and the value by <typeparamref name="TValueStep"/>, without a pair step: a pair step generic over
        /// a class value (<see cref="NinoPairStep{TKey,TKeyFormatter,TValue,TValueStep}"/>) is shared code, whose
        /// Write the kernel reaches through the generic dictionary, an indirect call per pair that is never inlined;
        /// the base's own step is a non-generic struct, called directly and inlined where the budget allows.
        /// </summary>
        public static void WritePairs<TKey, TKeyFormatter, TValue, TValueFormatter, TValueStep>(
            Dictionary<TKey, TValue> dictionary, ref Writer target)
            where TKeyFormatter : struct, INinoFormatter<TKey>
            where TValueFormatter : struct, INinoFormatter<TValue>
            where TValueStep : struct, INinoStep<TValue>
        {
            var entries = NinoDictionaryEntries.InUse(dictionary, out int left);
            if (entries == null || NinoObject.IsDeep(ref target))
            {
                WriteEntriesSlow<TKey, TKeyFormatter, TValue, TValueFormatter>(dictionary, ref target);
                return;
            }

            Writer writer = default;
            target.CopyTo(ref writer);
            ref var entry = ref NinoUnsafe.GetArrayDataReference(entries);
            for (; left > 0; left--, entry = ref Unsafe.Add(ref entry, 1))
            {
                default(TKeyFormatter).Serialize(entry.Key, ref writer);
                default(TValueStep).Write(entry.Value, ref writer);
            }

            writer.CopyTo(ref target);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void WriteEntriesSlow<TKey, TKeyFormatter, TValue, TValueFormatter>(
            Dictionary<TKey, TValue> dictionary, ref Writer target)
            where TKeyFormatter : struct, INinoFormatter<TKey>
            where TValueFormatter : struct, INinoFormatter<TValue>
        {
            foreach (var pair in dictionary)
            {
                default(TKeyFormatter).Serialize(pair.Key, ref target);
                default(TValueFormatter).Serialize(pair.Value, ref target);
            }
        }
    }
}
