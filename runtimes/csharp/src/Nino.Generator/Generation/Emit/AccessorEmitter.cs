using System.Collections.Generic;
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
    /// <summary>
    /// The <c>[UnsafeAccessor]</c> declarations of a generic type, in a class of their own next to the formatter
    /// (<see cref="MemberCode.UnsafeAccessorClass"/>): the runtime requires the type parameters of the class declaring
    /// them to be exactly the target type's, and the formatter also takes their formatters.
    /// </summary>
    public static void EmitGenericUnsafeAccessors(CodeWriter w, TypeModel m, List<string> aliases)
    {
        if (!m.IsGeneric || !HasAccess(m, MemberAccess.UnsafeAccessor))
            return;
        w.Line();
        w.Line(HiddenAttribute);
        w.Open($"internal static class {UnsafeAccessorClass(m)}{m.TypeArgumentList}{m.Constraints}");
        EmitUnsafeAccessors(w, m, aliases, "internal");
        w.Close();
    }

    /// <summary><c>[UnsafeAccessor]</c> declarations inside the formatter (a non-generic type's).</summary>
    public static void EmitUnsafeAccessors(CodeWriter w, TypeModel m, List<string> aliases, string access = "private")
    {
        const string alias = "__NinoRCS";
        const string aliasPrefix = alias + ".";
        const string aliasDeclaration = "using " + alias + " = global::System.Runtime.CompilerServices;";
        string prefix = CompilerServices;
        // Each annotation uses the qualifier twice. Two annotations pay for the alias declaration.
        int annotations = 0;
        foreach (var member in m.Members)
        {
            if (member.Access != MemberAccess.UnsafeAccessor)
                continue;
            annotations += member.IsProperty && !member.Unassignable ? 2 : 1;
            if (annotations >= 2)
                break;
        }

        if (annotations == 0)
            return;
        if (annotations >= 2 && (!m.IsGeneric || !m.TypeParameters.Contains(alias)))
        {
            aliases.Add(aliasDeclaration);
            prefix = aliasPrefix;
        }

        string attr = prefix + "UnsafeAccessor";
        string kind = prefix + "UnsafeAccessorKind";
        foreach (var member in m.Members)
        {
            if (member.Access != MemberAccess.UnsafeAccessor)
                continue;

            string self = (member.DeclaringTypeIsValueType ? "ref " : "") + member.DeclaringType + " @this";
            w.Line();
            if (member.IsProperty)
            {
                w.Line($"[{attr}({kind}.Method, Name = \"get_{member.SourceName}\")]");
                w.Line($"{access} static extern {member.TypeName} {Getter(member)}({self});");
                // No setter for a member that is read and dropped (get-only, no matching constructor parameter).
                if (member.Unassignable)
                    continue;
                w.Line($"[{attr}({kind}.Method, Name = \"set_{member.SourceName}\")]");
                w.Line($"{access} static extern void {Setter(member)}({self}, {member.TypeName} value);");
            }
            else
            {
                w.Line($"[{attr}({kind}.Field, Name = \"{member.SourceName}\")]");
                w.Line($"{access} static extern ref {member.TypeName} {FieldRef(member)}({self});");
            }
        }
    }

    /// <summary>
    /// The accessors of the interface members a struct implements explicitly (<see cref="MemberAccess.Interface"/>),
    /// inside the formatter: generic over the instance, constrained to the interface, so the call is a constrained call
    /// on the struct itself, which the JIT binds to the implementation and inlines, with no box (a cast to the
    /// interface would box a copy, and a write to it would be lost).
    /// </summary>
    public static void EmitInterfaceAccessors(CodeWriter w, TypeModel m)
    {
        foreach (var member in m.Members)
        {
            if (member.Access != MemberAccess.Interface)
                continue;

            string constraint = $"where __TI : {member.DeclaringType}";
            string name = Identifier(member.SourceName);
            w.Line();
            w.Line(InlineAttribute);
            w.Line($"private static {member.TypeName} {Getter(member)}<__TI>(ref __TI o) {constraint} => o.{name};");
            if (member.ConstructionOnly)
                continue;
            w.Line(InlineAttribute);
            w.Line($"private static void {Setter(member)}<__TI>(ref __TI o, {member.TypeName} value) {constraint} => " +
                   $"o.{name} = value;");
        }
    }

    /// <summary>
    /// The generated part of the (partial) type itself: a static accessor class, where its private members are
    /// visible, with getters, setters and field references for the formatter to call; and the pool hooks of a
    /// <c>[NinoPooled]</c> class.
    /// </summary>
    public static void EmitPartial(CodeWriter w, TypeModel m)
    {
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

        if (HasAccess(m, MemberAccess.Partial))
            EmitAccessorClass(w, m);
        if (m.Pooled)
            EmitPoolHooks(w, m);

        foreach (var _ in declarations)
            w.Close();
        if (hasNamespace)
            w.Close();
    }

    private static bool HasAccess(TypeModel m, MemberAccess access)
    {
        foreach (var member in m.Members)
            if (member.Access == access)
                return true;

        return false;
    }

    private static void EmitAccessorClass(CodeWriter w, TypeModel m)
    {
        w.Line(HiddenAttribute);
        w.Open($"internal static class {AccessorClass(m)}");
        foreach (var member in m.Members)
        {
            if (member.Access != MemberAccess.Partial)
                continue;

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
