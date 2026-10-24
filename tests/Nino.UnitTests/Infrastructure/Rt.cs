using Nino.Core;

#nullable disable
namespace Nino.UnitTests;

/// <summary>Round trips through the public API, shared by the tests (<c>using static Nino.UnitTests.Rt;</c>).</summary>
internal static class Rt
{
    /// <summary>Serializes <paramref name="value"/> and deserializes it as a new <typeparamref name="T"/>.</summary>
    public static T RoundTrip<T>(T value) => NinoDeserializer.Deserialize<T>(NinoSerializer.Serialize(value));

    /// <summary>Serializes <paramref name="source"/> and deserializes it into <paramref name="target"/>.</summary>
    public static void Into<T>(T source, ref T target) =>
        NinoDeserializer.Deserialize(NinoSerializer.Serialize(source), ref target);

    /// <summary><c>Convert.FromHexString</c>, which the netstandard2.1 build (compiled for Unity) does not have.</summary>
    public static byte[] FromHex(string hex)
    {
        var bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = System.Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return bytes;
    }
}
