using System.Linq;
using static Nino.Generator.Generation.Emit.MemberCode;
using static Nino.Generator.Generation.TypeNames;

namespace Nino.Generator.Generation.Emit;

/// <summary>Access to non-public members: <c>[UnsafeAccessor]</c> externs (.NET 8+) or a partial accessor class.</summary>
internal static class AccessorEmitter
{
    /// <summary><c>[UnsafeAccessor]</c> declarations inside the formatter.</summary>
    public static void EmitUnsafeAccessors(CodeWriter w, TypeModel m)
    {
        const string attr = CompilerServices + "UnsafeAccessor";
        const string kind = CompilerServices + "UnsafeAccessorKind";
        foreach (var member in m.Members.Where(x => x.Access == MemberAccess.UnsafeAccessor))
        {
            string self = (member.DeclaringTypeIsValueType ? "ref " : "") + member.DeclaringType + " @this";
            w.Line();
            if (member.IsProperty)
            {
                w.Line($"[{attr}({kind}.Method, Name = \"get_{member.SourceName}\")]");
                w.Line($"private static extern {member.TypeName} __G_{member.Name}({self});");
                // No setter for a member that is read and dropped (get-only, no matching constructor parameter).
                if (member.Unassignable)
                    continue;
                w.Line($"[{attr}({kind}.Method, Name = \"set_{member.SourceName}\")]");
                w.Line($"private static extern void __S_{member.Name}({self}, {member.TypeName} value);");
            }
            else
            {
                w.Line($"[{attr}({kind}.Field, Name = \"{member.SourceName}\")]");
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
        w.Line(HiddenAttribute);
        w.Open($"internal static class {AccessorClass(m)}");
        foreach (var member in m.Members.Where(x => x.Access == MemberAccess.Partial))
        {
            string o = (member.DeclaringTypeIsValueType ? "ref " : "") + m.TypeName + " o";
            string name = Identifier(member.SourceName);
            w.Line(InlineAttribute);
            w.Line($"internal static {member.TypeName} G_{member.Name}({o}) => o.{name};");
            if (member.ConstructionOnly)
                continue;
            w.Line(InlineAttribute);
            w.Line(member.IsProperty
                ? $"internal static void S_{member.Name}({o}, {member.TypeName} value) => o.{name} = value;"
                : $"internal static ref {member.TypeName} F_{member.Name}({o}) => ref o.{name};");
        }

        w.Close();
    }

    /// <summary>
    /// <c>ReturnToPool</c>, the rent the formatter calls before creating an instance, the pool fields, and the two
    /// partial methods the user implements (required: the compiler and NINO019 report them).
    /// </summary>
    private static void EmitPoolHooks(CodeWriter w, TypeModel m)
    {
        string t = m.TypeName;
        string pool = $"{Core}NinoPool<{t}>";
        const string exchange = "global::System.Threading.Interlocked.Exchange(ref";
        w.Line("/// <summary>Instances to pool before Nino first deserializes this type; runs once.</summary>");
        w.Line($"private static partial global::System.Collections.Generic.IEnumerable<{t}> {PoolHooks.Warmup}();");
        w.Line();
        w.Line("/// <summary>Releases what must not outlive this use of the instance; called by <see cref=\"ReturnToPool\"/>.</summary>");
        w.Line($"private partial void {PoolHooks.Return}();");
        w.Line();
        w.Line($"private static readonly {pool} s_ninoPool = {pool}.Shared;");
        w.Line("[global::System.ThreadStatic]");
        w.Line($"private static {pool}.PerThread t_ninoPool;");
        w.Line($"[{Core}NinoIgnore]");
        w.Line("private int _ninoPooled; // 1 while in the pool");
        w.Line();
        w.Line("/// <summary>");
        w.Line("/// Returns the instance to its pool: the next deserialization of this type reuses it, with the lists, arrays");
        w.Line("/// and objects it holds. Do not use it afterwards. Throws when it is already in the pool.");
        w.Line("/// </summary>");
        w.Block("public void ReturnToPool()", () =>
        {
            w.Line($"if ({exchange} _ninoPooled, 1) != 0)");
            w.Indented(() => w.Line($"{pool}.ThrowReturnedTwice();"));
            w.Line($"{PoolHooks.Return}();");
            w.Line("s_ninoPool.Add(this, ref t_ninoPool);");
        });
        w.Line();
        w.Line(HiddenAttribute);
        w.Block($"internal static {t} RentFromNinoPool()", () =>
        {
            w.Line("if (s_ninoPool.BeginWarmup())");
            w.Open();
            w.Line($"foreach (var item in {PoolHooks.Warmup}() ?? global::System.Linq.Enumerable.Empty<{t}>())");
            w.Open();
            w.Line($"if (item != null && {exchange} item._ninoPooled, 1) == 0)");
            w.Indented(() => w.Line("s_ninoPool.Add(item, ref t_ninoPool);"));
            w.Close();
            w.Close();
            w.Line();
            w.Line("var value = s_ninoPool.TryTake(ref t_ninoPool);");
            w.Line("if (value != null)");
            w.Indented(() => w.Line("value._ninoPooled = 0;"));
            w.Line("return value;");
        });
    }
}
