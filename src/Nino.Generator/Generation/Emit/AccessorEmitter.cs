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
    /// The generated part of the (partial) type itself: a static accessor class, where its private members are
    /// visible, with getters, setters and field references for the formatter to call; and the pool hooks of a
    /// <c>[NinoPooled]</c> class.
    /// </summary>
    public static string EmitPartial(TypeModel m)
    {
        var w = new CodeWriter(1024);
        w.Line();
        bool hasNamespace = m.PartialNamespace.Length != 0;
        if (hasNamespace)
            w.Open($"namespace {m.PartialNamespace}");
        var declarations = m.PartialDeclarations;
        for (int i = 0; i < declarations.Length; i++)
        {
            bool self = i == declarations.Length - 1;
            w.Open(self && m.Pooled ? declarations[i] + $" : {Core}INinoPooled" : declarations[i]);
        }

        if (m.Members.Any(x => x.Access == MemberAccess.Partial))
            EmitAccessorClass(w, m);
        if (m.Pooled)
            EmitPoolHooks(w, m);

        foreach (var _ in declarations)
            w.Close();
        if (hasNamespace)
            w.Close();
        return w.ToString();
    }

    private static void EmitAccessorClass(CodeWriter w, TypeModel m)
    {
        const string inline =
            "[global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]";
        w.Line(Hidden);
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
    }

    /// <summary>
    /// <c>ReturnToPool</c>, the rent the formatter calls before creating an instance, and the two partial methods
    /// the user implements (the analyzer requires them: NINO019).
    /// </summary>
    private static void EmitPoolHooks(CodeWriter w, TypeModel m)
    {
        string t = m.TypeName;
        string pool = $"{Core}NinoPool<{t}>";
        w.Line("/// <summary>Fills the pool before Nino first deserializes this type; runs once.</summary>");
        w.Line("static partial void OnNinoPoolWarmup();");
        w.Line();
        w.Line("/// <summary>Releases what must not outlive this use of the instance; called by <see cref=\"ReturnToPool\"/>.</summary>");
        w.Line("partial void OnNinoPoolReturn();");
        w.Line();
        w.Line($"[{Core}NinoIgnore]");
        w.Line("private int _ninoPooled;");
        w.Line();
        w.Line("/// <summary>");
        w.Line("/// Returns the instance to its pool: the next deserialization of this type reuses it, with the lists, arrays");
        w.Line("/// and objects it holds. Do not use it afterwards. Throws when it is already in the pool.");
        w.Line("/// </summary>");
        w.Block("public void ReturnToPool()", () =>
        {
            w.Line("if (global::System.Threading.Interlocked.Exchange(ref _ninoPooled, 1) != 0)");
            w.Indented(() => w.Line($"{pool}.ThrowReturnedTwice();"));
            w.Line("OnNinoPoolReturn();");
            w.Line($"{pool}.Add(this);");
        });
        w.Line();
        w.Line(Hidden);
        w.Block($"internal static {t} RentFromNinoPool()", () =>
        {
            w.Line($"if ({pool}.BeginWarmup())");
            w.Indented(() => w.Line("OnNinoPoolWarmup();"));
            w.Line($"var value = {pool}.TryTake();");
            w.Line("if (value != null)");
            w.Indented(() => w.Line("value._ninoPooled = 0;"));
            w.Line("return value;");
        });
    }

    private const string Hidden =
        "[global::System.ComponentModel.EditorBrowsable(global::System.ComponentModel.EditorBrowsableState.Never)]";
}
