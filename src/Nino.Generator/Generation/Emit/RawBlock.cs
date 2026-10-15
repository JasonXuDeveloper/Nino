using System.Collections.Generic;
using System.Globalization;

namespace Nino.Generator.Generation.Emit;

/// <summary>
/// Consecutive raw members written into (or read from) one reservation. Offsets are expressions: constant sizes
/// are summed, struct sizes stay symbolic (<c>NinoUnsafe.SizeOf&lt;T&gt;()</c>).
/// </summary>
internal sealed class RawBlock
{
    private readonly List<string> _symbolic = new();
    private int _constant;

    /// <summary>Byte offset of each member's value.</summary>
    public readonly List<string> Offsets = new();

    /// <summary>Byte offsets of each member's key and length, when built with headers (two per member).</summary>
    public readonly List<string> HeaderOffsets = new();

    /// <summary>Total size expression.</summary>
    public string Total => Sum(0);

    /// <param name="run">The members.</param>
    /// <param name="headers">Whether each value is preceded by an 8-byte (key, length) header (keyed bodies).</param>
    public RawBlock(IReadOnlyList<MemberModel> run, bool headers = false)
    {
        foreach (var member in run)
        {
            if (headers)
            {
                HeaderOffsets.Add(Sum(0));
                HeaderOffsets.Add(Sum(4));
                _constant += 8;
            }

            Offsets.Add(Sum(0));
            if (int.TryParse(member.RawSize, out int size))
                _constant += size;
            else
                _symbolic.Add(member.RawSize);
        }
    }

    private string Sum(int extra)
    {
        int constant = _constant + extra;
        if (_symbolic.Count == 0)
            return constant.ToString(CultureInfo.InvariantCulture);
        var parts = new List<string>(_symbolic);
        if (constant != 0)
            parts.Insert(0, constant.ToString(CultureInfo.InvariantCulture));
        return string.Join(" + ", parts);
    }

    /// <summary>Splits members into runs of raw members (one block each) and single non-raw members.</summary>
    public static List<List<MemberModel>> Groups(IEnumerable<MemberModel> members)
    {
        var groups = new List<List<MemberModel>>();
        List<MemberModel>? run = null;
        foreach (var member in members)
        {
            if (member.RawSize.Length == 0)
            {
                run = null;
                groups.Add(new List<MemberModel> { member });
                continue;
            }

            if (run == null)
                groups.Add(run = new List<MemberModel>());
            run.Add(member);
        }

        return groups;
    }

    /// <summary>Whether the member is a struct (non-constant size), whose padding the write must clear.</summary>
    public static bool IsStruct(MemberModel member) => !char.IsDigit(member.RawSize[0]);
}
