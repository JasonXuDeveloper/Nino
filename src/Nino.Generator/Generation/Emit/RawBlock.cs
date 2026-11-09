using System.Collections.Generic;
using System.Globalization;
using Nino.Generator.Common;

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

    /// <summary>Whether <see cref="Total"/> is a number (no struct's size in it).</summary>
    public bool IsConstant => _symbolic.Count == 0;

    /// <summary>Size expression of the block followed by <paramref name="extra"/> more bytes.</summary>
    public string Plus(int extra) => Sum(extra);

    /// <param name="run">The members.</param>
    /// <param name="headers">Whether each value is preceded by an 8-byte (key, length) header (keyed bodies).</param>
    /// <param name="start">Bytes in front of the first member (a region's length).</param>
    public RawBlock(MemberRange run, bool headers = false, int start = 0)
    {
        _constant = start;
        for (int i = 0; i < run.Count; i++)
        {
            var member = run[i];
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

    /// <summary>A contiguous slice of a model's immutable member array.</summary>
    internal readonly struct MemberRange(EquatableArray<MemberModel> members, int start, int count)
    {
        public int Count => count;

        public MemberModel this[int index] => members[start + index];

        public MemberRange Slice(int offset, int length) => new(members, start + offset, length);
    }

    /// <summary>Splits members into runs of raw members (one block each) and single non-raw members.</summary>
    internal struct MemberGroups(MemberRange members)
    {
        private int _next;

        public MemberRange Current { get; private set; }

        public MemberGroups GetEnumerator() => this;

        public bool MoveNext()
        {
            if (_next == members.Count)
                return false;
            int start = _next++;
            if (members[start].Raw != null)
                while (_next < members.Count && members[_next].Raw != null)
                    _next++;
            Current = members.Slice(start, _next - start);
            return true;
        }
    }

    public static MemberGroups Groups(MemberRange members) => new(members);

    /// <summary>Whether the member is a struct (non-constant size), whose padding the write must clear.</summary>
    public static bool IsStruct(MemberModel member) => member.Raw is { IsConstant: false };
}
