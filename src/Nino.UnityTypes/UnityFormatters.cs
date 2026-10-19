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
            next = ref UnityBytes.Put(ref next, keys.Length);
            foreach (var key in keys)
            {
                next = ref UnityBytes.Put(ref next, key.time);
                next = ref UnityBytes.Put(ref next, key.value);
                next = ref UnityBytes.Put(ref next, key.inTangent);
                next = ref UnityBytes.Put(ref next, key.outTangent);
                next = ref UnityBytes.Put(ref next, key.inWeight);
                next = ref UnityBytes.Put(ref next, key.outWeight);
                next = ref UnityBytes.Put(ref next, (int)key.weightedMode);
            }

            next = ref UnityBytes.Put(ref next, (int)value.preWrapMode);
            UnityBytes.Put(ref next, (int)value.postWrapMode);
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
                keys[i] = new Keyframe(UnityBytes.Get<float>(ref next, 0), UnityBytes.Get<float>(ref next, 4),
                    UnityBytes.Get<float>(ref next, 8), UnityBytes.Get<float>(ref next, 12),
                    UnityBytes.Get<float>(ref next, 16), UnityBytes.Get<float>(ref next, 20))
                {
                    weightedMode = (WeightedMode)UnityBytes.Get<int>(ref next, 24),
                };
                next = ref Unsafe.Add(ref next, KeySize);
            }

            var curve = value ??= new AnimationCurve();
            curve.keys = keys;
            curve.preWrapMode = (WrapMode)UnityBytes.Get<int>(ref next, 0);
            curve.postWrapMode = (WrapMode)UnityBytes.Get<int>(ref next, 4);
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
            next = ref UnityBytes.Put(ref next, colorKeys.Length);
            foreach (var key in colorKeys)
            {
                next = ref UnityBytes.Put(ref next, key.color.r);
                next = ref UnityBytes.Put(ref next, key.color.g);
                next = ref UnityBytes.Put(ref next, key.color.b);
                next = ref UnityBytes.Put(ref next, key.color.a);
                next = ref UnityBytes.Put(ref next, key.time);
            }

            next = ref UnityBytes.Put(ref next, alphaKeys.Length);
            foreach (var key in alphaKeys)
            {
                next = ref UnityBytes.Put(ref next, key.alpha);
                next = ref UnityBytes.Put(ref next, key.time);
            }

            UnityBytes.Put(ref next, (int)value.mode);
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
                var color = new Color(UnityBytes.Get<float>(ref next, 0), UnityBytes.Get<float>(ref next, 4),
                    UnityBytes.Get<float>(ref next, 8), UnityBytes.Get<float>(ref next, 12));
                colorKeys[i] = new GradientColorKey(color, UnityBytes.Get<float>(ref next, 16));
                next = ref Unsafe.Add(ref next, ColorKeySize);
            }

            var alphaKeys = new GradientAlphaKey[reader.ReadLength(AlphaKeySize)];
            next = ref reader.ReadBlock(alphaKeys.Length * AlphaKeySize + sizeof(int));
            for (int i = 0; i < alphaKeys.Length; i++)
            {
                alphaKeys[i] =
                    new GradientAlphaKey(UnityBytes.Get<float>(ref next, 0), UnityBytes.Get<float>(ref next, 4));
                next = ref Unsafe.Add(ref next, AlphaKeySize);
            }

            var gradient = value ??= new Gradient();
            gradient.colorKeys = colorKeys;
            gradient.alphaKeys = alphaKeys;
            gradient.mode = (GradientMode)UnityBytes.Get<int>(ref next, 0);
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
            next = ref UnityBytes.Put(ref Unsafe.Add(ref next, 1), value.left);
            next = ref UnityBytes.Put(ref next, value.right);
            next = ref UnityBytes.Put(ref next, value.top);
            UnityBytes.Put(ref next, value.bottom);
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
            offset.left = UnityBytes.Get<int>(ref sides, 0);
            offset.right = UnityBytes.Get<int>(ref sides, 4);
            offset.top = UnityBytes.Get<int>(ref sides, 8);
            offset.bottom = UnityBytes.Get<int>(ref sides, 12);
        }
    }

    /// <summary>
    /// Values at a cursor in a block reserved or read as a whole, in the byte order Nino writes them (the machine's,
    /// little-endian).
    /// </summary>
    internal static class UnityBytes
    {
        /// <summary>Stores <paramref name="value"/> at <paramref name="at"/> and returns the byte after it.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref byte Put<T>(ref byte at, T value) where T : unmanaged
        {
            Unsafe.WriteUnaligned(ref at, value);
            return ref Unsafe.Add(ref at, Unsafe.SizeOf<T>());
        }

        /// <summary>The value at <paramref name="at"/> + <paramref name="offset"/>.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static T Get<T>(ref byte at, int offset) where T : unmanaged =>
            Unsafe.ReadUnaligned<T>(ref Unsafe.Add(ref at, offset));
    }
}
