using static Nino.Generator.Generation.TypeNames;

namespace Nino.Generator.Generation.Emit;

/// <summary>Expressions and statements that read or assign a member, whatever its accessibility.</summary>
internal static class MemberCode
{
    /// <summary>Local variable that holds a member while a re-created instance is read.</summary>
    public static string Local(MemberModel member) => "__v_" + member.Name;

    public static string AccessorClass(TypeModel m) => "__NinoAccess_" + m.FormatterName;

    private static string Accessor(TypeModel m) => $"{m.TypeName}.{AccessorClass(m)}";

    /// <summary>The class holding a generic type's <c>[UnsafeAccessor]</c> externs, next to its formatter.</summary>
    public static string UnsafeAccessorClass(TypeModel m) => "__NinoAccessors_" + m.FormatterName;

    private static bool IsReadonlyField(MemberModel member) => !member.IsProperty && member.ConstructionOnly;

    private static string Instance(MemberModel member, string v) => member.DeclaringTypeIsValueType ? "ref " + v : v;

    // The member of v, named in C#: through a cast to its declaring type when a derived member hides it.
    private static string Direct(MemberModel member, string v) => member.Access == MemberAccess.Cast
        ? $"(({member.DeclaringType}){v}).{Identifier(member.SourceName)}"
        : $"{v}.{Identifier(member.SourceName)}";

    /// <summary>
    /// The accessors of a member that generated code cannot name: <c>[UnsafeAccessor]</c> externs in the formatter
    /// (<c>__G_Name</c>, <c>__S_Name</c>, <c>__R_Name</c>) or methods of the partial accessor class (<c>G_Name</c>...):
    /// a getter, a setter (properties) and a field reference (fields). (<c>__F_</c> names a generic formatter's formatter
    /// type parameters, <see cref="TypeNames.FormatterParameter"/>.)
    /// </summary>
    public static string Getter(MemberModel member) => AccessorPrefix(member) + "G_" + member.Name;

    public static string Setter(MemberModel member) => AccessorPrefix(member) + "S_" + member.Name;

    public static string FieldRef(MemberModel member) => AccessorPrefix(member) + "R_" + member.Name;

    private static string AccessorPrefix(MemberModel member) =>
        member.Access == MemberAccess.UnsafeAccessor ? "__" : "";

    private static bool Named(MemberModel member) => member.Access is MemberAccess.Direct or MemberAccess.Cast;

    /// <summary>A call of the accessor <paramref name="accessor"/> on <paramref name="v"/>.</summary>
    private static string Call(TypeModel m, MemberModel member, string accessor, string v, string more = "") =>
        member.Access == MemberAccess.UnsafeAccessor
            ? m.IsGeneric
                ? $"{UnsafeAccessorClass(m)}{m.TypeArgumentList}.{accessor}({Instance(member, v)}{more})"
                : $"{accessor}({Instance(member, v)}{more})"
            : $"{Accessor(m)}.{accessor}({Instance(member, v)}{more})";

    /// <summary>Expression reading the member of <paramref name="v"/>.</summary>
    public static string Get(TypeModel m, MemberModel member, string v) =>
        Named(member) ? Direct(member, v)
        // [UnsafeAccessor] reads a field through its reference; the accessor class has no reference to a readonly one.
        : Call(m, member, member.IsProperty || member.Access == MemberAccess.Partial && IsReadonlyField(member)
            ? Getter(member)
            : FieldRef(member), v);

    /// <summary>Assignable reference to a field of <paramref name="v"/>, or null for properties.</summary>
    public static string? Ref(TypeModel m, MemberModel member, string v) =>
        member.IsProperty ? null : Named(member) ? Direct(member, v) : Call(m, member, FieldRef(member), v);

    /// <summary>Statement assigning <paramref name="expr"/> to the member of <paramref name="v"/>.</summary>
    public static string Set(TypeModel m, MemberModel member, string v, string expr) =>
        !member.IsProperty ? $"{Ref(m, member, v)} = {expr};"
        : Named(member) ? $"{Direct(member, v)} = {expr};"
        : Call(m, member, Setter(member), v, ", " + expr) + ";";

    /// <summary>Stores a read value: into the instance (in place) or into the member's local.</summary>
    public static string Store(TypeModel m, MemberModel member, bool inPlace, string expr, string v) =>
        inPlace ? Set(m, member, v, expr) : $"{Local(member)} = {expr};";

    /// <summary>
    /// For strings, the read expression that replaces the formatter call; <paramref name="current"/>, the member's
    /// value in an existing instance, is kept when the data holds the same string (null: a new instance).
    /// </summary>
    public static string? DirectRead(MemberModel member, string r, string? current = null) =>
        (member.Traits & FormatterTraits.String) != 0 ? $"{r}.ReadString({current})"
        : (member.Traits & FormatterTraits.Utf8String) != 0 ? $"{r}.ReadUtf8({current})"
        : null;

    /// <summary>The write statement that replaces the formatter call for strings, or null.</summary>
    public static string? DirectWrite(MemberModel member, string value) =>
        (member.Traits & FormatterTraits.String) != 0 ? $"writer.Write({value});"
        : (member.Traits & FormatterTraits.Utf8String) != 0 ? $"writer.WriteUtf8({value});"
        : null;
}
