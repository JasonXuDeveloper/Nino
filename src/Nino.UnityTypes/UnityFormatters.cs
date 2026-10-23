using System.Runtime.CompilerServices;
using Nino.Core;
using Nino.UnityTypes;
using UnityEngine;

// Registered like Nino.Core's own BCL formatters: generated code and the runtime resolver both read these.
[assembly: NinoFormatter(typeof(AnimationCurveFormatter))]
[assembly: NinoFormatter(typeof(GradientFormatter))]
[assembly: NinoFormatter(typeof(RectOffsetFormatter))]

namespace Nino.UnityTypes
{
    /// <summary>
    /// <c>AnimationCurve</c>: key count (-1 = null), then each key's time, value, in and out tangents, in and out
    /// weights (floats) and weighted mode (int32), then the pre and post wrap modes (int32). Keys are written through
    /// their public properties, not as memory, so the bytes do not depend on how a Unity version lays a
    /// <c>Keyframe</c> out. An existing curve is refilled.
    /// </summary>
    public struct AnimationCurveFormatter : INinoFormatter<AnimationCurve>
    {
        public void Serialize(AnimationCurve value, ref Writer writer)
        {
            if (value == null)
            {
                writer.WriteNullLength();
                return;
            }

            // The count, the keys and the wrap modes in one reservation.
            var keys = value.keys;
            ref byte next = ref writer.Reserve(checked(sizeof(int) + keys.Length * KeySize + 2 * sizeof(int)));
            NinoUnsafe.Write(ref next, keys.Length);
            next = ref Unsafe.Add(ref next, sizeof(int));
            foreach (var key in keys)
            {
                NinoUnsafe.Write(ref next, 0, key.time);
                NinoUnsafe.Write(ref next, 4, key.value);
                NinoUnsafe.Write(ref next, 8, key.inTangent);
                NinoUnsafe.Write(ref next, 12, key.outTangent);
                NinoUnsafe.Write(ref next, 16, key.inWeight);
                NinoUnsafe.Write(ref next, 20, key.outWeight);
                NinoUnsafe.Write(ref next, 24, (int)key.weightedMode);
                next = ref Unsafe.Add(ref next, KeySize);
            }

            NinoUnsafe.Write(ref next, 0, (int)value.preWrapMode);
            NinoUnsafe.Write(ref next, 4, (int)value.postWrapMode);
        }

        private const int KeySize = 7 * sizeof(float);

        public void Deserialize(ref AnimationCurve value, ref Reader reader)
        {
            int count = reader.ReadLength(KeySize);
            if (count < 0)
            {
                value = null;
                return;
            }

            // The keys and the wrap modes under one bounds check.
            ref byte next = ref reader.ReadBlock(count * KeySize + 2 * sizeof(int));
            var keys = new Keyframe[count];
            for (int i = 0; i < keys.Length; i++)
            {
                keys[i] = new Keyframe(NinoUnsafe.Read<float>(ref next, 0), NinoUnsafe.Read<float>(ref next, 4),
                    NinoUnsafe.Read<float>(ref next, 8), NinoUnsafe.Read<float>(ref next, 12),
                    NinoUnsafe.Read<float>(ref next, 16), NinoUnsafe.Read<float>(ref next, 20))
                {
                    weightedMode = (WeightedMode)NinoUnsafe.Read<int>(ref next, 24),
                };
                next = ref Unsafe.Add(ref next, KeySize);
            }

            var curve = value ??= new AnimationCurve();
            curve.keys = keys;
            curve.preWrapMode = (WrapMode)NinoUnsafe.Read<int>(ref next, 0);
            curve.postWrapMode = (WrapMode)NinoUnsafe.Read<int>(ref next, 4);
        }
    }

    /// <summary>
    /// <c>Gradient</c>: color key count (-1 = null), then each key's red, green, blue, alpha and time (floats), then
    /// the alpha key count and each key's alpha and time (floats), then the mode (int32). An existing gradient is
    /// refilled.
    /// </summary>
    public struct GradientFormatter : INinoFormatter<Gradient>
    {
        public void Serialize(Gradient value, ref Writer writer)
        {
            if (value == null)
            {
                writer.WriteNullLength();
                return;
            }

            // Both counts, the keys and the mode in one reservation.
            var colorKeys = value.colorKeys;
            var alphaKeys = value.alphaKeys;
            ref byte next = ref writer.Reserve(checked(sizeof(int) + colorKeys.Length * ColorKeySize + sizeof(int) +
                                                       alphaKeys.Length * AlphaKeySize + sizeof(int)));
            NinoUnsafe.Write(ref next, colorKeys.Length);
            next = ref Unsafe.Add(ref next, sizeof(int));
            foreach (var key in colorKeys)
            {
                NinoUnsafe.Write(ref next, 0, key.color.r);
                NinoUnsafe.Write(ref next, 4, key.color.g);
                NinoUnsafe.Write(ref next, 8, key.color.b);
                NinoUnsafe.Write(ref next, 12, key.color.a);
                NinoUnsafe.Write(ref next, 16, key.time);
                next = ref Unsafe.Add(ref next, ColorKeySize);
            }

            NinoUnsafe.Write(ref next, alphaKeys.Length);
            next = ref Unsafe.Add(ref next, sizeof(int));
            foreach (var key in alphaKeys)
            {
                NinoUnsafe.Write(ref next, 0, key.alpha);
                NinoUnsafe.Write(ref next, 4, key.time);
                next = ref Unsafe.Add(ref next, AlphaKeySize);
            }

            NinoUnsafe.Write(ref next, (int)value.mode);
        }

        private const int ColorKeySize = 5 * sizeof(float);
        private const int AlphaKeySize = 2 * sizeof(float);

        public void Deserialize(ref Gradient value, ref Reader reader)
        {
            int colorCount = reader.ReadLength(ColorKeySize);
            if (colorCount < 0)
            {
                value = null;
                return;
            }

            // Each run of keys under one bounds check.
            ref byte next = ref reader.ReadBlock(colorCount * ColorKeySize);
            var colorKeys = new GradientColorKey[colorCount];
            for (int i = 0; i < colorKeys.Length; i++)
            {
                var color = new Color(NinoUnsafe.Read<float>(ref next, 0), NinoUnsafe.Read<float>(ref next, 4),
                    NinoUnsafe.Read<float>(ref next, 8), NinoUnsafe.Read<float>(ref next, 12));
                colorKeys[i] = new GradientColorKey(color, NinoUnsafe.Read<float>(ref next, 16));
                next = ref Unsafe.Add(ref next, ColorKeySize);
            }

            var alphaKeys = new GradientAlphaKey[reader.ReadLength(AlphaKeySize)];
            next = ref reader.ReadBlock(alphaKeys.Length * AlphaKeySize + sizeof(int));
            for (int i = 0; i < alphaKeys.Length; i++)
            {
                alphaKeys[i] =
                    new GradientAlphaKey(NinoUnsafe.Read<float>(ref next, 0), NinoUnsafe.Read<float>(ref next, 4));
                next = ref Unsafe.Add(ref next, AlphaKeySize);
            }

            var gradient = value ??= new Gradient();
            gradient.colorKeys = colorKeys;
            gradient.alphaKeys = alphaKeys;
            gradient.mode = (GradientMode)NinoUnsafe.Read<int>(ref next, 0);
        }
    }

    /// <summary>
    /// <c>RectOffset</c>: a has-value byte (0 = null), then left, right, top and bottom (int32). An existing offset is
    /// refilled.
    /// </summary>
    public struct RectOffsetFormatter : INinoFormatter<RectOffset>
    {
        public void Serialize(RectOffset value, ref Writer writer)
        {
            if (value == null)
            {
                writer.Write(false);
                return;
            }

            ref byte next = ref writer.Reserve(1 + 4 * sizeof(int));
            next = 1;
            NinoUnsafe.Write(ref next, 1, value.left);
            NinoUnsafe.Write(ref next, 5, value.right);
            NinoUnsafe.Write(ref next, 9, value.top);
            NinoUnsafe.Write(ref next, 13, value.bottom);
        }

        public void Deserialize(ref RectOffset value, ref Reader reader)
        {
            if (!reader.Read<bool>())
            {
                value = null;
                return;
            }

            ref byte sides = ref reader.ReadBlock(4 * sizeof(int));
            var offset = value ??= new RectOffset();
            offset.left = NinoUnsafe.Read<int>(ref sides, 0);
            offset.right = NinoUnsafe.Read<int>(ref sides, 4);
            offset.top = NinoUnsafe.Read<int>(ref sides, 8);
            offset.bottom = NinoUnsafe.Read<int>(ref sides, 12);
        }
    }
}
