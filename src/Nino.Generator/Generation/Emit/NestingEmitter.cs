using System.Collections.Generic;
using static Nino.Generator.Generation.TypeNames;

namespace Nino.Generator.Generation.Emit;

/// <summary>
/// Emits the nesting formatters of the types that hold themselves through collections
/// (<see cref="FormatterResolver.NestingFormattersNamedIn"/>): each counts one level, as Nino.Core's <c>NestingFormatter</c> does, and
/// calls the type's formatter, whose elements name the struct again.
/// </summary>
internal static class NestingEmitter
{
    public static string Emit(IEnumerable<NestingModel> formatters, bool fileScoped)
    {
        var w = new CodeWriter();
        bool first = true;
        foreach (var f in formatters)
        {
            if (!first)
                w.Line();
            first = false;
            w.Line("/// <summary>A level of a type that holds itself: counted, then its formatter.</summary>");
            w.Line(HiddenAttribute);
            w.Open($"internal struct {f.Name} : {Core}INinoFormatter<{f.TypeName}>");
            w.Open(Signature.Serialize(f.TypeName));
            w.Line($"writer.EnterStruct(typeof({f.TypeName}));");
            w.Line($"default({f.Body}).Serialize(value, ref writer);");
            w.Line("writer.ExitStruct();");
            w.Close();
            w.Line();
            w.Open(Signature.Deserialize(f.TypeName));
            w.Line($"reader.EnterStruct(typeof({f.TypeName}));");
            w.Line($"default({f.Body}).Deserialize(ref reader, ref value);");
            w.Line("reader.ExitStruct();");
            w.Close();
            w.Close();
        }

        var aliases = new List<string>();
        string body = GeneratedCode.WithAliases(w.Finish(), null, aliases);
        return GeneratedCode.File("", aliases, body, fileScoped);
    }
}
