using System.Globalization;
using System.Linq;
using Nino.Generator.Common;
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
    public static string Emit(TypeModel m) =>
        $"[assembly: {Core}NinoTypeLayout(typeof({m.OpenTypeName}), {Core}NinoFormat.{m.Format}, " +
        $"{Core}NinoHeader.{LayoutEntry.HeaderNames[Header(m)]}, {Bool(m.TrackReferences)}, {Bool(m.RawStruct)}, " +
        $"{m.TypeId.ToString(CultureInfo.InvariantCulture)}, \"{EscapeString(Members(m))}\"" +
        (m.PublishesDeferrable ? $", Deferrable = {Bool(m.Depth.Deferrable)}" : "") + ")]\n\n";

    /// <summary>
    /// The layout <see cref="Emit"/> publishes, as a layout baseline holds it (the analyzer compares it with the
    /// baseline's section of <paramref name="key"/>).
    /// </summary>
    public static LayoutEntry Entry(TypeModel m, string key) =>
        LayoutEntry.FromLayout(key, (int)m.Format, Header(m), m.TrackReferences, m.RawStruct, m.TypeId,
            m.PublishesDeferrable ? m.Depth.Deferrable : null, Members(m));

    /// <summary>
    /// Whether the layout <see cref="Emit"/> publishes is already in <paramref name="section"/>. Compare the model
    /// directly so the unchanged case does not have to format the member layout.
    /// </summary>
    public static bool Matches(TypeModel m, LayoutEntry section)
    {
        if (!section.MatchesHeader((int)m.Format, Header(m), m.TrackReferences, m.TypeId,
                m.PublishesDeferrable ? m.Depth.Deferrable : null) ||
            section.Raw != (m.RawStruct ? m.RawLayout : null) || section.Elements != m.Collection?.Encoding ||
            section.Members.Count != m.Members.Length)
            return false;

        for (int i = 0; i < m.Members.Length; i++)
        {
            var member = m.Members[i];
            var saved = section.Members[i];
            int formerCount = m.Keyed ? member.FormerKeys.Length : 0;
            if (saved.Name != member.SourceName || saved.Type != Plain(member.TypeName) ||
                saved.Encoding != member.Encoding || saved.Key != (m.Keyed ? member.Key : (int?)null) ||
                saved.Former.Count != formerCount)
                return false;
            for (int j = 0; j < formerCount; j++)
                if (saved.Former[j] != member.FormerKeys[j])
                    return false;
        }

        return true;
    }

    /// <summary>What is written in front of the body (<c>NinoHeader</c>): nothing, a presence byte or a type id.</summary>
    private static int Header(TypeModel m) => m.Kind switch
    {
        FormatterKind.Struct => 0,
        FormatterKind.SealedClass => 1,
        _ => 2,
    };

    /// <summary>The members part: <c>name:type:encoding</c> per member, in written order.</summary>
    private static string Members(TypeModel m)
    {
        // Raw structs have no member models or collection body. Their layout text is already complete.
        if (m.RawStruct)
            return m.RawLayout;

        if (m.Members.Length == 0 && m.Collection == null)
            return "";

        var sb = GeneratedCode.RentScratch(64);
        if (m.Collection is { } collection)
            sb.Append("[elements]::").Append(collection.Encoding);
        foreach (var member in m.Members)
        {
            if (sb.Length != 0)
                sb.Append(';');
            sb.Append(member.SourceName).Append(':').Append(Plain(member.TypeName)).Append(':')
                .Append(member.Encoding);
            if (m.Keyed)
                sb.Append(":key=").Append(LayoutBaseline.Hex(member.Key));
            if (m.Keyed && member.FormerKeys.Length != 0)
                sb.Append(":former=").Append(string.Join(",",
                    member.FormerKeys.Select(LayoutBaseline.Hex)));
        }

        string text = sb.ToString();
        GeneratedCode.ReturnScratch(sb);
        return text;
    }

    private static string Bool(bool value) => value ? "true" : "false";
}
