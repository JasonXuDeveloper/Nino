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
            if (item.Kind == FlatItemKind.Block && item.Count > 1)
            {
                var first = m.Members[item.Member];
                Accessor(w, m, names, BlockName(m, item), first.SourceName, BlockOffset(m, item, item.Count));
            }

            if (item.Kind == FlatItemKind.Block)
                BlockCheck(w, m, names, item);

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
            ChainClass(w, m, names, "__Flat", $"{Services}NinoFlatValues<{Chain(m, fields: true)}>", "Item");
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
        string element = m.Kind == FormatterKind.SealedClass ? "Sealed" : "Typed";
        ChainClass(w, m, names, "__Flat", $"{Services}NinoFlat<__Layout, {Chain(m, fields: false)}>", element);
        if (flat.Checks)
        {
            ChainClass(w, m, names, "__Fields", $"{Services}NinoFlat<__Layout, {Chain(m, fields: true)}>", element);
            w.Line();
            EmitCheck(w, m);
        }

        // A raw body (#452): nested in other flat bodies only, its own runs and bodies as they were.
        if (flat.NestOnly)
            return;
        w.Line();
        EmitHooks(w, m, names, m.Self);
        if (flat.RunsOnly)
            return;
        w.Line();
        EmitBodies(w, m, names);
    }

    /// <summary>
    /// A chain's class (<paramref name="name"/>, deriving <paramref name="type"/>) and, for a type other flat bodies can
    /// nest (not generic), its <c>Nested</c>: the body as one item (<paramref name="item"/>: NinoFlat's Sealed or Typed,
    /// NinoFlatValues' Item) under a name without type arguments. A node whose type arguments held the nested chain went
    /// past the JIT's context complexity (64 type arguments, counted through every level), which it reports as a
    /// recursive inline and leaves as a call per element (#451: FlatOuter's node after its nested FlatLeaf).
    /// </summary>
    private static void ChainClass(CodeWriter w, TypeModel m, FormatterNames names, string name, string type, string item)
    {
        if (m.Flat!.Generic)
        {
            w.Line($"internal sealed class {name} : {type} {{ }}");
            return;
        }

        w.Open($"internal sealed class {name} : {type}");
        w.Open($"internal readonly struct Nested : {Services}INinoItem");
        w.Line($"public bool Leaves {{ {names.Inline} get => default({item}).Leaves; }}");
        w.Line($"{names.Inline} public long MaxSize(ref byte item) => default({item}).MaxSize(ref item);");
        w.Line($"{names.Inline} public int WriteAt(ref byte destination, ref byte item) => default({item}).WriteAt(ref destination, ref item);");
        w.Line($"{names.Inline} public int ReadAt(ref byte source, int available, ref byte item, bool fresh) => " +
               $"default({item}).ReadAt(ref source, available, ref item, fresh);");
        w.Close();
        w.Close();
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
    /// <c>__Check</c>: whether each block lies in the object as on the wire (<c>Write</c>), and also nothing in them is
    /// checked on read (<c>Read</c>: Untrusted mode), measured once on an uninitialized instance, and the same of each
    /// nested body that has a check (its blocks in the chain of blocks); the hooks read it once per call, never per
    /// element. Internal: the bodies nesting this one read it.
    /// </summary>
    private static void EmitCheck(CodeWriter w, TypeModel m)
    {
        var flat = m.Flat!;
        var blocks = flat.Items.Where(static i => i.Kind == FlatItemKind.Block).ToArray();
        var inner = flat.Items.Where(static i => i.NestedChecks).Select(static i => i.Nested + ".__Check").Distinct()
            .ToArray();
        w.Open("internal static class __Check");
        var write = (blocks.Length != 0 ? new[] { "Proves()" } : new string[0]).Concat(inner.Select(static c => c + ".Write"));
        w.Line($"internal static readonly bool Write = {string.Join(" && ", write)};");
        // A block read copies its values as their bytes: only where Untrusted mode checks none of them (the chain of
        // fields checks them one by one); its bools are checked in the block's bytes (its TCheck).
        var structs = blocks.SelectMany(i => Enumerable.Range(i.Member, i.Count))
            .Where(i => m.Members[i].Raw!.Read == RawRead.Checked)
            .Select(i => $"{Services}NinoFieldBlock.ReadsAsIs<{m.Members[i].TypeName}>()").Distinct()
            .Concat(inner.Select(static c => c + ".Read")).ToArray();
        w.Line(structs.Length == 0
            ? "internal static readonly bool Read = Write;"
            : $"internal static readonly bool Read = Write && {string.Join(" && ", structs)};");
        if (blocks.Length == 0)
        {
            w.Close();
            return;
        }

        w.Line();
        w.Open("private static bool Proves()");
        w.Line($"var value = {Services}NinoFieldBlock.Probe<{m.TypeName}>();");
        w.Line("return value != null");
        for (int c = 0; c < blocks.Length; c++)
        {
            var item = blocks[c];
            string first = m.Members[item.Member].SourceName;
            for (int i = item.Member; i < item.Member + item.Count; i++)
            {
                var member = m.Members[i];
                bool last = c == blocks.Length - 1 && i == item.Member + item.Count - 1;
                w.Line($"    && {Services}NinoFieldBlock.IsAt(in value.{first}, in value.{member.SourceName}, " +
                       $"{BlockOffset(m, item, i - item.Member)}, {member.Raw!.Expression}){(last ? ";" : "")}");
            }
        }

        w.Close();
        w.Close();
    }

    /// <summary>
    /// The offset of member <paramref name="index"/> of a block in it (its first <paramref name="index"/> members'
    /// bytes; all of them for the block's size): a literal, or a sum with the sizes of its structs
    /// (<c>NinoUnsafe.SizeOf</c>, JIT-time constants).
    /// </summary>
    private static string BlockOffset(TypeModel m, FlatItem item, int index)
    {
        int constant = 0;
        var parts = new System.Collections.Generic.List<string>();
        for (int i = item.Member; i < item.Member + index; i++)
        {
            var raw = m.Members[i].Raw!;
            if (raw.IsConstant)
                constant += raw.Bytes;
            else
                parts.Add(raw.Expression);
        }

        if (constant != 0 || parts.Count == 0)
            parts.Insert(0, constant.ToString(CultureInfo.InvariantCulture));
        return string.Join(" + ", parts);
    }

    /// <summary>
    /// A block's check of its bytes before a read copies them (<c>INinoBlockCheck</c>, NinoBlockAt's TCheck): its bools'
    /// bytes, each 0 or 1, tested together. A block without a bool takes <c>NinoUnchecked</c>.
    /// </summary>
    private static void BlockCheck(CodeWriter w, TypeModel m, FormatterNames names, FlatItem item)
    {
        var flags = Enumerable.Range(item.Member, item.Count).Where(i => m.Members[i].Raw!.Read == RawRead.Bool)
            .Select(i => $"{Services}NinoBlockCheck.Flag(ref source, {BlockOffset(m, item, i - item.Member)})").ToArray();
        if (flags.Length == 0)
            return;
        w.Line($"internal readonly struct {CheckName(m, item)} : {Services}INinoBlockCheck {{ {names.Inline} " +
               $"public void Check(ref byte source) => {Services}NinoBlockCheck.Bools({string.Join(" | ", flags)}); }}");
    }

    private static string CheckName(TypeModel m, FlatItem item) =>
        Enumerable.Range(item.Member, item.Count).Any(i => m.Members[i].Raw!.Read == RawRead.Bool)
            ? "__C_" + m.Members[item.Member].Name
            : Services + "NinoUnchecked";

    /// <summary>
    /// The items as nested type arguments in wire order: blocks copied whole (<c>NinoBlockAt</c>), or
    /// (<paramref name="fields"/>) one item per member (<c>NinoItemAt</c> with its bottom item), as a tree of groups
    /// (<c>NinoGroup</c>, <see cref="FlatRules.Tree"/>): the one chain of items every composite is (Nino.Core's
    /// NinoItemChain.cs).
    /// </summary>
    private static string Chain(TypeModel m, bool fields)
    {
        // Each part: "NinoBlockAt<__B_A, __C_A>" or "NinoItemAt<__M_S, NinoUtf16Item>". A version-tolerant or keyed
        // body: one item per member, each wrapped (NinoTolerantItem, NinoKeyItem), the chain in its region
        // (NinoRegionItem; a keyed one's entries NinoEntriesItem).
        var format = m.Flat!.Format;
        fields |= format != NinoFormat.Positional;
        var parts = new System.Collections.Generic.List<string>();
        foreach (var item in m.Flat!.Items)
        {
            if (!fields && item.Kind == FlatItemKind.Block)
            {
                parts.Add($"{Services}NinoBlockAt<{BlockName(m, item)}, {CheckName(m, item)}>");
                continue;
            }

            for (int i = item.Member; i < item.Member + item.Count; i++)
            {
                var member = m.Members[i];
                // A block's members one by one: each by its own kind.
                var kind = item.Kind != FlatItemKind.Block ? item.Kind
                    : member.Raw!.Read == RawRead.Bool ? FlatItemKind.Bool
                    : member.Raw.IsConstant && member.Raw.Read == RawRead.Plain ? FlatItemKind.Value
                    : FlatItemKind.Struct;
                string bottom = kind switch
                {
                    FlatItemKind.String => Services + "NinoUtf16Item",
                    FlatItemKind.Utf8 => Services + "NinoUtf8Item",
                    FlatItemKind.Bool => Services + "NinoBoolItem",
                    FlatItemKind.Struct => $"{Services}NinoStructItem<{member.TypeName}>",
                    FlatItemKind.Slot => $"{Services}NinoSlotItem<{member.TypeName}, {member.Formatter}, " +
                                         $"{(item.Property ? PropertyName(member) : MemberName(member))}>",
                    // The chain of blocks nests the type's blocks (its check is in this one's), the chain of fields its
                    // fields.
                    FlatItemKind.Nested => item.Nested + (fields && item.NestedChecks ? ".__Fields" : ".__Flat") + ".Nested",
                    _ => $"{Services}NinoRawItem<{member.TypeName}>",
                };
                if (format == NinoFormat.VersionTolerant)
                    bottom = $"{Services}NinoTolerantItem<{bottom}>";
                else if (format == NinoFormat.Keyed)
                    bottom = $"{Services}NinoKeyItem<{(item.Property ? PropertyName(member) : MemberName(member))}, {bottom}>";
                parts.Add(item.Property
                    ? $"{Services}NinoPropertyAt<{PropertyName(member)}, {PropertyValue(item, member)}, {bottom}>"
                    : $"{Services}NinoItemAt<{MemberName(member)}, {bottom}>");
            }
        }

        var text = new StringBuilder();
        if (format == NinoFormat.VersionTolerant)
            text.Append(Services).Append("NinoRegionItem<");
        else if (format == NinoFormat.Keyed)
            text.Append(Services).Append("NinoRegionItem<").Append(Services).Append("NinoEntriesItem<");
        // A group's parts are separated by commas: one after each part or group that is not its group's last.
        var left = new System.Collections.Generic.Stack<int>();
        void Next()
        {
            if (left.Count == 0)
                return;
            int rest = left.Pop() - 1;
            left.Push(rest);
            if (rest > 0)
                text.Append(", ");
        }

        FlatRules.Tree(parts.Count, (i, _) =>
            {
                text.Append(parts[i]);
                Next();
            },
            count =>
            {
                text.Append(Services).Append("NinoGroup<");
                left.Push(count);
            },
            () =>
            {
                text.Append('>');
                left.Pop();
                Next();
            });
        text.Append('>', format == NinoFormat.Keyed ? 2 : format == NinoFormat.VersionTolerant ? 1 : 0);
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

    /// <summary>A block's accessor: its first member's where it is the only one (a struct).</summary>
    private static string BlockName(TypeModel m, FlatItem item) =>
        item.Count > 1 ? "__B_" + m.Members[item.Member].Name : MemberName(m.Members[item.Member]);
}
