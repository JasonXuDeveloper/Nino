using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Nino.Generator.Generation.Emit;

/// <summary>
/// Consecutive raw members written into (or read from) one reservation. Offsets are expressions: constant sizes
/// are summed, struct sizes stay symbolic (<c>NinoUnsafe.SizeOf&lt;T&gt;()</c>).
/// </summary>
internal sealed class RawBlock
{
    /// <summary>Size of a keyed member's key, which its length follows.</summary>
    private const int KeySize = NinoWire.KeySize;

    /// <summary>Size of a keyed member's header: key and length.</summary>
    private const int HeaderSize = KeySize + NinoWire.LengthSize;

    private readonly List<string> _symbolic = new();
    private int _constant;

    /// <summary>Byte offset of each member's value.</summary>
    public readonly List<string> Offsets = new();

    /// <summary>Byte offsets of each member's key and length, when built with headers (two per member).</summary>
    public readonly List<string> HeaderOffsets = new();

    /// <summary>Total size expression.</summary>
    public string Total => Sum(0);

    /// <summary>Size expression of the block followed by <paramref name="extra"/> more bytes.</summary>
    public string Plus(int extra) => Sum(extra);

    /// <param name="run">The members.</param>
    /// <param name="headers">Whether each value is preceded by an 8-byte (key, length) header (keyed bodies).</param>
    /// <param name="start">Bytes in front of the first member (a region's length).</param>
    public RawBlock(IReadOnlyList<MemberModel> run, bool headers = false, int start = 0)
    {
        _constant = start;
        foreach (var member in run)
        {
            if (headers)
            {
                HeaderOffsets.Add(Sum(0));
                HeaderOffsets.Add(Sum(KeySize));
                _constant += HeaderSize;
            }

            Offsets.Add(Sum(0));
            var raw = member.Raw!;
            if (raw.IsConstant)
                _constant += raw.Bytes;
            else
                _symbolic.Add(raw.Expression);
        }
    }

    private string Sum(int extra) => Size(_constant + extra, _symbolic);

    /// <summary>
    /// A size as C#: <paramref name="constant"/> bytes plus the <paramref name="symbolic"/> sizes, a size that occurs
    /// n times written once, as <c>n * size</c> (offsets in a block of n structs of one type would otherwise repeat
    /// it up to n times each).
    /// </summary>
    public static string Size(int constant, IReadOnlyList<string> symbolic)
    {
        if (symbolic.Count == 0)
            return constant.ToString(CultureInfo.InvariantCulture);
        var parts = new List<string>(symbolic.Count + 1);
        if (constant != 0)
            parts.Add(constant.ToString(CultureInfo.InvariantCulture));
        // In the order each size first occurs.
        foreach (var size in symbolic.GroupBy(static size => size, System.StringComparer.Ordinal))
        {
            int count = size.Count();
            parts.Add(count == 1 ? size.Key : $"{count.ToString(CultureInfo.InvariantCulture)} * {size.Key}");
        }

        return string.Join(" + ", parts);
    }

    /// <summary>Splits members into runs of raw members (one block each) and single non-raw members.</summary>
    public static List<List<MemberModel>> Groups(IEnumerable<MemberModel> members)
    {
        var groups = new List<List<MemberModel>>();
        List<MemberModel>? run = null;
        foreach (var member in members)
        {
            if (member.Raw == null)
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
    public static bool IsStruct(MemberModel member) => member.Raw is { IsConstant: false };
}
