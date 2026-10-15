using System.Linq;
using static Nino.Generator.Generation.Emit.MemberCode;

namespace Nino.Generator.Generation.Emit;

/// <summary>Access to non-public members: <c>[UnsafeAccessor]</c> externs (.NET 8+) or a partial accessor class.</summary>
internal static class AccessorEmitter
{
    /// <summary><c>[UnsafeAccessor]</c> declarations inside the formatter.</summary>
    public static void EmitUnsafeAccessors(CodeWriter w, TypeModel m)
    {
        const string attr = "global::System.Runtime.CompilerServices.UnsafeAccessor";
        const string kind = "global::System.Runtime.CompilerServices.UnsafeAccessorKind";
        foreach (var member in m.Members.Where(x => x.Access == MemberAccess.UnsafeAccessor))
        {
            string self = (member.DeclaringTypeIsValueType ? "ref " : "") + member.DeclaringType + " @this";
            w.Line();
            if (member.IsProperty)
            {
                w.Line($"[{attr}({kind}.Method, Name = \"get_{member.Name}\")]");
                w.Line($"private static extern {member.TypeName} __G_{member.Name}({self});");
                w.Line($"[{attr}({kind}.Method, Name = \"set_{member.Name}\")]");
                w.Line($"private static extern void __S_{member.Name}({self}, {member.TypeName} value);");
            }
            else
            {
                w.Line($"[{attr}({kind}.Field, Name = \"{member.Name}\")]");
                w.Line($"private static extern ref {member.TypeName} __F_{member.Name}({self});");
            }
        }
    }

    /// <summary>
    /// A static accessor class declared inside the (partial) type itself, where its private members are visible:
    /// getters, setters and field references for the formatter to call.
    /// </summary>
    public static string EmitPartialAccessor(TypeModel m)
    {
        var w = new CodeWriter(1024);
        w.Line();
        bool hasNamespace = m.AccessorNamespace.Length != 0;
        if (hasNamespace)
            w.Open($"namespace {m.AccessorNamespace}");
        foreach (var declaration in m.AccessorDeclarations)
            w.Open(declaration);

        const string inline =
            "[global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]";
        w.Line("[global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]");
        w.Open($"internal static class {AccessorClass(m)}");
        foreach (var member in m.Members.Where(x => x.Access == MemberAccess.Partial))
        {
            string o = (member.DeclaringTypeIsValueType ? "ref " : "") + m.TypeName + " o";
            string name = Id(member.Name);
            w.Line(inline);
            w.Line($"internal static {member.TypeName} G_{member.Name}({o}) => o.{name};");
            if (member.ConstructionOnly)
                continue;
            w.Line(inline);
            w.Line(member.IsProperty
                ? $"internal static void S_{member.Name}({o}, {member.TypeName} value) => o.{name} = value;"
                : $"internal static ref {member.TypeName} F_{member.Name}({o}) => ref o.{name};");
        }

        w.Close();
        foreach (var _ in m.AccessorDeclarations)
            w.Close();
        if (hasNamespace)
            w.Close();
        return w.ToString();
    }
}
