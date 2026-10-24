using System.Linq;
using static Nino.Generator.Generation.Emit.MemberCode;
using static Nino.Generator.Generation.TypeNames;
using Nino.Generator.Common;

namespace Nino.Generator.Generation.Emit;

/// <summary>
/// Access to non-public members: <c>[UnsafeAccessor]</c> externs (.NET 8+; in a generic formatter .NET 9+, whose type
/// parameters the runtime matches with the type's) or a partial accessor class.
/// </summary>
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
                w.Line($"private static extern {member.TypeName} {Getter(member)}({self});");
                // No setter for a member that is read and dropped (get-only, no matching constructor parameter).
                if (member.Unassignable)
                    continue;
                w.Line($"[{attr}({kind}.Method, Name = \"set_{member.SourceName}\")]");
                w.Line($"private static extern void {Setter(member)}({self}, {member.TypeName} value);");
            }
            else
            {
                w.Line($"[{attr}({kind}.Field, Name = \"{member.SourceName}\")]");
                w.Line($"private static extern ref {member.TypeName} {FieldRef(member)}({self});");
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
        bool hasNamespace = m.Partial.Namespace.Length != 0;
        if (hasNamespace)
            w.Open($"namespace {m.Partial.Namespace}");
        var declarations = m.Partial.Declarations;
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
            w.Line($"internal static {member.TypeName} {Getter(member)}({o}) => o.{name};");
            if (member.ConstructionOnly)
                continue;
            w.Line(InlineAttribute);
            w.Line(member.IsProperty
                ? $"internal static void {Setter(member)}({o}, {member.TypeName} value) => o.{name} = value;"
                : $"internal static ref {member.TypeName} {FieldRef(member)}({o}) => ref o.{name};");
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
        // RentFromNinoPool is left to the JIT's own heuristics: forced inline into every read of the type, the take
        // (still a call) only grew those reads. The warmup, run once, is out of line. With nothing pooled yet (as when
        // every read makes new instances), the rent makes no thread-local access.
        w.Lines($$"""
            /// <summary>Instances to pool before Nino first deserializes this type; runs once.</summary>
            private static partial global::System.Collections.Generic.IEnumerable<{{t}}> {{PoolHooks.Warmup}}();

            /// <summary>Releases what must not outlive this use of the instance; called by <see cref="ReturnToPool"/>.</summary>
            private partial void {{PoolHooks.Return}}();

            private static readonly {{pool}} s_ninoPool = {{pool}}.Shared;
            [global::System.ThreadStatic]
            private static {{pool}}.PerThread t_ninoPool;
            [{{Core}}NinoIgnore]
            private int _ninoPooled; // 1 while in the pool

            /// <summary>
            /// Returns the instance to its pool: the next deserialization of this type reuses it, with the lists, arrays
            /// and objects it holds. Do not use it afterwards. Throws when it is already in the pool.
            /// </summary>
            public void ReturnToPool()
            {
                if ({{exchange}} _ninoPooled, 1) != 0)
                    {{pool}}.ThrowReturnedTwice();
                {{PoolHooks.Return}}();
                s_ninoPool.Add(this, ref t_ninoPool);
            }

            {{HiddenAttribute}}
            internal static {{t}} RentFromNinoPool()
            {
                if (s_ninoPool.BeginWarmup())
                    __NinoPoolWarmup();
                if (!s_ninoPool.EverFilled)
                    return null;
                var value = s_ninoPool.TryTake(ref t_ninoPool);
                if (value != null)
                    value._ninoPooled = 0;
                return value;
            }

            {{NoInlineAttribute}}
            private static void __NinoPoolWarmup()
            {
                foreach (var item in {{PoolHooks.Warmup}}() ?? global::System.Linq.Enumerable.Empty<{{t}}>())
                {
                    if (item != null && {{exchange}} item._ninoPooled, 1) == 0)
                        s_ninoPool.Add(item, ref t_ninoPool);
                }
            }
            """);
    }
}
