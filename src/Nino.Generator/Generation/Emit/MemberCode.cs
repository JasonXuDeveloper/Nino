using static Nino.Generator.Generation.TypeNames;

namespace Nino.Generator.Generation.Emit;

/// <summary>Expressions and statements that read or assign a member, whatever its accessibility.</summary>
internal static class MemberCode
{
    /// <summary>Local variable that holds a member while a re-created instance is read.</summary>
    public static string Local(MemberModel member) => "__v_" + member.Name;

    public static string AccessorClass(TypeModel m) => "__NinoAccess_" + m.FormatterName;

    private static string Accessor(TypeModel m) => $"{m.TypeName}.{AccessorClass(m)}";

    private static bool IsReadonlyField(MemberModel member) => !member.IsProperty && member.ConstructionOnly;

    private static string Instance(MemberModel member, string v) => member.DeclaringTypeIsValueType ? "ref " + v : v;

    /// <summary>Expression reading the member of <paramref name="v"/>.</summary>
    public static string Get(TypeModel m, MemberModel member, string v) => member.Access switch
    {
        MemberAccess.Direct => $"{v}.{Identifier(member.Name)}",
        MemberAccess.UnsafeAccessor => member.IsProperty
            ? $"__G_{member.Name}({Instance(member, v)})"
            : $"__F_{member.Name}({Instance(member, v)})",
        _ => member.IsProperty || IsReadonlyField(member)
            ? $"{Accessor(m)}.G_{member.Name}({Instance(member, v)})"
            : $"{Accessor(m)}.F_{member.Name}({Instance(member, v)})",
    };

    /// <summary>Assignable reference to a field of <paramref name="v"/>, or null for properties.</summary>
    public static string? Ref(TypeModel m, MemberModel member, string v)
    {
        if (member.IsProperty)
            return null;
        return member.Access switch
        {
            MemberAccess.Direct => $"{v}.{Identifier(member.Name)}",
            MemberAccess.UnsafeAccessor => $"__F_{member.Name}({Instance(member, v)})",
            _ => $"{Accessor(m)}.F_{member.Name}({Instance(member, v)})",
        };
    }

    /// <summary>Statement assigning <paramref name="expr"/> to the member of <paramref name="v"/>.</summary>
    public static string Set(TypeModel m, MemberModel member, string v, string expr)
    {
        if (!member.IsProperty)
            return $"{Ref(m, member, v)} = {expr};";
        return member.Access switch
        {
            MemberAccess.Direct => $"{v}.{Identifier(member.Name)} = {expr};",
            MemberAccess.UnsafeAccessor => $"__S_{member.Name}({Instance(member, v)}, {expr});",
            _ => $"{Accessor(m)}.S_{member.Name}({Instance(member, v)}, {expr});",
        };
    }

    /// <summary>Stores a read value: into the instance (in place) or into the member's local.</summary>
    public static string Store(TypeModel m, MemberModel member, bool inPlace, string expr) =>
        inPlace ? Set(m, member, "value", expr) : $"{Local(member)} = {expr};";

    /// <summary>
    /// For formatters that never reuse the current value (strings), the read expression that replaces the
    /// formatter call. Avoids a property getter call and keeps the generated code short.
    /// </summary>
    public static string? DirectRead(MemberModel member, string r) => member.Formatter switch
    {
        Core + "StringFormatter" => $"{r}.ReadString()",
        Core + "Utf8StringFormatter" => $"{r}.ReadUtf8()",
        _ => null,
    };

    /// <summary>The write statement that replaces the formatter call for strings, or null.</summary>
    public static string? DirectWrite(MemberModel member, string value) => member.Formatter switch
    {
        Core + "StringFormatter" => $"writer.Write({value});",
        Core + "Utf8StringFormatter" => $"writer.WriteUtf8({value});",
        _ => null,
    };

    /// <summary>The contents of a C# string literal for <paramref name="s"/>.</summary>
    public static string EscapeString(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
