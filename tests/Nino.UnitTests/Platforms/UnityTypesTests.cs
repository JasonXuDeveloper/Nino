using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;
using UnityEngine;
using static Nino.UnitTests.Rt;

#nullable disable
namespace Nino.UnitTests;

[NinoType]
public class UnityClassHolder
{
    public AnimationCurve Curve;
    public Gradient Gradient;
    public RectOffset Padding;
}

/// <summary>
/// Nino.UnityTypes against the stand-in for UnityEngine.CoreModule: the bytes and round trips. Unity's own classes are
/// tested in the Unity project (Assets/Test/Editor/Tests/UnityTypesTests.cs).
/// </summary>
[TestClass]
public class UnityTypesTests
{
    [TestMethod]
    public void AnimationCurves()
    {
        var curve = new AnimationCurve(new Keyframe(0, 1, 2, 3, 4, 5) { weightedMode = WeightedMode.In }, new Keyframe(1, 2))
        {
            preWrapMode = WrapMode.PingPong,
            postWrapMode = WrapMode.ClampForever,
        };
        var bytes = NinoSerializer.Serialize(curve);
        Assert.AreEqual(4 + 2 * 28 + 8, bytes.Length);
        var back = NinoDeserializer.Deserialize<AnimationCurve>(bytes);
        CollectionAssert.AreEqual(curve.keys, back.keys);
        Assert.AreEqual(WrapMode.PingPong, back.preWrapMode);
        Assert.AreEqual(WrapMode.ClampForever, back.postWrapMode);
        Assert.IsNull(RoundTrip((AnimationCurve)null));

        var existing = new AnimationCurve();
        var kept = existing;
        NinoDeserializer.Deserialize(bytes, ref existing);
        Assert.AreSame(kept, existing);
        CollectionAssert.AreEqual(curve.keys, existing.keys);
    }

    [TestMethod]
    public void UnityTypesWriteTheirDocumentedBytes()
    {
        // The layouts in the formatters' summaries, value by value.
        var curve = new AnimationCurve(new Keyframe(0.5f, 1, 2, 3, 4, 5) { weightedMode = WeightedMode.Both })
        {
            preWrapMode = WrapMode.Loop,
            postWrapMode = WrapMode.PingPong,
        };
        CollectionAssert.AreEqual(Bytes(1, 0.5f, 1f, 2f, 3f, 4f, 5f, (int)WeightedMode.Both, (int)WrapMode.Loop,
            (int)WrapMode.PingPong), NinoSerializer.Serialize(curve));

        var gradient = new Gradient
        {
            colorKeys = new[] { new GradientColorKey(new Color(0.1f, 0.2f, 0.3f, 0.4f), 0.5f) },
            alphaKeys = new[] { new GradientAlphaKey(0.6f, 0.7f), new GradientAlphaKey(0.8f, 0.9f) },
            mode = GradientMode.Fixed,
        };
        CollectionAssert.AreEqual(Bytes(1, 0.1f, 0.2f, 0.3f, 0.4f, 0.5f, 2, 0.6f, 0.7f, 0.8f, 0.9f,
            (int)GradientMode.Fixed), NinoSerializer.Serialize(gradient));

        var offset = new System.Collections.Generic.List<byte> { 1 };
        offset.AddRange(Bytes(1, -2, 3, 4));
        CollectionAssert.AreEqual(offset.ToArray(), NinoSerializer.Serialize(new RectOffset(1, -2, 3, 4)));
        CollectionAssert.AreEqual(new byte[] { 0 }, NinoSerializer.Serialize((RectOffset)null));

        static byte[] Bytes(params object[] values)
        {
            var bytes = new System.Collections.Generic.List<byte>();
            foreach (var v in values)
                bytes.AddRange(v is float f ? BitConverter.GetBytes(f) : BitConverter.GetBytes((int)v));
            return bytes.ToArray();
        }
    }

    [TestMethod]
    public void GradientsAndRectOffsets()
    {
        var gradient = new Gradient
        {
            colorKeys = new[] { new GradientColorKey(new Color(1, 0, 0, 1), 0), new GradientColorKey(new Color(0, 0.5f, 1, 1), 1) },
            alphaKeys = new[] { new GradientAlphaKey(0.5f, 0.25f) },
            mode = GradientMode.PerceptualBlend,
        };
        var back = RoundTrip(gradient);
        CollectionAssert.AreEqual(gradient.colorKeys, back.colorKeys);
        CollectionAssert.AreEqual(gradient.alphaKeys, back.alphaKeys);
        Assert.AreEqual(GradientMode.PerceptualBlend, back.mode);
        Assert.IsNull(RoundTrip((Gradient)null));

        var offset = RoundTrip(new RectOffset(1, -2, 3, 4));
        Assert.AreEqual((1, -2, 3, 4), (offset.left, offset.right, offset.top, offset.bottom));
        Assert.AreEqual(17, NinoSerializer.Serialize(new RectOffset()).Length);
        Assert.IsNull(RoundTrip((RectOffset)null));
    }

    [TestMethod]
    public void RectOffsetPresenceIsZeroOrOne()
    {
        // A presence byte no writer writes is invalid data, as for every other nullable value.
        byte[] data = NinoSerializer.Serialize(new RectOffset(1, -2, 3, 4));
        data[0] = 2;
        Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<RectOffset>(data));
    }

    [TestMethod]
    public void AsMembers()
    {
        var back = RoundTrip(new UnityClassHolder
        {
            Curve = new AnimationCurve(new Keyframe(0.5f, 7)),
            Gradient = new Gradient(),
            Padding = new RectOffset(9, 8, 7, 6),
        });
        Assert.AreEqual(7, back.Curve.keys[0].value);
        Assert.AreEqual(2, back.Gradient.colorKeys.Length);
        Assert.AreEqual(6, back.Padding.bottom);
        var empty = RoundTrip(new UnityClassHolder());
        Assert.IsNull(empty.Curve);
        Assert.IsNull(empty.Gradient);
        Assert.IsNull(empty.Padding);
    }

    [TestMethod]
    public void TruncatedDataThrows()
    {
        var bytes = NinoSerializer.Serialize(new AnimationCurve(new Keyframe(0, 1)));
        Assert.ThrowsExactly<NinoDataException>(() =>
            NinoDeserializer.Deserialize<AnimationCurve>(bytes.AsSpan(0, bytes.Length - 1)));
    }

    [TestMethod]
    public void NullAlphaKeyCountThrows()
    {
        // No color keys, then -1 (null) where the alpha key count goes, then the mode.
        var bytes = new byte[] { 0, 0, 0, 0, 0xFF, 0xFF, 0xFF, 0xFF, 0, 0, 0, 0 };
        Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<Gradient>(bytes));
    }
}
