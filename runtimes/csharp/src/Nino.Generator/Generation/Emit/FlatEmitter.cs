using System.Globalization;
using System.Linq;
using System.Text;
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
        w.Line("// The body is flat (raw members, bools and strings): Nino.Core's flat kernels write and read it through");
        w.Line("// the accessor of each item (its field, a constant offset), the layout and the chain of items below.");
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
                Accessor(w, m, names, MemberName(member), member.SourceName, member.Raw?.Expression ?? "0");
            }
        }

        w.Line();
        w.Open($"internal readonly struct __Layout : {Services}INinoFlatLayout");
        w.Line($"{names.Inline} public object Create() => {BodyEmitter.Create(m, compactSyntax: false)};");
        w.Line(m.Kind == FormatterKind.SealedClass
            ? $"{names.Inline} public bool IsExact(object value) => true;"
            : $"{names.Inline} public bool IsExact(object value) => value.GetType() == typeof({t});");
        w.Line($"public int TypeId => {m.TypeId.ToString(CultureInfo.InvariantCulture)};");
        w.Close();
        w.Line();
        w.Line($"private sealed class __Flat : {Services}NinoFlat<__Layout, {Chain(m, fields: false)}> {{ }}");
        if (flat.Checks)
        {
            w.Line($"private sealed class __Fields : {Services}NinoFlat<__Layout, {Chain(m, fields: true)}> {{ }}");
            w.Line();
            EmitCheck(w, m);
        }

        w.Line();
        EmitHooks(w, m, names, m.Self);
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
        w.Line("public bool HasFlat => true;");
        Hook(w, names, flat.Checks, $"public {Core}Writer WriteFlat({Services}NinoRun run, {Core}Writer writer)",
            "__Check.Write", $"Write{kind}<{t}, {formatter}>(run, writer)");
        Hook(w, names, flat.Checks, $"public {Core}Reader ReadFlat({Services}NinoRun run, {Core}Reader reader)",
            "__Check.Read", $"Read{kind}<{t}, {formatter}>(run, reader)");
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
        w.IndentedLine($"return __Flat.{call};");
        w.Line($"return __Fields.{call};");
        w.Close();
    }

    /// <summary>
    /// <c>WriteBody</c> and <c>ReadBody</c> (and a base's <c>WriteMembers</c> / <c>ReadMembers</c>): one body through
    /// the chain of fields, which needs no check (a block's copy pays off in runs, not in one body), inlined into the
    /// caller where the body is small (<see cref="TypeModel.InlineBody"/>), else out of line on a local copy of the
    /// writer or reader.
    /// </summary>
    private static void EmitBodies(CodeWriter w, TypeModel m, FormatterNames names)
    {
        string t = m.TypeName;
        string body = m.Flat!.Checks ? "__Fields" : "__Flat";
        string form = m.InlineBody ? "Inline" : "Body";
        w.Line(names.Inline);
        w.Line($"{Signature.WriteBody(t)} => {body}.Write{form}(value, ref writer);");
        w.Line();
        w.Line(names.Inline);
        w.Open(Signature.ReadBody(t));
        w.Line("var __v = value;");
        w.Line("if (__v == null)");
        w.IndentedLine($"value = __v = {BodyEmitter.Create(m, names.CompactSyntax)};");
        w.Line($"{body}.Read{form}(__v, ref reader);");
        w.Close();
        if (!m.Inheritance.EmitMembers)
            return;
        w.Line();
        w.Line(names.Inline);
        w.Line($"{Signature.WriteMembers(t)} => {body}.WriteInline(value, ref writer);");
        w.Line();
        w.Line(names.Inline);
        w.Line($"{Signature.ReadMembers(t)} => {body}.ReadInline(value, ref reader);");
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
        var structs = flat.Items.Where(static i => i.Kind == FlatItemKind.Struct)
            .Select(i => $"{Services}NinoFieldBlock.ReadsAsIs<{m.Members[i.Member].TypeName}>()").ToArray();
        w.Line(structs.Length == 0
            ? "internal static readonly bool Read = Write;"
            : $"internal static readonly bool Read = Write && {string.Join(" && ", structs)};");
        w.Line();
        w.Open("private static bool Proves()");
        w.Line($"var value = {Services}NinoFieldBlock.Probe<{m.TypeName}>();");
        w.Line("return value != null");
        var checks = flat.Items.Where(static i => i.Kind is FlatItemKind.Block or FlatItemKind.Struct).ToArray();
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
    /// The items as nested type arguments in wire order, ended by <c>NinoFlatEnd</c>: blocks and struct members copied
    /// whole, or (<paramref name="fields"/>) one item per member.
    /// </summary>
    private static string Chain(TypeModel m, bool fields)
    {
        var text = new StringBuilder();
        int open = 0;
        foreach (var item in m.Flat!.Items)
        {
            switch (item.Kind)
            {
                case FlatItemKind.Block when !fields:
                    Open(text, "NinoFlatBlock", BlockName(m, item));
                    open++;
                    break;
                case FlatItemKind.Struct when !fields:
                    Open(text, "NinoFlatBlock", MemberName(m.Members[item.Member]));
                    open++;
                    break;
                default:
                    for (int i = item.Member; i < item.Member + item.Count; i++)
                    {
                        var member = m.Members[i];
                        string name = MemberName(member);
                        switch (item.Kind)
                        {
                            case FlatItemKind.String:
                                Open(text, "NinoFlatString", name);
                                break;
                            case FlatItemKind.Utf8:
                                Open(text, "NinoFlatUtf8", name);
                                break;
                            case FlatItemKind.Bool:
                                Open(text, "NinoFlatBool", name);
                                break;
                            case FlatItemKind.Struct:
                                Open(text, "NinoFlatStruct", name + ", " + member.TypeName);
                                break;
                            default:
                                Open(text, "NinoFlatValue", name + ", " + member.TypeName);
                                break;
                        }

                        open++;
                    }

                    break;
            }
        }

        text.Append(Services).Append("NinoFlatEnd").Append('>', open);
        return text.ToString();
    }

    private static void Open(StringBuilder text, string item, string arguments) =>
        text.Append(Services).Append(item).Append('<').Append(arguments).Append(", ");

    private static void Accessor(CodeWriter w, TypeModel m, FormatterNames names, string name, string field,
        string size) =>
        w.Line($"internal readonly struct {name} : {Services}INinoFlatMember {{ {names.Inline} public ref byte Ref(object v) => " +
               $"ref {Services}NinoFlatAccess.Bytes(ref {Services}NinoFlatAccess.As<{m.TypeName}>(v).{field}); " +
               $"public int Size => {size}; }}");

    private static string MemberName(MemberModel member) => "__F_" + member.Name;

    private static string BlockName(TypeModel m, FlatItem item) => "__B_" + m.Members[item.Member].Name;
}
