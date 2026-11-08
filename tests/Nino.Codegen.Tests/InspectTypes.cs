using Nino.Core;

// Types for InspectCommandTests that the conformance corpus does not have: members written by a custom formatter,
// which nino-gen cannot decode (each target leaves these types out with NINOGEN002, so the generated files of Shapes
// do not change).
namespace InspectSamples
{
    /// <summary>A long as a ZigZag LEB128 varint: 1 to 10 bytes, a length no layout states.</summary>
    public struct VarLong : INinoFormatter<long>
    {
        public void Serialize(long value, ref Writer writer)
        {
            ulong v = (ulong)((value << 1) ^ (value >> 63));
            while (v >= 0x80)
            {
                writer.Write((byte)(v | 0x80));
                v >>= 7;
            }

            writer.Write((byte)v);
        }

        public void Deserialize(ref Reader reader, ref long value)
        {
            ulong v = 0;
            for (int shift = 0; ; shift += 7)
            {
                reader.Read(out byte b);
                v |= (ulong)(b & 0x7F) << shift;
                if (b < 0x80)
                    break;
            }

            value = (long)(v >> 1) ^ -(long)(v & 1);
        }
    }

    /// <summary>A keyed body: the formatter's bytes fill their entry, so they are shown and skipped.</summary>
    [NinoType(format: NinoFormat.Keyed)]
    public sealed class KeyedCustom
    {
        public int Before;
        [NinoMemberFormatter(typeof(VarLong))] public long Delta;
        public string After;
    }

    /// <summary>A positional body: nothing says where the formatter's bytes end, so reading stops there.</summary>
    [NinoType]
    public sealed class PositionalCustom
    {
        public int Before;
        [NinoMemberFormatter(typeof(VarLong))] public long Delta;
        public string After;
    }
}
