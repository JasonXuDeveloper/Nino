using System.Globalization;
using System.Linq;
using System.Text;
using Nino.Generator.Common;
using static Nino.Generator.Generation.TypeNames;

namespace Nino.Generator.Generation.Emit;

/// <summary>
/// The members of a formatter whose body is flat (<see cref="TypeModel.Flat"/>, #355): no member-by-member bodies,
/// only the facts Nino.Core's flat kernels need. A member accessor struct per item (one line each), a layout struct,
/// the chain of items as the base class of <c>__Flat</c> (and of <c>__Fields</c>, one item per member, the fallback),
/// the startup check that picks one of them once per call (<c>__Check</c>), the run hooks and the bodies.
/// </summary>
internal static class FlatEmitter
{
    private const string Services = CoreCompilerServices;

    /// <summary>Everything but the twins' hooks (<see cref="EmitHooks"/>), in the formatter.</summary>
    public static void Emit(CodeWriter w, TypeModel m, FormatterNames names)
    {
        var flat = m.Flat!;
        string t = m.TypeName;
        w.Line("// The body is flat (raw members, bools, raw T?s, strings and flat bodies): Nino.Core's flat kernels write");
        w.Line("// and read it through the accessor of each item (a field, a constant offset; or a property), the layout and");
        w.Line("// the chain of items below.");
        foreach (var item in flat.Items)
        {
            if (item.Kind == FlatItemKind.Block)
            {
                var first = m.Members[item.Member];
                int bytes = Enumerable.Range(item.Member, item.Count).Sum(i => m.Members[i].Raw!.Bytes);
                Accessor(w, m, names, BlockName(m, item), first.SourceName, bytes.ToString(CultureInfo.InvariantCulture));
            }

            for (int i = item.Member; i < item.Member + item.Count; i++)
            {
                var member = m.Members[i];
                string? key = m.Keyed ? member.Key.ToString(CultureInfo.InvariantCulture) : null;
                if (item.Property)
                    PropertyAccessor(w, m, names, item, member, item.Kind == FlatItemKind.Slot ? SlotSize(member) : "0", key);
                else
                    Accessor(w, m, names, MemberName(member), member.SourceName,
                        item.Kind == FlatItemKind.Slot ? SlotSize(member) : member.Raw?.Expression ?? "0", key);
            }
        }

        w.Line();
        if (flat.ValueType)
        {
            // A struct: each element of a run in place, through one item per member.
            w.Line($"internal sealed class __Flat : {Services}NinoFlatValues<{Chain(m, fields: true)}> {{ }}");
            w.Line();
            EmitHooks(w, m, names, m.Self);
            return;
        }

        w.Open($"internal readonly struct __Layout : {Services}INinoFlatLayout");
        w.Line($"{names.Inline} public object Create() => {BodyEmitter.Create(m, compactSyntax: false)};");
        w.Line(m.Kind == FormatterKind.SealedClass
            ? $"{names.Inline} public bool IsExact(object value) => true;"
            : $"{names.Inline} public bool IsExact(object value) => value.GetType() == typeof({t});");
        w.Line($"public int TypeId => {m.TypeId.ToString(CultureInfo.InvariantCulture)};");
        w.Close();
        w.Line();
        w.Line($"internal sealed class __Flat : {Services}NinoFlat<__Layout, {Chain(m, fields: false)}> {{ }}");
        if (flat.Checks)
        {
            w.Line($"internal sealed class __Fields : {Services}NinoFlat<__Layout, {Chain(m, fields: true)}> {{ }}");
            w.Line();
            EmitCheck(w, m);
        }

        w.Line();
        EmitHooks(w, m, names, m.Self);
        if (flat.RunsOnly)
            return;
        w.Line();
        EmitBodies(w, m, names);
    }

    /// <summary>
    /// The run hooks of <c>INinoFormatter</c> on the formatter or a twin (<paramref name="formatter"/>, which writes and
    /// reads an element the run leaves): the chain of blocks where the startup check holds, else the chain of fields.
    /// </summary>
    public static void EmitHooks(CodeWriter w, TypeModel m, FormatterNames names, string formatter)
    {
        var flat = m.Flat!;
        string t = m.TypeName;
        string kind = m.Kind == FormatterKind.SealedClass ? "Sealed" : "Typed";
        // A generic type's chain is exact where every type argument is a value type; shared over a class, each accessor
        // would be a call, and its runs go element by element.
        w.Line(m.IsGeneric
            ? $"public bool HasFlat => {string.Join(" && ", m.TypeParameters.Select(static p => $"typeof({p}).IsValueType"))};"
            : "public bool HasFlat => true;");
        // The class bodies an element opens, a literal: runs deeper than the inline depth allows go element by element.
        string depth = flat.RunDepth.ToString(CultureInfo.InvariantCulture);
        if (flat.ValueType)
        {
            w.Line(names.Inline);
            w.Line($"public void WriteFlat({Services}NinoRun run, ref {Core}Writer writer) => __Flat.Write<{t}, {formatter}>(run, ref writer, {depth});");
            w.Line(names.Inline);
            w.Line($"public void ReadFlat({Services}NinoRun run, ref {Core}Reader reader) => __Flat.Read<{t}, {formatter}>(run, ref reader, {depth});");
            return;
        }

        Hook(w, names, flat.Checks, $"public void WriteFlat({Services}NinoRun run, ref {Core}Writer writer)",
            "__Check.Write", $"Write{kind}<{t}, {formatter}>(run, ref writer, {depth})");
        Hook(w, names, flat.Checks, $"public void ReadFlat({Services}NinoRun run, ref {Core}Reader reader)",
            "__Check.Read", $"Read{kind}<{t}, {formatter}>(run, ref reader, {depth})");
    }

    private static void Hook(CodeWriter w, FormatterNames names, bool checks, string signature, string check,
        string call)
    {
        w.Line(names.Inline);
        if (!checks)
        {
            w.Line($"{signature} => __Flat.{call};");
            return;
        }

        w.Open(signature);
        w.Line($"if ({check})");
        w.IndentedLine($"__Flat.{call};");
        w.Line("else");
        w.IndentedLine($"__Fields.{call};");
        w.Close();
    }

    /// <summary>
    /// <c>WriteBody</c> (and a base's <c>WriteMembers</c>): one body through the chain of fields, which needs no check
    /// (a block's copy pays off in runs, not in one body). A sealed class's writes are out of line on a local copy of
    /// the writer whatever their size: the chain inlined into its callers (a polymorphic base's dispatch, a union's
    /// cases) took their inlining budgets, which then left the accessors themselves as calls (PolymorphicTest's
    /// UnionArray, 1.08 times the base's time to write), and its own JIT budget inlines the whole chain. One body's
    /// read is the member-by-member one (BodyEmitter): through the chain, a body in a dispatch kept more on the stack
    /// (ChatMessage's 14 stack accesses became 33) and UnionArray read in up to 1.16 times the base's time.
    /// </summary>
    private static void EmitBodies(CodeWriter w, TypeModel m, FormatterNames names)
    {
        string t = m.TypeName;
        string body = Body(m);
        // A sealed class's top-level writes have their own twin (__WriteTop), so its WriteBody serves the dispatch and
        // the members of other types: out of line. A class that is not sealed writes a top-level value through
        // WriteBody: out of line, one value took 1.3 times the base's time under NativeAOT; a small body stays inline.
        string write = m.InlineBody && m.Kind != FormatterKind.SealedClass ? "WriteInline" : "WriteBody";
        w.Line(names.Inline);
        w.Line($"{Signature.WriteBody(t)} => {body}.{write}(value, ref writer);");
        if (!m.Inheritance.EmitMembers)
            return;
        // A base's WriteMembers, inline in its subtypes' bodies.
        w.Line();
        w.Line(names.Inline);
        w.Line($"{Signature.WriteMembers(t)} => {body}.WriteInline(value, ref writer);");
    }

    /// <summary>
    /// <c>__Check</c>: whether each block lies in the object as on the wire and each struct member's memory is its
    /// bytes (<c>Write</c>), and also nothing in them is checked on read (<c>Read</c>: Untrusted mode), measured once on
    /// an uninitialized instance; the hooks read it once per call, never per element.
    /// </summary>
    private static void EmitCheck(CodeWriter w, TypeModel m)
    {
        var flat = m.Flat!;
        w.Open("private static class __Check");
        w.Line("internal static readonly bool Write = Proves();");
        var structs = flat.Items.Where(static i => i.Kind == FlatItemKind.Struct && !i.Property)
            .Select(i => $"{Services}NinoFieldBlock.ReadsAsIs<{m.Members[i.Member].TypeName}>()").ToArray();
        w.Line(structs.Length == 0
            ? "internal static readonly bool Read = Write;"
            : $"internal static readonly bool Read = Write && {string.Join(" && ", structs)};");
        w.Line();
        w.Open("private static bool Proves()");
        w.Line($"var value = {Services}NinoFieldBlock.Probe<{m.TypeName}>();");
        w.Line("return value != null");
        var checks = flat.Items.Where(static i => i.Kind == FlatItemKind.Block || i.Kind == FlatItemKind.Struct && !i.Property).ToArray();
        for (int c = 0; c < checks.Length; c++)
        {
            var item = checks[c];
            string first = m.Members[item.Member].SourceName;
            int offset = 0;
            for (int i = item.Member; i < item.Member + item.Count; i++)
            {
                var member = m.Members[i];
                bool last = c == checks.Length - 1 && i == item.Member + item.Count - 1;
                w.Line($"    && {Services}NinoFieldBlock.IsAt(in value.{first}, in value.{member.SourceName}, " +
                       $"{offset.ToString(CultureInfo.InvariantCulture)}, {member.Raw!.Expression}){(last ? ";" : "")}");
                offset += member.Raw.Bytes;
            }
        }

        w.Close();
        w.Close();
    }

    /// <summary>
    /// The items as nested type arguments in wire order: blocks and struct members copied whole (<c>NinoBlockAt</c>), or
    /// (<paramref name="fields"/>) one item per member (<c>NinoItemAt</c> with its bottom item), the last without a next:
    /// the one chain of items every composite is (Nino.Core's NinoItemChain.cs).
    /// </summary>
    private static string Chain(TypeModel m, bool fields)
    {
        // Each node as its type and its arguments before the next: "NinoBlockAt<__B_A" or "NinoItemAt<__M_S, NinoUtf16Item".
        // A version-tolerant or keyed body: one item per member, each wrapped (NinoTolerantItem, NinoKeyItem), the
        // chain in its region (NinoRegionItem; a keyed one's entries NinoEntriesItem).
        var format = m.Flat!.Format;
        fields |= format != NinoFormat.Positional;
        var nodes = new System.Collections.Generic.List<string>();
        foreach (var item in m.Flat!.Items)
        {
            if (!fields && (item.Kind == FlatItemKind.Block || item.Kind == FlatItemKind.Struct && !item.Property))
            {
                string member = item.Kind == FlatItemKind.Block ? BlockName(m, item) : MemberName(m.Members[item.Member]);
                nodes.Add($"{Services}NinoBlockAt<{member}");
                continue;
            }

            for (int i = item.Member; i < item.Member + item.Count; i++)
            {
                var member = m.Members[i];
                string bottom = item.Kind switch
                {
                    FlatItemKind.String => Services + "NinoUtf16Item",
                    FlatItemKind.Utf8 => Services + "NinoUtf8Item",
                    FlatItemKind.Bool => Services + "NinoBoolItem",
                    FlatItemKind.Struct => $"{Services}NinoStructItem<{member.TypeName}>",
                    FlatItemKind.Slot => $"{Services}NinoSlotItem<{member.TypeName}, {member.Formatter}, " +
                                         $"{(item.Property ? PropertyName(member) : MemberName(member))}>",
                    FlatItemKind.Nested => item.Nested,
                    _ => $"{Services}NinoRawItem<{member.TypeName}>",
                };
                if (format == NinoFormat.VersionTolerant)
                    bottom = $"{Services}NinoTolerantItem<{bottom}>";
                else if (format == NinoFormat.Keyed)
                    bottom = $"{Services}NinoKeyItem<{(item.Property ? PropertyName(member) : MemberName(member))}, {bottom}>";
                nodes.Add(item.Property
                    ? $"{Services}NinoPropertyAt<{PropertyName(member)}, {PropertyValue(item, member)}, {bottom}"
                    : $"{Services}NinoItemAt<{MemberName(member)}, {bottom}");
            }
        }

        var text = new StringBuilder();
        if (format == NinoFormat.VersionTolerant)
            text.Append(Services).Append("NinoRegionItem<");
        else if (format == NinoFormat.Keyed)
            text.Append(Services).Append("NinoRegionItem<").Append(Services).Append("NinoEntriesItem<");
        for (int i = 0; i < nodes.Count; i++)
            text.Append(nodes[i]).Append(i < nodes.Count - 1 ? ", " : "");
        text.Append('>', nodes.Count + (format == NinoFormat.Keyed ? 2 : format == NinoFormat.VersionTolerant ? 1 : 0));
        return text.ToString();
    }

    /// <summary>A field's accessor (INinoMember), with its <paramref name="size"/> and, in a keyed body, its
    /// <paramref name="key"/> (INinoKey).</summary>
    private static void Accessor(CodeWriter w, TypeModel m, FormatterNames names, string name, string field,
        string size, string? key = null) =>
        w.Line($"internal readonly struct {name} : {Services}INinoMember{(key != null ? $", {Services}INinoKey" : "")} {{ " +
               $"{names.Inline} public ref byte Ref(ref byte v) => " +
               $"ref {Services}NinoMemberAccess.Bytes(ref {Services}NinoMemberAccess.As<{m.TypeName}>(ref v).{field}); " +
               $"public int Size => {size}; {(key != null ? $"public int Key => {key}; " : "")}}}");

    /// <summary>
    /// A property's accessor: its value read and assigned through the property (INinoProperty), its
    /// <paramref name="size"/> (a slot's) and, in a keyed body, its <paramref name="key"/> (INinoKey). A reference
    /// travels as NinoReference, assigned back without a cast (the bottom item read a value of the member's type).
    /// </summary>
    private static void PropertyAccessor(CodeWriter w, TypeModel m, FormatterNames names, FlatItem item,
        MemberModel member, string size, string? key)
    {
        string access = $"{Services}NinoMemberAccess.As<{m.TypeName}>(ref v).{member.SourceName}";
        string value = PropertyValue(item, member);
        string load = item.Reference ? $"new {value}({access})" : access;
        string store = item.Reference ? $"value.As<{member.TypeName}>()" : "value";
        w.Line($"internal readonly struct {PropertyName(member)} : {Services}INinoProperty<{value}>" +
               $"{(key != null ? $", {Services}INinoKey" : "")} {{ " +
               $"{names.Inline} public {value} Load(ref byte v) => {load}; " +
               $"{names.Inline} public void Store(ref byte v, {value} value) => {access} = {store}; " +
               $"public int Size => {size}; {(key != null ? $"public int Key => {key}; " : "")}}}");
    }

    /// <summary>The type of a property's local copy: the member's, or NinoReference for a class (a class as a type
    /// argument makes the chain code shared over classes).</summary>
    private static string PropertyValue(FlatItem item, MemberModel member) =>
        item.Reference ? Services + "NinoReference" : member.TypeName;

    /// <summary>The most bytes a raw <c>T?</c> member's single value takes, a literal (#436: never a field of Nino.Core
    /// read per element; <see cref="FlatRules.SlotBytes"/>).</summary>
    private static string SlotSize(MemberModel member) =>
        FlatRules.SlotBytes(member).ToString(CultureInfo.InvariantCulture);

    private static string PropertyName(MemberModel member) => "__P_" + member.Name;

    /// <summary>The class one body is written and read through: the chain of fields where there is one.</summary>
    public static string Body(TypeModel m) => m.Flat!.Checks ? "__Fields" : "__Flat";

    private static string MemberName(MemberModel member) => "__M_" + member.Name; // never __F_: a generic formatter's parameters

    private static string BlockName(TypeModel m, FlatItem item) => "__B_" + m.Members[item.Member].Name;
}
