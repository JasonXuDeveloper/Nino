using MemberGroups = Nino.Generator.Generation.Emit.RawBlock.MemberGroups;
using MemberRange = Nino.Generator.Generation.Emit.RawBlock.MemberRange;
using static Nino.Generator.Generation.Emit.MemberCode;
using static Nino.Generator.Generation.TypeNames;

namespace Nino.Generator.Generation.Emit;

internal sealed partial class BodyEmitter
{
    /// <summary>
    /// Each member as (key, length, value). Consecutive raw members share one reservation, which also holds the key
    /// and length of the member after them; any other member's key and length are one reservation.
    /// </summary>
    private void EmitKeyedWrites(MemberGroups groups)
    {
        int lengths = 0;
        MemberRange run = default;
        while (groups.MoveNext())
        {
            var group = groups.Current;
            var first = group[0];
            if (first.Raw != null)
            {
                run = group;
                continue;
            }

            string l = "__l" + lengths++;
            var block = new RawBlock(run, headers: true);
            // A member that cannot defer only needs its length patched.
            bool patchOnly = CanPatchMember(first);
            string reserve;
            if (patchOnly)
            {
                _w.Line($"int {l} = writer.Position + {block.Plus(NinoWire.KeySize)};");
                reserve = $"writer.Reserve({block.Plus(NinoWire.KeySize + NinoWire.LengthSize)})";
            }
            else
            {
                reserve = $"writer.BeginRegionAfter({block.Plus(NinoWire.KeySize)}, out int {l})";
            }
            string p = run.Count != 0 ? EmitRawBlock(run, block, reserve) : "__p" + _blocks++;
            if (run.Count == 0)
                _w.Line($"ref byte {p} = ref {reserve};");
            _w.Line($"{CoreCompilerServices}NinoUnsafe.Write(ref {p}, {block.Total}, {first.Key});");
            run = default;
            EmitFormatterWrite(first);
            _w.Line($"writer.{(patchOnly ? "PatchLength" : "EndRegion")}({l});");
        }

        if (run.Count != 0)
        {
            var block = new RawBlock(run, headers: true);
            EmitRawBlock(run, block, $"writer.Reserve({block.Total})");
        }
    }

    private static bool PreservesReadContext(MemberModel member) =>
        member.Raw != null || (member.Traits & FormatterTraits.PreservesReadContext) != 0;

    /// <summary>
    /// (key, length) as the int64 a reader sees, for raw members of constant size, as an argument; else null.
    /// </summary>
    private static string? KeyHeader(MemberModel member)
    {
        if (member.Raw is not { IsConstant: true } raw)
            return null;
        // Reader rejects big-endian hosts before reading any headers.
        return $"{PackKeyHeader(member.Key, raw.Bytes)}L";
    }

    private static long PackKeyHeader(int key, int size) => (long)size << 32 | unchecked((uint)key);

    /// <summary>
    /// Reads members written in declaration order straight through (raw members check key and length with one
    /// 8-byte compare); at the first unexpected key, falls back to a loop that dispatches any key.
    /// </summary>
    private void EmitKeyedReads(MemberRange members, string r, bool inPlace)
    {
        for (int i = 0; i < members.Count; i++)
        {
            var member = members[i];
            var header = KeyHeader(member);
            if (header != null)
            {
                var read = member.Raw!.Read;
                if (read is RawRead.Plain or RawRead.Bool)
                {
                    string local = "__k" + i;
                    // Read the wire byte so non-canonical bool values still reach the check.
                    string type = read == RawRead.Bool ? "byte" : member.TypeName;
                    _w.Line($"if (!{r}.TryReadKeyed({header}, out {type} {local})) goto __keyed;");
                    string value = read == RawRead.Bool ? $"{CoreCompilerServices}NinoUnsafe.ReadBool(ref {local}, 0)" : local;
                    _w.Line(Store(_m, member, inPlace, value, _mode.Target));
                    continue;
                }

                _w.Line($"if (!{r}.TryReadInt64({header})) goto __keyed;");
                EmitMemberRead(member, r, inPlace);
                continue;
            }

            _w.Line($"if (!{r}.TryReadInt32({member.Key})) goto __keyed;");
            _w.Open();
            _w.Line($"var __m = {r}.ReadRegion();");
            EmitMemberRead(member, "__m", inPlace);
            // A context-preserving member only needs the exact-length check.
            string end = PreservesReadContext(member) ? "if (!__m.Eof) " : "";
            _w.Line($"{end}{r}.EndMember(in __m);");
            _w.Close();
        }

        // Unknown members appended by a newer version, or members in another order.
        if (members.Count != 0)
            _w.Line("__keyed:");
        _w.Open($"while (!{r}.Eof)");
        _w.Line($"int __key = {r}.UnsafeRead<int>();");
        bool splitRegion = members.Count is > 0 and <= MaxSplitRegionMembers;
        _w.Line(splitRegion ? $"{Core}Reader __m;" : $"var __m = {r}.ReadRegion();");
        if (members.Count != 0)
        {
            // A member's value fills its entry exactly; an unknown member is skipped with its entry.
            _w.Open("switch (__key)");
            bool preservesContext = true;
            for (int i = 0; i < members.Count; i++)
            {
                var member = members[i];
                preservesContext &= PreservesReadContext(member);
                _w.Line($"case {member.Key}:");
                // Data written before a [NinoFormerName] rename; writes always use the current name.
                foreach (int formerKey in member.FormerKeys)
                    _w.Line($"case {formerKey}:");
                using (_w.Indent())
                {
                    if (splitRegion)
                        _w.Line($"__m = {r}.ReadRegion();");
                    EmitMemberRead(member, "__m", inPlace);
                    _w.Line("break;");
                }
            }

            _w.Line("default:");
            using (_w.Indent())
            {
                if (splitRegion)
                    _w.Line($"{r}.ReadRegion();");
                _w.Line("continue;");
            }
            _w.Close();
            string end = preservesContext ? "if (!__m.Eof) " : "";
            _w.Line($"{end}{r}.EndMember(in __m);");
        }

        _w.Close();
    }
}
