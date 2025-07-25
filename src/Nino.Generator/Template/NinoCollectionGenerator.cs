using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Nino.Generator.Filter;
using Nino.Generator.Metadata;

namespace Nino.Generator.Template;

public abstract class NinoCollectionGenerator(
    Compilation compilation,
    List<ITypeSymbol> potentialCollectionSymbols,
    NinoGraph ninoGraph)
    : NinoGenerator(compilation)
{
    protected readonly NinoGraph NinoGraph = ninoGraph;

    protected class Transformer(string name, IFilter filter, Func<ITypeSymbol, Writer, bool> ruleBasedGenerator)
    {
        public readonly string Name = name;
        public readonly IFilter Filter = filter;
        public readonly Func<ITypeSymbol, Writer, bool> RuleBasedGenerator = ruleBasedGenerator;
    }

    protected class Writer(string indent)
    {
        private readonly StringBuilder _sb = new(16_000);

        public void Append(string str)
        {
            _sb.Append(str);
        }

        public void AppendLine()
        {
            _sb.AppendLine();
            _sb.Append(indent);
        }

        public void AppendLine(string str)
        {
            _sb.AppendLine(str);
            _sb.Append(indent);
        }

        public void Clear()
        {
            _sb.Clear();
        }

        public void CopyTo(StringBuilder sb)
        {
            sb.Append(indent);
            sb.Append(_sb);
        }

        public override string ToString()
        {
            return _sb.ToString();
        }
    }

    protected abstract IFilter Selector { get; }
    protected abstract string ClassName { get; }
    protected abstract string OutputFileName { get; }
    protected abstract void PublicMethod(StringBuilder sb, string typeFullName);
    protected abstract List<Transformer>? Transformers { get; }
    private readonly ConcurrentDictionary<ITypeSymbol, bool> _filteredSymbols = new(SymbolEqualityComparer.Default);
    private IFilter? _cachedSelector;

    protected bool ValidFilter(ITypeSymbol symbol)
    {
        _cachedSelector ??= Selector;
        return _filteredSymbols.GetOrAdd(symbol, _cachedSelector.Filter);
    }

    protected static readonly string Inline = "[MethodImpl(MethodImplOptions.AggressiveInlining)]";

    protected static readonly Action<string, Writer, Action<Writer>> IfDirective = (str, writer, action) =>
    {
        writer.Append("#if ");
        writer.AppendLine(str);
        action(writer);
        writer.AppendLine("#endif");
    };

    protected static readonly Action<string, Writer, Action<Writer>, Action<Writer>> IfElseDirective =
        (str, writer, actionIf, actionElse) =>
        {
            writer.Append("#if ");
            writer.AppendLine(str);
            actionIf(writer);
            writer.AppendLine("#else");
            actionElse(writer);
            writer.AppendLine("#endif");
        };

    protected override void Generate(SourceProductionContext spc)
    {
        HashSet<ITypeSymbol> generatedTypes = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        Generate(spc, generatedTypes);
    }

    public void Generate(SourceProductionContext spc, HashSet<ITypeSymbol> generatedTypes)
    {
        if (potentialCollectionSymbols.Count == 0) return;
        var generatedTransformers = Transformers;
        if (generatedTransformers == null || generatedTransformers.Count == 0) return;

        var filteredSymbols = potentialCollectionSymbols
            .Where(symbol =>
            {
                if (!ValidFilter(symbol)) return false;

                if (symbol is INamedTypeSymbol namedTypeSymbol)
                {
                    if (namedTypeSymbol.IsGenericType)
                        return namedTypeSymbol.TypeArguments.All(ValidFilter);

                    return true;
                }

                if (symbol is IArrayTypeSymbol arrayTypeSymbol)
                {
                    return ValidFilter(arrayTypeSymbol.ElementType);
                }

                return false;
            }).ToList();

        var compilation = Compilation;
        var sb = new StringBuilder(1_000_000);
        HashSet<string> addedType = new HashSet<string>();
        Writer writer = new Writer("        ");
        foreach (var symbol in filteredSymbols)
        {
            var type = symbol;
            if (type.IsTupleType && type is INamedTypeSymbol namedTypeSymbol)
            {
                type = namedTypeSymbol.TupleUnderlyingType ?? symbol;
            }

            var typeFullName = type.GetDisplayString();
            if (!addedType.Add(typeFullName)) continue;

            writer.Clear();
            for (var index = 0; index < generatedTransformers.Count; index++)
            {
                var transformer = generatedTransformers[index];
                try
                {
                    if (!transformer.Filter.Filter(type)) continue;
                    var generated = transformer.RuleBasedGenerator(type, writer);
                    if (!generated) continue;
                    sb.AppendLine($"#region {typeFullName} - Generated by transformer {transformer.Name}");
                    PublicMethod(sb, typeFullName);
                    writer.CopyTo(sb);
                    sb.AppendLine();
                    sb.AppendLine("#endregion");
                    sb.AppendLine();
                    generatedTypes.Add(type);
                    break;
                }
                catch (Exception e)
                {
                    throw new AggregateException(
                        $"{OutputFileName} error: Failed to generate code for type {typeFullName} " +
                        $"using transformer[{index}] ({transformer.Name})",
                        e);
                }
            }
        }

        var curNamespace = compilation.AssemblyName!.GetNamespace();

        // generate code
        var code = $$"""
                     // <auto-generated/>

                     using System;
                     using global::Nino.Core;
                     using System.Buffers;
                     using System.Collections.Generic;
                     using System.Collections.Concurrent;
                     using System.Runtime.InteropServices;
                     using System.Runtime.CompilerServices;

                     namespace {{curNamespace}}
                     {
                         public static partial class {{ClassName}}
                         {
                     {{sb}}    }
                     }
                     """;

        spc.AddSource(OutputFileName, code);
    }
}