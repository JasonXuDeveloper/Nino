using System;
using System.Reflection;
using Nino.Core;
using Nino.UnitTests;

namespace Nino.Fuzz.Golden
{
    /// <summary>
    /// The pinned bytes of <see cref="GoldenBytesTests"/> and the value they hold, read from the test class itself so
    /// the fuzzer's seed follows every intentional format change.
    /// </summary>
    public static class GoldenCorpus
    {
        public static byte[] Bytes()
        {
            var field = typeof(GoldenBytesTests).GetField("Expected", BindingFlags.NonPublic | BindingFlags.Static)
                        ?? throw new InvalidOperationException("GoldenBytesTests.Expected not found");
            var hex = (string)field.GetRawConstantValue();
            return Convert.FromHexString(hex);
        }

        public static GoldenData Sample()
        {
            var create = typeof(GoldenBytesTests).GetMethod("Create", BindingFlags.NonPublic | BindingFlags.Static)
                         ?? throw new InvalidOperationException("GoldenBytesTests.Create not found");
            return (GoldenData)create.Invoke(null, null);
        }

        public static GoldenData Read(ReadOnlySpan<byte> data) => NinoDeserializer.Deserialize<GoldenData>(data);

        public static GoldenData ReadInto(ReadOnlySpan<byte> data, GoldenData existing)
        {
            NinoDeserializer.Deserialize(data, ref existing);
            return existing;
        }

        public static byte[] Write(GoldenData value) => NinoSerializer.Serialize(value);
    }
}
