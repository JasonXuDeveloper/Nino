using System;
using System.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;
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

    /// <summary>
    /// <c>Convert.FromHexString</c>, which the netstandard2.1 build (compiled for Unity) does not have; spaces between
    /// the bytes are ignored.
    /// </summary>
    public static byte[] FromHex(string hex)
    {
        hex = hex.Replace(" ", "");
        var bytes = new byte[hex.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
            bytes[i] = System.Convert.ToByte(hex.Substring(i * 2, 2), 16);
        return bytes;
    }

    /// <summary>The bytes as upper-case hex digits, nothing between them (<c>Convert.ToHexString</c>'s form).</summary>
    public static string ToHex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "");

    /// <summary>The stack of the threads <see cref="OnSmallStack"/> runs tests on by default: 512 KB.</summary>
    public const int SmallStack = 512 * 1024;

    /// <summary>
    /// Runs <paramref name="action"/> on a new thread with a stack of <paramref name="stackSize"/> bytes, so that what
    /// nests without a bound fails there, not in the test host; what it throws fails the test.
    /// </summary>
    public static void OnSmallStack(Action action, int stackSize = SmallStack)
    {
        Exception error = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                error = e;
            }
        }, stackSize);
        thread.Start();
        thread.Join();
        if (error != null)
            throw new AssertFailedException("failed on the small stack: " + error, error);
    }
}
