using System.Globalization;
using System.Linq;
using System.Text;
using static Nino.Generator.Generation.TypeNames;

namespace Nino.Generator.Generation.Emit;

/// <summary>
/// Emits the <c>[assembly: NinoTypeLayout]</c> of a type: its wire layout as metadata, from the same model as its
/// formatter (see <c>NinoTypeLayoutAttribute</c> for the format). Depends only on the model, which is the same in
/// every target framework. Whether a class body can be deferred (read after its instance is in its slot, see
/// <c>NinoDepth</c>) is published too, as <c>Deferrable</c>, where the referenced Nino.Core has that property: a reader
/// in another language needs it to read data nested deeper than <c>NinoDepth.Inline</c>.
/// </summary>
internal static class LayoutEmitter
{
    public static string Emit(TypeModel m)
    {
        string format = m.Keyed ? "Keyed" : m.VersionTolerant ? "VersionTolerant" : "Positional";
        string header = m.Kind switch
        {
            FormatterKind.Struct => "None",
            FormatterKind.SealedClass => "NullFlag",
            _ => "TypeId",
        };
        return $"[assembly: {Core}NinoTypeLayout(typeof({m.OpenTypeName}), {Core}NinoFormat.{format}, " +
               $"{Core}NinoHeader.{header}, {Bool(m.TrackReferences)}, {Bool(m.RawStruct)}, " +
               $"{m.TypeId.ToString(CultureInfo.InvariantCulture)}, \"{Members(m)}\"" +
               (m.PublishesDeferrable ? $", Deferrable = {Bool(m.Depth.Deferrable)}" : "") + ")]\n";
    }

    /// <summary>The members part: <c>name:type:encoding</c> per member, in written order.</summary>
    private static string Members(TypeModel m)
    {
        var sb = new StringBuilder();
        if (m.RawStruct)
            sb.Append(m.RawLayout);
        if (m.Collection is { } collection)
            sb.Append("[elements]::").Append(collection.Encoding);
        foreach (var member in m.Members)
        {
            if (sb.Length != 0)
                sb.Append(';');
            sb.Append(member.SourceName).Append(':').Append(Plain(member.TypeName)).Append(':')
                .Append(member.Encoding);
            if (m.Keyed)
                sb.Append(":key=").Append(Hex(member.Key));
            if (m.Keyed && member.FormerKeys.Length != 0)
                sb.Append(":former=").Append(string.Join(",",
                    member.FormerKeys.Select(Hex)));
        }

        return EscapeString(sb.ToString());
    }

    private static string Hex(int key) => "0x" + key.ToString("X8", CultureInfo.InvariantCulture);

    private static string Bool(bool value) => value ? "true" : "false";
}
