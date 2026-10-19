using static Nino.Generator.Generation.TypeNames;

namespace Nino.Generator.Generation.Emit;

/// <summary>The signatures of the formatter methods generated code implements, for a value of type <c>t</c>.</summary>
internal static class Signature
{
    public static string WriteParameters(string t) => $"{t} value, ref {Core}Writer writer";

    public static string ReadParameters(string t) => $"ref {t} value, ref {Core}Reader reader";

    public static string Serialize(string t) => $"public void Serialize({WriteParameters(t)})";

    public static string Deserialize(string t) => $"public void Deserialize({ReadParameters(t)})";

    public static string WriteBody(string t) => $"public void WriteBody({WriteParameters(t)})";

    public static string ReadBody(string t) => $"public void ReadBody({ReadParameters(t)})";
}
