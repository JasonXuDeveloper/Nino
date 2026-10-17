using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using static Nino.Generator.Generation.TypeNames;

namespace Nino.Generator.Generation.Emit;

/// <summary>
/// Emits the <c>[assembly: NinoTypeLayout]</c> of a type: its wire layout as metadata, from the same model as its
/// formatter (see <c>NinoTypeLayoutAttribute</c> for the format). Depends only on the model, which is the same in
/// every target framework.
/// </summary>
internal static class LayoutEmitter
{
    private static readonly Regex TypeArguments = new(@"<([^<>]*)>", RegexOptions.CultureInvariant);

    public static string Emit(TypeModel m)
    {
        string format = m.Keyed ? "Keyed" : m.VersionTolerant ? "VersionTolerant" : "Positional";
        string header = m.Kind switch
        {
            FormatterKind.Struct => "None",
            FormatterKind.SealedClass => "NullFlag",
            _ => "TypeId",
        };
        return $"[assembly: {Core}NinoTypeLayout(typeof({OpenType(m)}), {Core}NinoFormat.{format}, " +
               $"{Core}NinoHeader.{header}, {Bool(m.TrackReferences)}, {Bool(m.RawStruct)}, " +
               $"{m.TypeId.ToString(CultureInfo.InvariantCulture)}, \"{Members(m)}\")]\n";
    }

    /// <summary>The members part: <c>name:type:encoding</c> per member, in written order.</summary>
    private static string Members(TypeModel m)
    {
        var sb = new StringBuilder();
        if (m.RawStruct)
            sb.Append(string.Join(";", m.RawFields));
        if (m.CollectionFormatter.Length != 0)
            sb.Append("[elements]::").Append(Plain(m.CollectionFormatter));
        foreach (var member in m.Members)
        {
            if (sb.Length != 0)
                sb.Append(';');
            sb.Append(member.SourceName).Append(':').Append(Plain(member.TypeName)).Append(':');
            if (member.Raw is { } raw)
                sb.Append(raw.IsConstant ? "raw(" + raw.Expression + ")" : "raw");
            else
                sb.Append(Plain(member.Formatter));
            if (m.Keyed && member.FormerKeys.Length != 0)
                sb.Append(":former=").Append(string.Join(",",
                    member.FormerKeys.Select(k => "0x" + k.ToString("X8", CultureInfo.InvariantCulture))));
        }

        return sb.ToString().Replace("\\", "\\\\").Replace("\"", "\\\"");
    }

    /// <summary>A type or formatter as C# source without <c>global::</c>.</summary>
    private static string Plain(string name) => name.Replace("global::", "");

    /// <summary>The type for <c>typeof</c>: generic types as their open definition (<c>Box&lt;&gt;</c>).</summary>
    private static string OpenType(TypeModel m)
    {
        if (!m.IsGeneric)
            return m.TypeName;
        var parameters = m.TypeParameters.ToArray();
        return TypeArguments.Replace(m.TypeName, match =>
        {
            var names = match.Groups[1].Value.Split(',').Select(s => s.Trim()).ToArray();
            return names.All(n => parameters.Contains(n)) ? "<" + new string(',', names.Length - 1) + ">" : match.Value;
        });
    }

    private static string Bool(bool value) => value ? "true" : "false";
}
