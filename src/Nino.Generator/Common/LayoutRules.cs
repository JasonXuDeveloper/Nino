using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
#if NINO_GENERATOR
using Nino.Generator.Generation;
#else
using Nino.Core.Internal;
#endif

namespace Nino.Generator.Common;

// Compiled into the analyzer, the code fixes and nino-gen (src/Nino.Codegen): one set of rules for the IDE, the build
// and the command line.

/// <summary>How bad a change to a layout is.</summary>
internal enum LayoutSeverity
{
    /// <summary>Data written on one side of the change cannot be read on the other (NINO034).</summary>
    Breaking,

    /// <summary>Data is read, but a member's value is lost without an error (NINO035).</summary>
    SilentLoss,

    /// <summary>Compatible, with something to know (NINO036).</summary>
    Info,
}

/// <summary>Which readers a change breaks.</summary>
[Flags]
internal enum LayoutDirections
{
    None = 0,

    /// <summary>This build reading data written before the change (old saves, caches).</summary>
    NewReadsOld = 1,

    /// <summary>Builds before the change reading data this one writes (clients not updated, old nodes in a rollout).</summary>
    OldReadsNew = 2,

    Both = NewReadsOld | OldReadsNew,
}

/// <summary>The fix a finding offers besides accepting the change into the baseline.</summary>
internal enum LayoutFix
{
    /// <summary>Only accepting it (rewriting the type's section).</summary>
    Accept,

    /// <summary>Adding <c>[NinoFormerName(argument)]</c> to the member, or the type when the finding has no member.</summary>
    FormerName,

    /// <summary>Making the class sealed again.</summary>
    AddSealed,

    /// <summary>Making the class not sealed again.</summary>
    RemoveSealed,

    /// <summary>Moving the member to the end of its type.</summary>
    MoveToEnd,
}

/// <summary>One change between a type's section in the baseline and its layout now.</summary>
internal sealed class LayoutFinding
{
    public LayoutFinding(LayoutSeverity severity, string? member, string change, string fix,
        LayoutFix fixKind = LayoutFix.Accept, string? argument = null)
    {
        Severity = severity;
        Member = member;
        Change = change;
        Fix = fix;
        FixKind = fixKind;
        Argument = argument;
    }

    public LayoutSeverity Severity { get; }

    /// <summary>The member of the layout now that changed, by C# name; null when the type itself did, or the member is gone.</summary>
    public string? Member { get; }

    /// <summary>What changed and what that does to which readers, a clause after the type or member's name.</summary>
    public string Change { get; }

    /// <summary>What to do.</summary>
    public string Fix { get; }

    public LayoutFix FixKind { get; }

    /// <summary>The former name of <see cref="LayoutFix.FormerName"/>.</summary>
    public string? Argument { get; }
}

/// <summary>
/// The rules of the layout baseline: which types it holds (<see cref="Scope"/>), which type of the code a section is
/// about (<see cref="Pair"/>), and what a change between the two does to data on either side of it
/// (<see cref="Compare"/>). Text in, findings out: the analyzer feeds it the layouts of its models, nino-gen those
/// published in a built assembly.
/// </summary>
internal static class LayoutRules
{
    private const string Accept =
        "if that is intended, accept the change into the layout baseline (code fix, or nino-gen layouts)";

    /// <summary>The words for the readers <paramref name="broken"/> names.</summary>
    public static string Readers(LayoutDirections broken) => broken switch
    {
        LayoutDirections.Both =>
            "this build cannot read data written before the change, and builds before it cannot read data written after it",
        LayoutDirections.NewReadsOld => "this build cannot read data written before the change",
        LayoutDirections.OldReadsNew => "builds before the change cannot read data written after it",
        _ => "",
    };

    /// <summary>
    /// The changes from <paramref name="old"/> (a section of the baseline) to <paramref name="now"/> (the type's
    /// layout now). <paramref name="idWritten"/>: the type id is part of the bytes (a <c>TypeId</c> header, or a
    /// subtype written in its base's slot). <paramref name="sameType"/> tells whether a type name in an old encoding
    /// and one in the new name the same type (renamed, its id kept); names that are equal always do.
    /// <paramref name="rawSection"/> tells whether a type name has a section of its own in the baseline for its raw
    /// memory: a member holding it is not reported for what that section reports.
    /// </summary>
    public static List<LayoutFinding> Compare(LayoutEntry old, LayoutEntry now, bool idWritten,
        Func<string, string, bool>? sameType = null, Func<string, bool>? rawSection = null)
    {
        var findings = new List<LayoutFinding>();
        var m = new Matcher(sameType ?? (static (_, _) => false), rawSection ?? (static _ => false));
        if (old.Format != now.Format)
            findings.Add(Breaking(null, $"changed its format from {old.Format} to {now.Format}", Accept));
        if (old.TrackReferences != now.TrackReferences)
            findings.Add(Breaking(null,
                now.TrackReferences ? "now tracks references (trackReferences: true), which adds a reference header to every value"
                    : "no longer tracks references (trackReferences: false), which drops the reference header of every value",
                Accept));
        if (old.Header != now.Header)
            findings.Add(Header(old, now));
        if (old.Id != 0 && now.Id != 0 && old.Id != now.Id &&
            (idWritten || old.Header == LayoutEntry.HeaderNames[2] || now.Header == LayoutEntry.HeaderNames[2]))
            findings.Add(Id(old, now));
        if (old.Raw != now.Raw)
            findings.Add(Breaking(null, old.Raw == null ? $"is now written as raw memory ({now.Raw})"
                : now.Raw == null ? $"is no longer written as raw memory ({old.Raw})"
                : $"changed its raw layout from {old.Raw} to {now.Raw}", Accept));
        if (old.Deferrable is { } was && now.Deferrable is { } deferrable && was != deferrable)
            findings.Add(new LayoutFinding(LayoutSeverity.Info, null,
                (deferrable ? "can now be deferred" : "can no longer be deferred") +
                " when nested deeper than 128 bodies (its deferrable changed, by its construction or by becoming abstract), " +
                "so data nesting it that deep is laid out differently on either side of the change",
                "if no data nests it that deep, accept the change into the layout baseline (code fix, or nino-gen layouts)"));
        if (!m.Same(old.Elements ?? "", now.Elements ?? ""))
            findings.Add(Breaking(null,
                $"changed what its elements are written as from {old.Elements ?? "none"} to {now.Elements ?? "none"}",
                Accept));
        if (old.Format != now.Format || old.Raw != null || now.Raw != null)
            return findings;
        if (now.IsKeyed)
            Keyed(old, now, m, findings);
        else if (now.IsVersionTolerant)
            Tolerant(old, now, m, findings);
        else
            Positional(old, now, m, findings);
        return findings;
    }

    /// <summary>The finding for a type whose section has no type in the code any more.</summary>
    public static LayoutFinding Removed(LayoutEntry old) => Breaking(null,
        "is no longer a [NinoType] of this assembly",
        $"if it was renamed or moved, add [NinoFormerName(\"{old.Key}\")] to it; if it was removed, accept the change into the layout baseline (code fix, or nino-gen layouts)");

    private static LayoutFinding Breaking(string? member, string change, string fix,
        LayoutDirections broken = LayoutDirections.Both, LayoutFix kind = LayoutFix.Accept, string? argument = null) =>
        new(LayoutSeverity.Breaking, member, change + ", so " + Readers(broken), fix, kind, argument);

    private static LayoutFinding Header(LayoutEntry old, LayoutEntry now)
    {
        string presence = LayoutEntry.HeaderNames[1], typeId = LayoutEntry.HeaderNames[2];
        if (old.Header == presence && now.Header == typeId)
            return Breaking(null,
                "is no longer sealed: a 4-byte type id is written in front of it where a presence byte was",
                "make it sealed again", kind: LayoutFix.AddSealed);
        if (old.Header == typeId && now.Header == presence)
            return Breaking(null,
                "is now sealed: a presence byte is written in front of it where a 4-byte type id was",
                "remove sealed", kind: LayoutFix.RemoveSealed);
        return Breaking(null, $"changed what is written in front of it from {old.Header} to {now.Header} (a struct became a class, or a class a struct)",
            Accept);
    }

    private static LayoutFinding Id(LayoutEntry old, LayoutEntry now)
    {
        string change = $"changed its type id from {LayoutBaseline.Hex(old.Id)} to {LayoutBaseline.Hex(now.Id)}";
        // The old id is the old name's: renamed or moved, and the old name brings the id back.
        if (old.Key != now.Key && NinoHash.Compute(old.Key) == old.Id)
            return Breaking(null, change + $" (it was '{old.Key}')",
                $"add [NinoFormerName(\"{old.Key}\")] to keep the id", kind: LayoutFix.FormerName,
                argument: old.Key);
        return Breaking(null, change + " (its name or [NinoFormerName] changed)",
            "restore the [NinoFormerName] its id was computed from, or " + Accept);
    }

    /// <summary>
    /// Keyed: members are found by key. Added members are compatible; a removed one is an
    /// <see cref="LayoutSeverity.Info"/>, unless an added member with the same encoding looks like it renamed without
    /// <c>[NinoFormerName]</c> (silent loss both ways); a member renamed with its former name is read from old data,
    /// but old code does not know its new key.
    /// </summary>
    private static void Keyed(LayoutEntry old, LayoutEntry now, Matcher m,
        List<LayoutFinding> findings)
    {
        var oldKeys = new HashSet<int>(old.Members.Where(a => a.Key != null).Select(a => a.Key!.Value));
        var added = now.Members.Where(a => a.Key is { } k && !oldKeys.Contains(k) && !a.Former.Any(oldKeys.Contains))
            .ToList();
        for (int index = 0; index < old.Members.Count; index++)
        {
            var member = old.Members[index];
            if (member.Key is not { } key)
                continue;
            string keyText = LayoutBaseline.Hex(key);
            var same = now.Members.FirstOrDefault(a => a.Key == key);
            if (same != null)
            {
                if (!m.Same(member, same))
                    findings.Add(Breaking(same.Name,
                        $"changed what key {keyText} is written as from {member.Encoding} to {same.Encoding}",
                        "give the member a new name (a new key) and keep the old one, or " + Accept));
                continue;
            }

            var renamed = now.Members.FirstOrDefault(a => a.Former.Contains(key));
            if (renamed != null)
            {
                if (!m.Same(member, renamed))
                    findings.Add(Breaking(renamed.Name,
                        $"is read from '{member.Name}' (key {keyText}, its [NinoFormerName]), but changed what it is written as from {member.Encoding} to {renamed.Encoding}",
                        Accept));
                else
                    findings.Add(new LayoutFinding(LayoutSeverity.Info, renamed.Name,
                        $"was renamed from '{member.Name}': this build reads it from data written before the change, but builds before it do not know its new key and read their default for '{member.Name}'",
                        "in a rolling deployment, deploy the builds that read the new name before any that writes it, then accept the change into the layout baseline (code fix, or nino-gen layouts)"));
                continue;
            }

            // An added member written the same way, at the same place if one is: renamed without its former name.
            var match = added.FirstOrDefault(a => now.Members.IndexOf(a) == index && m.Same(member, a)) ??
                        (added.Count(a => m.Same(member, a)) == 1
                            ? added.First(a => m.Same(member, a))
                            : null);
            if (match != null)
            {
                added.Remove(match);
                findings.Add(new LayoutFinding(LayoutSeverity.SilentLoss, match.Name,
                    $"looks like '{member.Name}' renamed: its key is new, so this build reads its default value from data written before the change, and builds before it drop the value it writes, without an error",
                    $"add [NinoFormerName(\"{member.Name}\")] to it", LayoutFix.FormerName, member.Name));
                continue;
            }

            findings.Add(new LayoutFinding(LayoutSeverity.Info, null,
                $"no longer has keyed member '{member.Name}' (key {keyText}): this build skips it in data written before the change, and builds before it read their default for it",
                "accept the change into the layout baseline (code fix, or nino-gen layouts)"));
        }
    }

    /// <summary>
    /// Version tolerant: members are found by position in one region, so members appended at the end are skipped by
    /// old readers and left at their defaults by new ones; anything else moves every member after it.
    /// </summary>
    private static void Tolerant(LayoutEntry old, LayoutEntry now, Matcher m,
        List<LayoutFinding> findings)
    {
        if (now.Members.Count >= old.Members.Count &&
            old.Members.Select((a, i) => m.Same(a, now.Members[i])).All(x => x))
            return;

        // Match members by name, then a renamed one by its place and encoding.
        var match = new int[old.Members.Count];
        for (int i = 0; i < old.Members.Count; i++)
            match[i] = now.Members.FindIndex(a => a.Name == old.Members[i].Name);
        for (int i = 0; i < old.Members.Count; i++)
        {
            if (match[i] < 0 && i < now.Members.Count && !match.Contains(i) &&
                old.Members.All(a => a.Name != now.Members[i].Name) &&
                m.Same(old.Members[i], now.Members[i]))
                match[i] = i;
        }

        int before = findings.Count;
        const string Order = "version-tolerant members are read by position: ";
        for (int i = 0; i < old.Members.Count; i++)
        {
            if (match[i] < 0)
                findings.Add(Breaking(null,
                    $"no longer has member '{old.Members[i].Name}' ({Order}every member after it is read as the one before it)",
                    $"keep the member (mark it [NinoIgnore] only once no data holds it), or {Accept}"));
            else if (!m.Same(old.Members[i], now.Members[match[i]]))
                findings.Add(Breaking(now.Members[match[i]].Name,
                    $"changed what it is written as from {old.Members[i].Encoding} to {now.Members[match[i]].Encoding}",
                    $"add a new member at the end instead, or {Accept}"));
        }

        int last = -1;
        for (int i = 0; i < old.Members.Count; i++)
        {
            if (match[i] < 0)
                continue;
            if (match[i] < last)
            {
                findings.Add(Breaking(now.Members[match[i]].Name,
                    $"moved before '{now.Members[last].Name}' ({Order}the two are read as each other)",
                    $"move it back, or {Accept}"));
                break;
            }

            last = match[i];
        }

        for (int j = 0; j < now.Members.Count && j < last; j++)
        {
            if (match.Contains(j))
                continue;
            int next = match.Where(x => x > j).Min();
            findings.Add(Breaking(now.Members[j].Name,
                $"was inserted before '{now.Members[next].Name}' ({Order}members after it are read as the ones before them)",
                "move it to the end of the type", kind: LayoutFix.MoveToEnd));
        }

        if (findings.Count == before)
            findings.Add(Breaking(null, "changed its members", Accept));
    }

    /// <summary>Positional: the members are the bytes in order, with nothing to tell old from new.</summary>
    private static void Positional(LayoutEntry old, LayoutEntry now, Matcher m,
        List<LayoutFinding> findings)
    {
        int count = Math.Min(old.Members.Count, now.Members.Count);
        for (int i = 0; i < count; i++)
        {
            if (m.Same(old.Members[i], now.Members[i]))
                continue;
            findings.Add(Breaking(now.Members[i].Name,
                $"is written as {now.Members[i].Encoding} where '{old.Members[i].Name}' was written as {old.Members[i].Encoding} (a positional type, held by stored data, writes its members in order with nothing to tell them apart)",
                $"make the type version tolerant or keyed if it changes, or {Accept}"));
            return;
        }

        if (old.Members.Count == now.Members.Count)
            return;
        findings.Add(old.Members.Count > now.Members.Count
            ? Breaking(null,
                $"no longer has member '{old.Members[count].Name}' (a positional type, held by stored data, writes its members in order with nothing to tell old from new)",
                $"make the type version tolerant or keyed if it changes, or {Accept}")
            : Breaking(now.Members[count].Name,
                "was added (a positional type, held by stored data, writes its members in order with nothing to tell old from new)",
                $"make the type version tolerant or keyed if it changes, or {Accept}"));
    }

    /// <summary>
    /// Compares encodings: equal, or equal but for type names <see cref="SameType"/> says name one type (renamed, its
    /// id kept). Types written by their own layout (<c>type(T)</c>) are compared by name only, and the raw memory of a
    /// type with a section of its own (<see cref="RawSection"/>) is left to that section: each change is reported
    /// once, by the type that made it.
    /// </summary>
    private readonly struct Matcher
    {
        public Matcher(Func<string, string, bool> sameType, Func<string, bool> rawSection) =>
            (SameType, RawSection) = (sameType, rawSection);

        public Func<string, string, bool> SameType { get; }
        public Func<string, bool> RawSection { get; }

        /// <summary>Whether two members are written the same way.</summary>
        public bool Same(LayoutMember old, LayoutMember now)
        {
            if (Same(old.Encoding, now.Encoding))
                return true;
            // A raw struct with a section of its own: its changes are reported there.
            var self = this;
            return Same(old.Type, now.Type) && Tokens(old.Type).Where(IsName).Any(n => self.RawSection(Dotted(n))) &&
                   Same(WithoutRaw(old.Encoding), WithoutRaw(now.Encoding));
        }

        /// <summary>Whether two encodings describe the same bytes.</summary>
        public bool Same(string old, string now)
        {
            if (old == now)
                return true;
            var a = Tokens(old);
            var b = Tokens(now);
            if (a.Count != b.Count)
                return false;
            for (int i = 0; i < a.Count; i++)
            {
                if (a[i] != b[i] && !(IsName(a[i]) && IsName(b[i]) && SameType(a[i], b[i])))
                    return false;
            }

            return true;
        }
    }

    /// <summary><paramref name="encoding"/> with every raw struct's size and fields left out (<c>raw(8){...}</c> → <c>raw</c>).</summary>
    private static string WithoutRaw(string encoding)
    {
        var sb = new StringBuilder(encoding.Length);
        for (int i = 0; i < encoding.Length; i++)
        {
            if (string.CompareOrdinal(encoding, i, "raw(", 0, 4) == 0 && (i == 0 || !NameChar(encoding[i - 1])))
            {
                sb.Append("raw");
                i = encoding.IndexOf(')', i);
                if (i + 1 < encoding.Length && encoding[i + 1] == '{')
                {
                    int depth = 0;
                    for (i++; i < encoding.Length; i++)
                    {
                        if (encoding[i] == '{')
                            depth++;
                        else if (encoding[i] == '}' && --depth == 0)
                            break;
                    }
                }

                continue;
            }

            sb.Append(encoding[i]);
        }

        return sb.ToString();
    }

    /// <summary>
    /// <paramref name="text"/> as names (letters, digits, <c>_</c>, <c>.</c> and <c>+</c>: a namespace-qualified
    /// type name without its type arguments) and the characters between them.
    /// </summary>
    private static List<string> Tokens(string text)
    {
        var tokens = new List<string>();
        int i = 0;
        while (i < text.Length)
        {
            int start = i;
            if (NameChar(text[i]))
            {
                while (i < text.Length && NameChar(text[i]))
                    i++;
            }
            else
                i++;
            tokens.Add(text.Substring(start, i - start));
        }

        return tokens;
    }

    private static bool NameChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '.' or '+';

    private static bool IsName(string token) => token.Length != 0 && NameChar(token[0]) && !char.IsDigit(token[0]);

    /// <summary>
    /// A type's name as encodings and member types spell it, without type arguments: <c>Ns.Outer+Inner&lt;T&gt;</c>
    /// (a <see cref="LayoutEntry.Key"/>) and <c>Ns.Outer&lt;int&gt;.Inner</c> are both <c>Ns.Outer.Inner</c>.
    /// </summary>
    public static string Dotted(string name)
    {
        var sb = new StringBuilder(name.Length);
        int depth = 0;
        foreach (char c in name)
        {
            if (c == '<')
                depth++;
            else if (c == '>')
                depth--;
            else if (depth == 0)
                sb.Append(c == '+' ? '.' : c);
        }

        return sb.ToString();
    }

    /// <summary>The names a layout's text mentions (<see cref="Dotted"/> form): its members' types and encodings.</summary>
    private static IEnumerable<string> Mentions(LayoutEntry entry)
    {
        var texts = entry.Members.SelectMany(m => new[] { m.Type, m.Encoding }).ToList();
        if (entry.Elements != null)
            texts.Add(entry.Elements);
        if (entry.Raw != null)
            texts.Add(entry.Raw);
        return texts.SelectMany(Tokens).Where(IsName).Select(Dotted);
    }

    /// <summary>
    /// The keys of the types a baseline holds: every version-tolerant or keyed type, and every type their layouts
    /// reach (a member's type or encoding names it, at any depth, or it derives from one reached:
    /// <paramref name="ancestors"/> gives the keys of a type's <c>[NinoType]</c> base types and interfaces). A
    /// positional type nothing stored reaches is left out: its bytes do not outlive a build.
    /// </summary>
    public static HashSet<string> Scope(IReadOnlyCollection<LayoutEntry> layouts,
        Func<LayoutEntry, IEnumerable<string>> ancestors)
    {
        var byName = new Dictionary<string, List<LayoutEntry>>(StringComparer.Ordinal);
        var subtypes = new Dictionary<string, List<LayoutEntry>>(StringComparer.Ordinal);
        foreach (var layout in layouts)
        {
            Add(byName, Dotted(layout.Key), layout);
            foreach (string ancestor in ancestors(layout))
                Add(subtypes, ancestor, layout);
        }

        var scope = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<LayoutEntry>(layouts.Where(l => l.IsKeyed || l.IsVersionTolerant));
        while (pending.Count != 0)
        {
            var layout = pending.Pop();
            if (!scope.Add(layout.Key))
                continue;
            var reached = Mentions(layout).SelectMany(n => byName.TryGetValue(n, out var list) ? list : Enumerable.Empty<LayoutEntry>());
            if (subtypes.TryGetValue(layout.Key, out var derived))
                reached = reached.Concat(derived);
            foreach (var next in reached)
            {
                if (!scope.Contains(next.Key))
                    pending.Push(next);
            }
        }

        return scope;
    }

    private static void Add(Dictionary<string, List<LayoutEntry>> map, string key, LayoutEntry entry)
    {
        if (!map.TryGetValue(key, out var list))
            map.Add(key, list = new List<LayoutEntry>());
        list.Add(entry);
    }

    /// <summary>A type's name without namespace, containing types and type arguments, with its arity: <c>Inner`2</c>.</summary>
    public static string SimpleName(string key)
    {
        string dotted = Dotted(key);
        int dot = dotted.LastIndexOf('.');
        int open = key.IndexOf('<');
        int arity = open < 0 ? 0 : LayoutEntry.Split(key.Substring(open + 1, key.Length - open - 2), ',').Count;
        return dotted.Substring(dot + 1) + "`" + arity;
    }

    /// <summary>
    /// The sections of <paramref name="missing"/> (whose keys no type has now) that are about one of
    /// <paramref name="added"/> (types of the code without a section), by the added type's key: the one with the
    /// same type id (a <c>[NinoFormerName]</c> kept it), else the one with the same <see cref="SimpleName"/> when
    /// each side has only one of that name (moved to another namespace or type).
    /// </summary>
    public static Dictionary<string, LayoutEntry> Pair(IEnumerable<LayoutEntry> missing,
        IEnumerable<KeyValuePair<string, int>> added)
    {
        var pairs = new Dictionary<string, LayoutEntry>(StringComparer.Ordinal);
        var olds = missing.ToList();
        var news = added.ToList();
        foreach (var type in news.ToList())
        {
            var old = type.Value == 0 ? null : olds.FirstOrDefault(o => o.Id == type.Value);
            if (old == null)
                continue;
            pairs[type.Key] = old;
            olds.Remove(old);
            news.Remove(type);
        }

        foreach (var group in news.GroupBy(n => SimpleName(n.Key)))
        {
            var candidates = olds.Where(o => SimpleName(o.Key) == group.Key).ToList();
            if (group.Count() == 1 && candidates.Count == 1)
                pairs[group.First().Key] = candidates[0];
        }

        return pairs;
    }
}
