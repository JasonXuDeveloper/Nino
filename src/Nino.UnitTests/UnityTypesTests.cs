using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;
using UnityEngine;

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
    private static T RoundTrip<T>(T value) => NinoDeserializer.Deserialize<T>(NinoSerializer.Serialize(value));

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
        Assert.ThrowsExactly<System.InvalidOperationException>(() =>
            NinoDeserializer.Deserialize<AnimationCurve>(bytes.AsSpan(0, bytes.Length - 1)));
    }
}
