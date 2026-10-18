using Nino.Core;
using NUnit.Framework;
using UnityEngine;

namespace Test.Editor.Tests
{
    [NinoType]
    public class UnityClassHolder
    {
        public AnimationCurve Curve;
        public Gradient Gradient;
        public RectOffset Padding;
    }

    /// <summary>
    /// The Unity classes of Nino.UnityTypes (Runtime/Nino.UnityTypes.dll) against Unity's own UnityEngine.CoreModule:
    /// round trips, null, reuse, and as members of a [NinoType].
    /// </summary>
    public class UnityTypesTests
    {
        private static T RoundTrip<T>(T value) => NinoDeserializer.Deserialize<T>(NinoSerializer.Serialize(value));

        private static AnimationCurve Curve()
        {
            var curve = new AnimationCurve(
                new Keyframe(0, 0, 0.5f, 1.5f, 0.25f, 0.75f) { weightedMode = WeightedMode.Both },
                new Keyframe(0.5f, 2, -1, 1),
                new Keyframe(1, 1));
            curve.preWrapMode = WrapMode.PingPong;
            curve.postWrapMode = WrapMode.Loop;
            return curve;
        }

        private static void AssertSame(AnimationCurve expected, AnimationCurve actual)
        {
            Assert.AreEqual(expected.preWrapMode, actual.preWrapMode);
            Assert.AreEqual(expected.postWrapMode, actual.postWrapMode);
            Assert.AreEqual(expected.keys.Length, actual.keys.Length);
            for (int i = 0; i < expected.keys.Length; i++)
            {
                Keyframe a = expected.keys[i], b = actual.keys[i];
                Assert.AreEqual(a.time, b.time);
                Assert.AreEqual(a.value, b.value);
                Assert.AreEqual(a.inTangent, b.inTangent);
                Assert.AreEqual(a.outTangent, b.outTangent);
                Assert.AreEqual(a.inWeight, b.inWeight);
                Assert.AreEqual(a.outWeight, b.outWeight);
                Assert.AreEqual(a.weightedMode, b.weightedMode);
            }

            Assert.AreEqual(expected.Evaluate(0.3f), actual.Evaluate(0.3f));
        }

        private static Gradient Gradient()
        {
            var gradient = new Gradient { mode = GradientMode.Fixed };
            gradient.SetKeys(
                new[] { new GradientColorKey(Color.red, 0), new GradientColorKey(new Color(0.1f, 0.2f, 0.3f, 1), 0.6f) },
                new[] { new GradientAlphaKey(1, 0), new GradientAlphaKey(0.25f, 1) });
            return gradient;
        }

        [Test]
        public void AnimationCurves()
        {
            var curve = Curve();
            AssertSame(curve, RoundTrip(curve));
            Assert.IsNull(RoundTrip((AnimationCurve)null));
            Assert.AreEqual(0, RoundTrip(new AnimationCurve()).keys.Length);

            // An existing curve is refilled.
            var existing = AnimationCurve.Linear(0, 0, 1, 1);
            var kept = existing;
            NinoDeserializer.Deserialize(NinoSerializer.Serialize(curve), ref existing);
            Assert.AreSame(kept, existing);
            AssertSame(curve, existing);
        }

        [Test]
        public void Gradients()
        {
            var gradient = Gradient();
            var back = RoundTrip(gradient);
            Assert.AreEqual(gradient.mode, back.mode);
            Assert.AreEqual(gradient.colorKeys, back.colorKeys);
            Assert.AreEqual(gradient.alphaKeys, back.alphaKeys);
            Assert.AreEqual(gradient.Evaluate(0.4f), back.Evaluate(0.4f));
            Assert.IsNull(RoundTrip((Gradient)null));
        }

        [Test]
        public void RectOffsets()
        {
            var offset = new RectOffset(1, -2, 3, 40000);
            var back = RoundTrip(offset);
            Assert.AreEqual(offset.left, back.left);
            Assert.AreEqual(offset.right, back.right);
            Assert.AreEqual(offset.top, back.top);
            Assert.AreEqual(offset.bottom, back.bottom);
            Assert.IsNull(RoundTrip((RectOffset)null));
        }

        [Test]
        public void AsMembers()
        {
            var holder = new UnityClassHolder { Curve = Curve(), Gradient = Gradient(), Padding = new RectOffset(4, 3, 2, 1) };
            var back = RoundTrip(holder);
            AssertSame(holder.Curve, back.Curve);
            Assert.AreEqual(holder.Gradient.colorKeys, back.Gradient.colorKeys);
            Assert.AreEqual(holder.Padding.ToString(), back.Padding.ToString());

            var empty = RoundTrip(new UnityClassHolder());
            Assert.IsNull(empty.Curve);
            Assert.IsNull(empty.Gradient);
            Assert.IsNull(empty.Padding);
        }
    }
}
