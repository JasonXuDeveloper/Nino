using System.Collections.Generic;
using System.Linq;
using static Nino.Generator.Generation.Emit.MemberCode;
using static Nino.Generator.Generation.TypeNames;

namespace Nino.Generator.Generation.Emit;

/// <summary>
/// Emits a type's body: <c>WriteBody</c>/<c>ReadBody</c> and the static <c>WriteMembers</c>/<c>ReadMembers</c> that
/// derived types reuse. Three member layouts share the code below:
/// <list type="bullet">
/// <item>positional: members back to back, consecutive raw members as one block;</item>
/// <item>version tolerant: positional, each level (the type and each [NinoType] base) in its own length-prefixed
/// slice, checking for the slice's end before each member;</item>
/// <item>keyed: (key, length, value) per member inside a slice, read in order with a fallback dispatch loop.</item>
/// </list>
/// </summary>
internal sealed class BodyEmitter
{
    private readonly TypeModel _m;
    private readonly CodeWriter _w;
    private readonly FormatterNames _names;
    private int _blocks;

    /// <summary>
    /// The instance member reads go to: <c>value</c>, or the local a class body caches it in (a store through
    /// <c>ref value</c> would otherwise make the JIT reload the reference after every call).
    /// </summary>
    private string _target = "value";

    public BodyEmitter(TypeModel model, CodeWriter writer, FormatterNames names)
    {
        _m = model;
        _w = writer;
        _names = names;
    }

    private string T => _m.TypeName;

    /// <summary>Whether each level is prefixed with its length (version tolerant, positional).</summary>
    private bool Leveled => _m.Levels.Length != 0;

    private bool Delegates => _m.BaseFormatter.Length != 0;

    private IEnumerable<MemberModel> OwnMembers => Delegates ? _m.Members.Skip(_m.BaseMemberCount) : _m.Members;

    /// <summary>The members of each level from <paramref name="first"/> on.</summary>
    private IEnumerable<List<MemberModel>> LevelsFrom(int first)
    {
        int start = 0;
        for (int i = 0; i < _m.Levels.Length; i++)
        {
            int count = _m.Levels[i];
            if (i >= first)
                yield return _m.Members.Skip(start).Take(count).ToList();
            start += count;
        }
    }

    private string Formatter(MemberModel member) => _names.Use(member.Formatter);

    // ----- entry ---------------------------------------------------------------------------------------------

    public void EmitRawStruct()
    {
        _w.Line("// Unmanaged and every field serialized: the struct is written as its raw memory.");
        _w.Line(_names.Inline);
        _w.Line($"public void WriteBody({T} value, ref {Core}Writer writer) => writer.WriteValue(value);");
        _w.Line();
        _w.Line(_names.Inline);
        _w.Line($"public void ReadBody(ref {T} value, ref {Core}Reader reader) => value = reader.ReadValue<{T}>();");
    }

    /// <summary>
    /// A body that calls formatters (strings, collections, other types) is compiled on its own rather than inlined
    /// into the caller: inlined, it would use up the caller's inlining budget (a share of the caller's own size)
    /// before the small helpers it and the caller use (bounds checks, depth tracking, flush) are inlined. Bodies of
    /// raw members only are a few stores and stay inlinable.
    /// </summary>
    private bool OutOfLine => _m.OutOfLineBody;

    /// <summary>
    /// The signature of a body method. An out-of-line body is a static method behind an inlined instance forwarder:
    /// a call to an instance method of the (empty) formatter struct stores a dummy byte on the caller's stack and
    /// passes its address as <c>this</c>. With a <c>__Top</c> twin (<see cref="TypeModel.TopInline"/>), the body is
    /// an inlinable static method (<c>__ReadBodyInline</c>) that the out-of-line one and <c>__Top</c> call.
    /// </summary>
    private string BodySignature(string name, string parameters)
    {
        if (!OutOfLine)
            return $"public void {name}({parameters})";
        string args = name == "WriteBody" ? "value, ref writer" : "ref value, ref reader";
        _w.Line(_names.Inline);
        _w.Line($"public void {name}({parameters}) => __{name}({args});");
        _w.Line();
        _w.Line(_names.NoInline);
        if (!_m.TopInline)
            return $"private static void __{name}({parameters})";
        if (name == "ReadBody" && LocalReader)
        {
            _w.Block($"private static void __{name}({parameters})", () =>
            {
                _w.Line("var __r = reader;");
                _w.Line($"__{name}Inline(ref value, ref __r);");
                _w.Line("reader = __r;");
            });
        }
        else
            _w.Line($"private static void __{name}({parameters}) => __{name}Inline({args});");
        _w.Line();
        _w.Line(_names.Inline);
        return $"private static void __{name}Inline({parameters})";
    }

    public void EmitWriteBody()
    {
        string signature = BodySignature("WriteBody", $"{T} value, ref {Core}Writer writer");
        if (_m.EmitMembers && !_m.Keyed && _m.CollectionFormatter.Length == 0)
        {
            _w.Line($"{signature} => WriteMembers(value, ref writer);");
            _w.Line();
            EmitMembersMethods();
            return;
        }

        _w.Block(signature, () =>
        {
            if (_m.Keyed)
                _w.Line("int __length = writer.BeginRegion();");
            if (_m.CollectionFormatter.Length != 0)
                _w.Line($"default({_m.CollectionFormatter}).Serialize(value, ref writer);");
            if (_m.EmitMembers)
                _w.Line("WriteMembers(value, ref writer);");
            else
                EmitMemberWrites();
            if (_m.Keyed)
                _w.Line("writer.EndRegion(__length);");
        });

        if (_m.EmitMembers)
        {
            _w.Line();
            EmitMembersMethods();
        }
    }

    /// <summary>Static member (de)serializers that derived types call instead of repeating these members.</summary>
    public void EmitMembersMethods()
    {
        // Inlined so the writer/reader state stays in registers across levels, as if the members were written inline.
        _w.Line(_names.Inline);
        _w.Block($"internal static void WriteMembers({T} value, ref {Core}Writer writer)", EmitMemberWrites);
        _w.Line();
        _w.Line(_names.Inline);
        _w.Block($"internal static void ReadMembers({T} value, ref {Core}Reader reader)",
            () => EmitMemberReads("reader", inPlace: true));
    }

    // ----- writes --------------------------------------------------------------------------------------------

    private void EmitMemberWrites()
    {
        _blocks = 0;
        if (Delegates)
            _w.Line($"{_m.BaseFormatter}.WriteMembers(value, ref writer);");
        if (_m.Keyed)
        {
            EmitKeyedWrites();
            return;
        }

        if (!Leveled)
        {
            EmitPositionalWrites(OwnMembers);
            return;
        }

        int n = 0;
        foreach (var level in LevelsFrom(Delegates ? _m.BaseLevelCount : 0))
        {
            string l = "__l" + n++;
            _w.Line($"int {l} = writer.BeginRegion();");
            EmitPositionalWrites(level);
            _w.Line($"writer.EndRegion({l});");
        }
    }

    private void EmitPositionalWrites(IEnumerable<MemberModel> members)
    {
        foreach (var group in RawBlock.Groups(members))
        {
            var first = group[0];
            if (first.Raw == null)
            {
                EmitFormatterWrite(first);
                continue;
            }

            if (group.Count == 1)
            {
                string method = RawBlock.IsStruct(first) ? "WriteValue" : "UnsafeWrite";
                _w.Line($"writer.{method}<{first.TypeName}>({Get(_m, first, "value")});");
                continue;
            }

            var block = new RawBlock(group);
            string p = "__p" + _blocks++;
            _w.Line($"ref byte {p} = ref writer.Reserve({block.Total});");
            for (int i = 0; i < group.Count; i++)
                _w.Line(RawWrite(group[i], p, block.Offsets[i]));
        }
    }

    private void EmitFormatterWrite(MemberModel member)
    {
        string value = Get(_m, member, "value");
        _w.Line(DirectWrite(member, value) ?? $"default({Formatter(member)}).Serialize({value}, ref writer);");
    }

    // Struct members may have padding, which WriteValue clears.
    private string RawWrite(MemberModel member, string p, string offset) =>
        $"{Core}NinoUnsafe.{(RawBlock.IsStruct(member) ? "WriteValue" : "Write")}<{member.TypeName}>(ref {p}, {offset}, {Get(_m, member, "value")});";

    /// <summary>Each member as (key, length, value); consecutive raw members share one reservation.</summary>
    private void EmitKeyedWrites()
    {
        int lengths = 0;
        foreach (var group in RawBlock.Groups(_m.Members))
        {
            var first = group[0];
            if (first.Raw == null)
            {
                string l = "__l" + lengths++;
                _w.Line($"writer.UnsafeWrite<int>({first.Key});");
                _w.Line($"int {l} = writer.BeginRegion();");
                EmitFormatterWrite(first);
                _w.Line($"writer.EndRegion({l});");
                continue;
            }

            var block = new RawBlock(group, headers: true);
            string p = "__p" + _blocks++;
            _w.Line($"ref byte {p} = ref writer.Reserve({block.Total});");
            for (int i = 0; i < group.Count; i++)
            {
                _w.Line($"{Core}NinoUnsafe.Write<int>(ref {p}, {block.HeaderOffsets[2 * i]}, {group[i].Key});");
                _w.Line($"{Core}NinoUnsafe.Write<int>(ref {p}, {block.HeaderOffsets[2 * i + 1]}, {group[i].Raw!.Expression});");
                _w.Line(RawWrite(group[i], p, block.Offsets[i]));
            }
        }
    }

    // ----- reads ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Whether the out-of-line read body works on a local copy of the reader, written back at its end: through the
    /// <c>ref</c> parameter, the position is stored after every member and loaded again by the next one (a store to
    /// load forwarding on the critical path of each member, and a load, an operation and a store on ARM64), where
    /// a local the JIT keeps in registers. Only for bodies that open no class body (<see cref="TypeModel.Nests"/>
    /// false): a nested body takes the reader's address, which would keep the local in memory too.
    /// </summary>
    private bool LocalReader => OutOfLine && !_m.Nests && !_m.Keyed && !_m.TrackReferences;

    public void EmitReadBody()
    {
        _w.Open(BodySignature("ReadBody", $"ref {T} value, ref {Core}Reader reader"));
        string r = "reader";
        // A __Top twin's inlinable body is read through the local of the out-of-line wrapper (BodySignature).
        bool local = LocalReader && !_m.TopInline;
        if (local)
        {
            _w.Line("var __r = reader;");
            r = "__r";
        }

        if (_m.Keyed)
        {
            _w.Line("var __reader = reader.ReadRegion();");
            r = "__reader";
        }

        if (_m.Construction.Recreate)
            EmitRecreatingRead(r);
        else
            EmitInPlaceRead(r);
        if (local)
            _w.Line("reader = __r;");
        _w.Close();
    }

    private void EmitInPlaceRead(string r)
    {
        var c = _m.Construction;
        if (_m.Kind != FormatterKind.Struct)
        {
            string create = (c.Factory.Length != 0 ? c.Factory : c.Creator) + "()";
            if (_m.Pooled)
                create = T + ".RentFromNinoPool() ?? " + create;
            // The instance in a local (a collection formatter may replace value, so not for collections).
            if (_m.CollectionFormatter.Length == 0)
                _target = "__v";
            if (_target == "__v")
            {
                _w.Line("var __v = value;");
                _w.Line("if (__v == null)");
                _w.Indented(() => _w.Line($"value = __v = {create};"));
            }
            else
            {
                _w.Line("if (value == null)");
                _w.Indented(() => _w.Line($"value = {create};"));
            }

            if (_m.TrackReferences)
                _w.Line($"reader.SetReference({_target});");
        }

        if (_m.CollectionFormatter.Length != 0)
            _w.Line($"default({_m.CollectionFormatter}).Deserialize(ref value, ref {r});");

        if (_m.EmitMembers)
            _w.Line($"ReadMembers({_target}, ref {r});");
        else
            EmitMemberReads(r, inPlace: true);
        _target = "value";

        if (_m.Keyed)
            _w.Line("reader.EndRegion(in __reader);");
    }

    /// <summary>Reads every member into locals, then constructs the instance from them.</summary>
    private void EmitRecreatingRead(string r)
    {
        var c = _m.Construction;
        foreach (var member in _m.Members)
            _w.Line($"{member.TypeName} {Local(member)} = default;");
        EmitMemberReads(r, inPlace: false);
        if (_m.Keyed)
            _w.Line("reader.EndRegion(in __reader);");

        if (c.Creator.Length == 0)
        {
            _w.Line($"throw new global::System.NotSupportedException(\"Nino: cannot construct {EscapeString(T)}\");");
            return;
        }

        // A class is built in a local and then replaces the old instance (which is recycled); a struct is
        // assigned in place.
        bool isClass = _m.Kind != FormatterKind.Struct;
        string target = isClass ? "__new" : "value";
        string assign = isClass ? "var __new = " : "value = ";

        var args = new HashSet<string>(c.Arguments);
        var assigned = _m.Members.Where(x => !args.Contains(x.Name) && !x.Unassignable).ToList();
        var initializers = assigned.Where(x => x.Access == MemberAccess.Direct).ToList();
        string call = c.Creator + "(" +
                      string.Join(", ", c.Arguments.Select(a => Local(_m.Members.First(x => x.Name == a)))) + ")";
        if (initializers.Count == 0)
        {
            _w.Line($"{assign}{call};");
        }
        else
        {
            _w.Line($"{assign}{call}");
            _w.Open();
            foreach (var member in initializers)
                _w.Line($"{Identifier(member.Name)} = {Local(member)},");
            _w.Close(";");
        }

        foreach (var member in assigned.Where(x => x.Access != MemberAccess.Direct))
            _w.Line(Set(_m, member, target, Local(member)));

        if (isClass)
            _w.Line($"{Core}NinoRecycler.Replace(ref value, __new);");
    }

    private void EmitMemberReads(string r, bool inPlace)
    {
        _blocks = 0;
        // Base members through the base formatter (in place only: recreated types read everything into locals).
        bool delegates = inPlace && Delegates;
        var members = inPlace ? OwnMembers : _m.Members;
        if (delegates)
            _w.Line($"{_m.BaseFormatter}.ReadMembers({_target}, ref {r});");

        if (_m.Keyed)
        {
            EmitKeyedReads(members.ToList(), r, inPlace);
            return;
        }

        if (Leveled)
        {
            int n = 0;
            foreach (var level in LevelsFrom(delegates ? _m.BaseLevelCount : 0))
                EmitLevelRead(level, r, inPlace, n++);
            return;
        }

        foreach (var group in RawBlock.Groups(members))
        {
            if (group.Count == 1)
            {
                EmitMemberRead(group[0], r, inPlace);
                continue;
            }

            var block = new RawBlock(group);
            string p = "__p" + _blocks++;
            _w.Line($"ref byte {p} = ref {r}.ReadBlock({block.Total});");
            for (int i = 0; i < group.Count; i++)
            {
                _w.Line(Store(_m, group[i], inPlace,
                    $"{Core}NinoUnsafe.Read<{group[i].TypeName}>(ref {p}, {block.Offsets[i]})", _target));
            }
        }
    }

    /// <summary>
    /// Reads one level's slice. Data written by an older version may end early (missing members keep their current
    /// or default value), and data written by a newer one has trailing members, which the slice skips.
    /// </summary>
    private void EmitLevelRead(List<MemberModel> members, string r, bool inPlace, int n)
    {
        if (members.Count == 0)
        {
            _w.Line($"{r}.ReadRegion();");
            return;
        }

        string s = "__s" + n, end = "__end" + n;
        _w.Open();
        _w.Line($"var {s} = {r}.ReadRegion();");
        foreach (var member in members)
        {
            _w.Line($"if ({s}.Eof) goto {end};");
            EmitMemberRead(member, s, inPlace);
        }

        _w.Line($"{end}:");
        _w.Line($"{r}.EndRegion(in {s});");
        _w.Close();
    }

    /// <summary>Reads one member: raw members directly, others through their formatter.</summary>
    private void EmitMemberRead(MemberModel member, string r, bool inPlace)
    {
        if (member.Raw != null)
        {
            _w.Line(Store(_m, member, inPlace, $"{r}.UnsafeRead<{member.TypeName}>()", _target));
            return;
        }

        var direct = DirectRead(member, r);
        if (direct != null)
        {
            _w.Line(Store(_m, member, inPlace, direct, _target));
            return;
        }

        string formatter = Formatter(member);
        if (!inPlace)
        {
            _w.Line($"default({formatter}).Deserialize(ref {Local(member)}, ref {r});");
            return;
        }

        var reference = Ref(_m, member, _target);
        if (reference != null)
        {
            _w.Line($"default({formatter}).Deserialize(ref {reference}, ref {r});");
            return;
        }

        // Properties: read through a local so the existing instance is reused, then assign it back.
        _w.Open();
        _w.Line($"var __t = {Get(_m, member, _target)};");
        _w.Line($"default({formatter}).Deserialize(ref __t, ref {r});");
        _w.Line(Set(_m, member, _target, "__t"));
        _w.Close();
    }

    // ----- keyed ---------------------------------------------------------------------------------------------

    /// <summary>(key, length) as the int64 a reader sees, for raw members of constant size; else null.</summary>
    private static string? KeyHeader(MemberModel member)
    {
        if (member.Raw is not { IsConstant: true } raw)
            return null;
        int size = raw.Bytes;
        uint key = unchecked((uint)member.Key);
        long le = (long)size << 32 | key;
        long be = (long)key << 32 | (uint)size;
        return le == be ? $"{le}L" : $"(global::System.BitConverter.IsLittleEndian ? {le}L : {be}L)";
    }

    /// <summary>
    /// Reads members written in declaration order straight through (raw members check key and length with one
    /// 8-byte compare); at the first unexpected key, falls back to a loop that dispatches any key.
    /// </summary>
    private void EmitKeyedReads(List<MemberModel> members, string r, bool inPlace)
    {
        foreach (var member in members)
        {
            var header = KeyHeader(member);
            if (header != null)
            {
                _w.Line($"if (!{r}.TryReadInt64({header})) goto __keyed;");
                EmitMemberRead(member, r, inPlace);
                continue;
            }

            _w.Line($"if (!{r}.TryReadInt32({member.Key})) goto __keyed;");
            _w.Open();
            _w.Line($"var __m = {r}.ReadRegion();");
            EmitMemberRead(member, "__m", inPlace);
            _w.Line($"{r}.EndRegion(in __m);");
            _w.Close();
        }

        // Unknown members appended by a newer version, or members in another order.
        if (members.Count != 0)
            _w.Line("__keyed:");
        _w.Open($"while (!{r}.Eof)");
        _w.Line($"int __key = {r}.UnsafeRead<int>();");
        _w.Line($"var __m = {r}.ReadRegion();");
        if (members.Count != 0)
        {
            _w.Open("switch (__key)");
            foreach (var member in members)
            {
                _w.Line($"case {member.Key}:");
                // Data written before a [NinoFormerName] rename; writes always use the current name.
                foreach (int formerKey in member.FormerKeys)
                    _w.Line($"case {formerKey}:");
                _w.Open();
                EmitMemberRead(member, "__m", inPlace);
                if (member.Raw == null)
                    _w.Line($"{r}.EndRegion(in __m);");
                _w.Line("break;");
                _w.Close();
            }

            _w.Close();
        }

        _w.Close();
    }
}
