using MemberGroups = Nino.Generator.Generation.Emit.RawBlock.MemberGroups;
using MemberRange = Nino.Generator.Generation.Emit.RawBlock.MemberRange;
using static Nino.Generator.Generation.Emit.MemberCode;
using static Nino.Generator.Generation.TypeNames;

namespace Nino.Generator.Generation.Emit;

internal sealed partial class BodyEmitter
{
    private void EmitMemberWrites()
    {
        _blocks = 0;
        if (Delegates)
            _w.Line($"{BaseFormatter}.WriteMembers(value, ref writer);");
        if (_m.Keyed)
        {
            EmitKeyedWrites(RawBlock.Groups(AllMembers));
            return;
        }

        EmitPositionalWrites(RawBlock.Groups(OwnMembers));
    }

    /// <summary>
    /// A version-tolerant or keyed body whose first members are raw values (written here, not by a base's
    /// WriteMembers, nor after a collection's elements): the region's length and those members (keyed: with their
    /// headers) in one reservation, one capacity check rather than two.
    /// </summary>
    private bool TryEmitRegionWithBlock()
    {
        if (Delegates || _m.IsCollection)
            return false;
        var groups = RawBlock.Groups(OwnMembers);
        if (!groups.MoveNext() || groups.Current[0].Raw == null)
            return false;
        _blocks = 0;
        var first = groups.Current;
        var block = new RawBlock(first, headers: _m.Keyed, start: NinoWire.LengthSize);
        if (first.Count == OwnMembers.Count)
        {
            // Raw stores cannot defer any bodies, so there is no open region or length to patch.
            EmitRawBlock(first, block, $"writer.ReserveFixedRegion({block.Plus(-NinoWire.LengthSize)})");
            return true;
        }

        bool patchOnly = CanPatchBody;
        if (patchOnly)
            _w.Line("int __length = writer.Position;");
        EmitRawBlock(first, block, patchOnly ? $"writer.Reserve({block.Total})" : $"writer.BeginRegion({block.Total}, out int __length)");
        if (_m.Keyed)
            EmitKeyedWrites(groups);
        else
            EmitPositionalWrites(groups);
        _w.Line(patchOnly ? "writer.PatchLength(__length);" : "writer.EndRegion(__length);");
        return true;
    }

    /// <summary>
    /// The raw members of <paramref name="block"/> (keyed: each after its key and length) in the reservation that
    /// <paramref name="reserve"/> returns.
    /// </summary>
    private string EmitRawBlock(MemberRange group, RawBlock block, string reserve)
    {
        string p = "__p" + _blocks++;
        _w.Line($"ref byte {p} = ref {reserve};");
        bool headers = block.HeaderOffsets.Length != 0;
        for (int i = 0; i < group.Count; i++)
        {
            if (headers)
            {
                var member = group[i];
                string keyOffset = block.HeaderOffsets[2 * i];
                // Writer only accepts little-endian hosts, so the key occupies the low word.
                if (member.Raw is { IsConstant: true } raw)
                    _w.Line($"{CoreCompilerServices}NinoUnsafe.Write(ref {p}, {keyOffset}, {PackKeyHeader(member.Key, raw.Bytes)}L);");
                else
                {
                    _w.Line($"{CoreCompilerServices}NinoUnsafe.Write(ref {p}, {keyOffset}, {member.Key});");
                    _w.Line($"{CoreCompilerServices}NinoUnsafe.Write(ref {p}, {block.HeaderOffsets[2 * i + 1]}, {member.Raw!.Expression});");
                }
            }

            _w.Line(RawWrite(group[i], p, block.Offsets[i]));
        }

        return p;
    }

    private void EmitPositionalWrites(MemberGroups groups)
    {
        while (groups.MoveNext())
        {
            var group = groups.Current;
            var first = group[0];
            if (first.Raw == null)
            {
                EmitFormatterWrite(first);
                continue;
            }

            if (group.Count == 1)
            {
                if (WriteFrom(first) != null)
                {
                    _w.Line(RawWrite(first, $"writer.Reserve({first.Raw!.Expression})", "0"));
                    continue;
                }

                string method = RawBlock.IsStruct(first) ? "WriteValue" : "UnsafeWrite";
                _w.Line($"writer.{method}<{first.TypeName}>({Get(_m, first, "value")});");
                continue;
            }

            var block = new RawBlock(group);
            EmitRawBlock(group, block, $"writer.Reserve({block.Total})");
        }
    }

    private void EmitFormatterWrite(MemberModel member)
    {
        string value = Get(_m, member, "value");
        _w.Line(DirectWrite(member, value) ?? $"default({Formatter(member)}).Serialize({value}, ref writer);");
    }

    // Struct members may have padding, which WriteValue clears.
    private string RawWrite(MemberModel member, string p, string offset)
    {
        string method = RawBlock.IsStruct(member) ? "WriteValue" : "Write";
        return WriteFrom(member) is { } field
            ? $"{CoreCompilerServices}NinoUnsafe.{method}In(ref {p}, {offset}, in {field});"
            : $"{CoreCompilerServices}NinoUnsafe.{method}(ref {p}, {offset}, {Get(_m, member, "value")});";
    }

    /// <summary>
    /// The field to write a raw member from by reference (<c>NinoUnsafe.WriteIn</c>), or null to pass it by value:
    /// fields wider than 8 bytes (Guid, decimal, structs), which by value the JIT copies to the stack and loads
    /// again. Narrower values are better by value, where the load folds the field offset.
    /// </summary>
    private string? WriteFrom(MemberModel member) =>
        !member.IsProperty && member.Access is MemberAccess.Direct or MemberAccess.Cast &&
        (RawBlock.IsStruct(member) || member.Raw!.Bytes > 8)
            ? MemberCode.Ref(_m, member, "value")
            : null;
}
