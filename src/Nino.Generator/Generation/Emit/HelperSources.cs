namespace Nino.Generator.Generation.Emit;

/// <summary>
/// Formatters for collection types Nino.Core cannot reference (System.Collections.Immutable is not available
/// everywhere, PriorityQueue only exists on .NET 6+), emitted into the assemblies that use them.
/// </summary>
internal static class HelperSources
{
    private const string Core = TypeNames.Core;

    public static string EmitPriorityQueueFormatter()
    {
        return FormatterEmitter.Header + $$"""
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
