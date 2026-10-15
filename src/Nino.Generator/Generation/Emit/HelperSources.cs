using System;
using System.Collections.Generic;
using Microsoft.CodeAnalysis;

namespace Nino.Generator.Generation.Emit;

/// <summary>
/// Formatters for collection types Nino.Core cannot reference (System.Collections.Immutable is not available
/// everywhere, PriorityQueue only exists on .NET 6+), emitted into the assemblies that use them.
/// </summary>
internal static class HelperSources
{
    private const string Core = TypeNames.Core;

    private const string PriorityQueue = "System.Collections.Generic.PriorityQueue`2";
    private const string ImmutableArray = "System.Collections.Immutable.ImmutableArray`1";
    private const string ImmutableList = "System.Collections.Immutable.ImmutableList`1";

    /// <summary>
    /// A helper formatter of <see cref="Target"/>: its type parameters are the target's type arguments followed
    /// by their formatters. The generator cannot see its own output, so it resolves these from this table; the
    /// runtime finds them through the <c>[assembly: NinoFormatter]</c> emitted with them.
    /// </summary>
    public sealed class Helper(INamedTypeSymbol target, string name)
    {
        public INamedTypeSymbol Target { get; } = target;

        public string? Bind(INamedTypeSymbol type, Func<ITypeSymbol, string?> element)
        {
            var args = new List<string>();
            var formatters = new List<string>();
            foreach (var arg in type.TypeArguments)
            {
                if (element(arg) is not { } formatter)
                    return null;
                args.Add(TypeNames.Global(arg));
                formatters.Add(formatter);
            }

            return $"global::NinoGen.{name}<{string.Join(", ", args)}, {string.Join(", ", formatters)}>";
        }
    }

    /// <summary>The helper formatters whose target type exists in <paramref name="compilation"/>.</summary>
    public static IReadOnlyList<Helper> Available(Compilation compilation)
    {
        var helpers = new List<Helper>();
        void Add(string target, string name)
        {
            if (compilation.GetTypeByMetadataName(target) is { } symbol)
                helpers.Add(new Helper(symbol, name));
        }

        Add(PriorityQueue, "NinoPriorityQueueFormatter");
        Add(ImmutableArray, "NinoImmutableArrayFormatter");
        Add(ImmutableList, "NinoImmutableListFormatter");
        return helpers;
    }

    /// <summary>Whether the helpers of PriorityQueue / the immutable collections are emitted into this compilation.</summary>
    public static (bool Immutable, bool PriorityQueue) Needed(Compilation compilation) => (
        compilation.GetTypeByMetadataName(ImmutableArray) != null,
        compilation.GetTypeByMetadataName(PriorityQueue) != null);

    public static string EmitPriorityQueueFormatter()
    {
        return FormatterEmitter.Header + $$"""
            [assembly: {{Core}}NinoFormatter(typeof(global::NinoGen.NinoPriorityQueueFormatter<,,,>))]

            namespace NinoGen
            {
                /// <summary><c>PriorityQueue&lt;TElement, TPriority&gt;</c>: count (-1 = null), then (element, priority) pairs.</summary>
                internal struct NinoPriorityQueueFormatter<TElement, TPriority, TElementFormatter, TPriorityFormatter>
                    : {{Core}}INinoFormatter<global::System.Collections.Generic.PriorityQueue<TElement, TPriority>>
                    where TElementFormatter : struct, {{Core}}INinoFormatter<TElement>
                    where TPriorityFormatter : struct, {{Core}}INinoFormatter<TPriority>
                {
                    public void Serialize(global::System.Collections.Generic.PriorityQueue<TElement, TPriority> value, ref {{Core}}Writer writer)
                    {
                        if (value == null)
                        {
                            writer.WriteNullLength();
                            return;
                        }

                        writer.WriteLength(value.Count);
                        foreach (var (element, priority) in value.UnorderedItems)
                        {
                            default(TElementFormatter).Serialize(element, ref writer);
                            default(TPriorityFormatter).Serialize(priority, ref writer);
                        }
                    }

                    public void Deserialize(ref global::System.Collections.Generic.PriorityQueue<TElement, TPriority> value, ref {{Core}}Reader reader)
                    {
                        int length = reader.ReadLength();
                        if (length < 0)
                        {
                            {{Core}}NinoRecycler.Discard(ref value);
                            return;
                        }

                        var queue = value;
                        if (queue == null)
                        {
                            queue = new global::System.Collections.Generic.PriorityQueue<TElement, TPriority>(length);
                            value = queue;
                        }
                        else
                        {
                            if ({{Core}}NinoRecycler<TElement>.IsRegistered)
                            {
                                foreach (var (element, _) in queue.UnorderedItems)
                                    {{Core}}NinoRecycler<TElement>.Recycle(element);
                            }

                            queue.Clear();
                        }

                        for (int i = 0; i < length; i++)
                        {
                            TElement element = default;
                            TPriority priority = default;
                            default(TElementFormatter).Deserialize(ref element, ref reader);
                            default(TPriorityFormatter).Deserialize(ref priority, ref reader);
                            queue.Enqueue(element, priority);
                        }
                    }
                }
            }

            """;
    }

    public static string EmitImmutableFormatters()
    {
        return FormatterEmitter.Header + $$"""
            [assembly: {{Core}}NinoFormatter(typeof(global::NinoGen.NinoImmutableArrayFormatter<,>))]
            [assembly: {{Core}}NinoFormatter(typeof(global::NinoGen.NinoImmutableListFormatter<,>))]

            namespace NinoGen
            {
                /// <summary><c>ImmutableArray&lt;T&gt;</c>: length (-1 = default), then the elements.</summary>
                internal struct NinoImmutableArrayFormatter<T, TFormatter> : {{Core}}INinoFormatter<global::System.Collections.Immutable.ImmutableArray<T>>
                    where TFormatter : struct, {{Core}}INinoFormatter<T>
                {
                    public void Serialize(global::System.Collections.Immutable.ImmutableArray<T> value, ref {{Core}}Writer writer)
                    {
                        if (value.IsDefault)
                        {
                            writer.WriteNullLength();
                            return;
                        }

                        writer.WriteLength(value.Length);
                        foreach (var item in value)
                            default(TFormatter).Serialize(item, ref writer);
                    }

                    public void Deserialize(ref global::System.Collections.Immutable.ImmutableArray<T> value, ref {{Core}}Reader reader)
                    {
                        int length = reader.ReadLength();
                        if (length < 0)
                        {
                            value = default;
                            return;
                        }

                        var builder = global::System.Collections.Immutable.ImmutableArray.CreateBuilder<T>(length);
                        for (int i = 0; i < length; i++)
                        {
                            T item = default;
                            default(TFormatter).Deserialize(ref item, ref reader);
                            builder.Add(item);
                        }

                        value = builder.MoveToImmutable();
                    }
                }

                /// <summary><c>ImmutableList&lt;T&gt;</c>: count (-1 = null), then the elements.</summary>
                internal struct NinoImmutableListFormatter<T, TFormatter> : {{Core}}INinoFormatter<global::System.Collections.Immutable.ImmutableList<T>>
                    where TFormatter : struct, {{Core}}INinoFormatter<T>
                {
                    public void Serialize(global::System.Collections.Immutable.ImmutableList<T> value, ref {{Core}}Writer writer)
                    {
                        if (value == null)
                        {
                            writer.WriteNullLength();
                            return;
                        }

                        writer.WriteLength(value.Count);
                        foreach (var item in value)
                            default(TFormatter).Serialize(item, ref writer);
                    }

                    public void Deserialize(ref global::System.Collections.Immutable.ImmutableList<T> value, ref {{Core}}Reader reader)
                    {
                        int length = reader.ReadLength();
                        if (length < 0)
                        {
                            value = null;
                            return;
                        }

                        var builder = global::System.Collections.Immutable.ImmutableList.CreateBuilder<T>();
                        for (int i = 0; i < length; i++)
                        {
                            T item = default;
                            default(TFormatter).Deserialize(ref item, ref reader);
                            builder.Add(item);
                        }

                        value = builder.ToImmutable();
                    }
                }
            }

            """;
    }
}
