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
/// <item>version tolerant: positional inside a length-prefixed slice, checking for its end before each member;</item>
/// <item>keyed: (key, length, value) per member inside a slice, read in order with a fallback dispatch loop.</item>
/// </list>
/// </summary>
internal sealed class BodyEmitter
{
    private readonly TypeModel _m;
    private readonly CodeWriter _w;
    private readonly FormatterNames _names;
    private int _blocks;

    public BodyEmitter(TypeModel model, CodeWriter writer, FormatterNames names)
    {
        _m = model;
        _w = writer;
        _names = names;
    }

    private string T => _m.TypeName;

    /// <summary>Whether the body is prefixed with its length (version tolerant or keyed).</summary>
    private bool Sliced => _m.VersionTolerant || _m.Keyed;

    private IEnumerable<MemberModel> OwnMembers =>
        _m.BaseFormatter.Length == 0 ? _m.Members : _m.Members.Skip(_m.BaseMemberCount);

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

    public void EmitWriteBody()
    {
        if (_m.EmitMembers && !Sliced && _m.CollectionFormatter.Length == 0)
        {
            _w.Line($"public void WriteBody({T} value, ref {Core}Writer writer) => WriteMembers(value, ref writer);");
            _w.Line();
            EmitMembersMethods();
            return;
        }

        _w.Block($"public void WriteBody({T} value, ref {Core}Writer writer)", () =>
        {
            if (Sliced)
                _w.Line("int __length = writer.ReserveInt32();");
            if (_m.CollectionFormatter.Length != 0)
                _w.Line($"default({_m.CollectionFormatter}).Serialize(value, ref writer);");
            if (_m.EmitMembers)
                _w.Line("WriteMembers(value, ref writer);");
            else
                EmitMemberWrites();
            if (Sliced)
                _w.Line("writer.PatchLength(__length);");
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
        if (_m.VersionTolerant)
        {
            _w.Line("/// <summary>Reads the members into an existing instance; false when the body ended early.</summary>");
            _w.Line(_names.Inline);
            _w.Block($"internal static bool ReadMembers({T} value, ref {Core}Reader reader)", () =>
            {
                EmitMemberReads("reader", inPlace: true, onEnd: "return false;");
                _w.Line("return true;");
            });
            return;
        }

        // Positional bodies never end early: no result to report.
        _w.Line(_names.Inline);
        _w.Block($"internal static void ReadMembers({T} value, ref {Core}Reader reader)",
            () => EmitMemberReads("reader", inPlace: true, onEnd: ""));
    }

    // ----- writes --------------------------------------------------------------------------------------------

    private void EmitMemberWrites()
    {
        _blocks = 0;
        if (_m.BaseFormatter.Length != 0)
            _w.Line($"{_m.BaseFormatter}.WriteMembers(value, ref writer);");
        if (_m.Keyed)
        {
            EmitKeyedWrites();
            return;
        }

        foreach (var group in RawBlock.Groups(OwnMembers))
        {
            var first = group[0];
            if (first.RawSize.Length == 0)
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
            if (first.RawSize.Length == 0)
            {
                string l = "__l" + lengths++;
                _w.Line($"writer.UnsafeWrite<int>({Key(first)});");
                _w.Line($"int {l} = writer.ReserveInt32();");
                EmitFormatterWrite(first);
                _w.Line($"writer.PatchLength({l});");
                continue;
            }

            var block = new RawBlock(group, headers: true);
            string p = "__p" + _blocks++;
            _w.Line($"ref byte {p} = ref writer.Reserve({block.Total});");
            for (int i = 0; i < group.Count; i++)
            {
                _w.Line($"{Core}NinoUnsafe.Write<int>(ref {p}, {block.HeaderOffsets[2 * i]}, {Key(group[i])});");
                _w.Line($"{Core}NinoUnsafe.Write<int>(ref {p}, {block.HeaderOffsets[2 * i + 1]}, {group[i].RawSize});");
                _w.Line(RawWrite(group[i], p, block.Offsets[i]));
            }
        }
    }

    // ----- reads ---------------------------------------------------------------------------------------------

    public void EmitReadBody()
    {
        _w.Open($"public void ReadBody(ref {T} value, ref {Core}Reader reader)");
        string r = "reader";
        if (Sliced)
        {
            _w.Line("var __reader = reader.ReadSlice();");
            r = "__reader";
        }

        if (_m.Construction.Recreate)
            EmitRecreatingRead(r);
        else
            EmitInPlaceRead(r);
        _w.Close();
    }

    private void EmitInPlaceRead(string r)
    {
        var c = _m.Construction;
        if (_m.Kind != FormatterKind.Struct)
        {
            _w.Line("if (value == null)");
            _w.Indented(() => _w.Line($"value = {(_m.Pooled ? T + ".RentFromNinoPool() ?? " : "")}{(c.RefFactory.Length != 0 ? c.RefFactory : c.Creator)}();"));
            if (_m.TrackReferences)
                _w.Line("reader.SetReference(value);");
        }

        if (_m.CollectionFormatter.Length != 0)
            _w.Line($"default({_m.CollectionFormatter}).Deserialize(ref value, ref {r});");

        if (_m.EmitMembers)
        {
            _w.Line($"ReadMembers(value, ref {r});");
        }
        else
        {
            EmitMemberReads(r, inPlace: true, onEnd: "goto __end;");
            if (_m.VersionTolerant && !_m.Keyed)
                _w.Line("__end: ;");
        }

        if (Sliced)
            _w.Line("reader.EndSlice(in __reader);");
    }

    /// <summary>Reads every member into locals, then constructs the instance from them.</summary>
    private void EmitRecreatingRead(string r)
    {
        var c = _m.Construction;
        foreach (var member in _m.Members)
            _w.Line($"{member.TypeName} {Local(member)} = default;");
        EmitMemberReads(r, inPlace: false, onEnd: "goto __end;");
        if (_m.VersionTolerant && !_m.Keyed)
            _w.Line("__end:");
        if (Sliced)
            _w.Line("reader.EndSlice(in __reader);");

        if (c.Creator.Length == 0)
        {
            _w.Line($"throw new global::System.NotSupportedException(\"Nino: cannot construct {EscapeString(T)}\");");
            return;
        }

        bool isClass = _m.Kind != FormatterKind.Struct;
        if (isClass)
            _w.Line("var __old = value;");

        var args = new HashSet<string>(c.Arguments);
        var assigned = _m.Members.Where(x => !args.Contains(x.Name) && !x.Unassignable).ToList();
        var initializers = assigned.Where(x => x.Access == MemberAccess.Direct).ToList();
        string call = c.Creator + "(" +
                      string.Join(", ", c.Arguments.Select(a => Local(_m.Members.First(x => x.Name == a)))) + ")";
        if (initializers.Count == 0)
        {
            _w.Line($"value = {call};");
        }
        else
        {
            _w.Line($"value = {call}");
            _w.Open();
            foreach (var member in initializers)
                _w.Line($"{Identifier(member.Name)} = {Local(member)},");
            _w.Close(";");
        }

        foreach (var member in assigned.Where(x => x.Access != MemberAccess.Direct))
            _w.Line(Set(_m, member, "value", Local(member)));

        if (isClass)
        {
            _w.Line("if (__old != null)");
            _w.Indented(() => _w.Line($"{Core}NinoRecycler<{T}>.Recycle(__old);"));
        }
    }

    private void EmitMemberReads(string r, bool inPlace, string onEnd)
    {
        _blocks = 0;
        // Base members through the base formatter (in place only: recreated types read everything into locals).
        var members = inPlace ? OwnMembers : _m.Members;
        if (inPlace && _m.BaseFormatter.Length != 0)
        {
            _w.Line(_m.VersionTolerant
                ? $"if (!{_m.BaseFormatter}.ReadMembers(value, ref {r})) {onEnd}"
                : $"{_m.BaseFormatter}.ReadMembers(value, ref {r});");
        }

        if (_m.Keyed)
        {
            EmitKeyedReads(members.ToList(), r, inPlace);
            return;
        }

        if (_m.VersionTolerant)
        {
            // Tolerant bodies may end early (data written by an older version): check before every member.
            foreach (var member in members)
            {
                _w.Line($"if ({r}.Eof) {onEnd}");
                EmitMemberRead(member, r, inPlace);
            }

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
                    $"{Core}NinoUnsafe.Read<{group[i].TypeName}>(ref {p}, {block.Offsets[i]})"));
            }
        }
    }

    /// <summary>Reads one member: raw members directly, others through their formatter.</summary>
    private void EmitMemberRead(MemberModel member, string r, bool inPlace)
    {
        if (member.RawSize.Length != 0)
        {
            _w.Line(Store(_m, member, inPlace, $"{r}.UnsafeRead<{member.TypeName}>()"));
            return;
        }

        var direct = DirectRead(member, r);
        if (direct != null)
        {
            _w.Line(Store(_m, member, inPlace, direct));
            return;
        }

        string formatter = Formatter(member);
        if (!inPlace)
        {
            _w.Line($"default({formatter}).Deserialize(ref {Local(member)}, ref {r});");
            return;
        }

        var reference = Ref(_m, member, "value");
        if (reference != null)
        {
            _w.Line($"default({formatter}).Deserialize(ref {reference}, ref {r});");
            return;
        }

        // Properties: read through a local so the existing instance is reused, then assign it back.
        _w.Open();
        _w.Line($"var __t = {Get(_m, member, "value")};");
        _w.Line($"default({formatter}).Deserialize(ref __t, ref {r});");
        _w.Line(Set(_m, member, "value", "__t"));
        _w.Close();
    }

    // ----- keyed ---------------------------------------------------------------------------------------------

    private static int Key(MemberModel member) => ComputeId(member.Name);

    /// <summary>(key, length) as the int64 a reader sees, for raw members of constant size; else null.</summary>
    private static string? KeyHeader(MemberModel member)
    {
        if (member.RawSize.Length == 0 || !int.TryParse(member.RawSize, out int size))
            return null;
        uint key = unchecked((uint)Key(member));
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

            _w.Line($"if (!{r}.TryReadInt32({Key(member)})) goto __keyed;");
            _w.Open();
            _w.Line($"var __m = {r}.ReadSlice();");
            EmitMemberRead(member, "__m", inPlace);
            _w.Line($"{r}.EndSlice(in __m);");
            _w.Close();
        }

        // Unknown members appended by a newer version, or members in another order.
        if (members.Count != 0)
            _w.Line("__keyed:");
        _w.Open($"while (!{r}.Eof)");
        _w.Line($"int __key = {r}.UnsafeRead<int>();");
        _w.Line($"var __m = {r}.ReadSlice();");
        if (members.Count != 0)
        {
            _w.Open("switch (__key)");
            foreach (var member in members)
            {
                _w.Line($"case {Key(member)}:");
                _w.Open();
                EmitMemberRead(member, "__m", inPlace);
                if (member.RawSize.Length == 0)
                    _w.Line($"{r}.EndSlice(in __m);");
                _w.Line("break;");
                _w.Close();
            }

            _w.Close();
        }

        _w.Close();
    }
}
