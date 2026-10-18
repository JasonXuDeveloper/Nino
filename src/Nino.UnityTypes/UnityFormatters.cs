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

            var keys = value.keys;
            writer.WriteLength(keys.Length);
            foreach (var key in keys)
            {
                writer.Write(key.time);
                writer.Write(key.value);
                writer.Write(key.inTangent);
                writer.Write(key.outTangent);
                writer.Write(key.inWeight);
                writer.Write(key.outWeight);
                writer.Write((int)key.weightedMode);
            }

            writer.Write((int)value.preWrapMode);
            writer.Write((int)value.postWrapMode);
        }

        public void Deserialize(ref AnimationCurve value, ref Reader reader)
        {
            int count = reader.ReadLength(7 * sizeof(float));
            if (count < 0)
            {
                value = null;
                return;
            }

            var keys = new Keyframe[count];
            for (int i = 0; i < keys.Length; i++)
            {
                keys[i] = new Keyframe(reader.Read<float>(), reader.Read<float>(), reader.Read<float>(),
                    reader.Read<float>(), reader.Read<float>(), reader.Read<float>())
                {
                    weightedMode = (WeightedMode)reader.Read<int>(),
                };
            }

            var curve = value ??= new AnimationCurve();
            curve.keys = keys;
            curve.preWrapMode = (WrapMode)reader.Read<int>();
            curve.postWrapMode = (WrapMode)reader.Read<int>();
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

            var colorKeys = value.colorKeys;
            writer.WriteLength(colorKeys.Length);
            foreach (var key in colorKeys)
            {
                writer.Write(key.color.r);
                writer.Write(key.color.g);
                writer.Write(key.color.b);
                writer.Write(key.color.a);
                writer.Write(key.time);
            }

            var alphaKeys = value.alphaKeys;
            writer.WriteLength(alphaKeys.Length);
            foreach (var key in alphaKeys)
            {
                writer.Write(key.alpha);
                writer.Write(key.time);
            }

            writer.Write((int)value.mode);
        }

        public void Deserialize(ref Gradient value, ref Reader reader)
        {
            int colorCount = reader.ReadLength(5 * sizeof(float));
            if (colorCount < 0)
            {
                value = null;
                return;
            }

            var colorKeys = new GradientColorKey[colorCount];
            for (int i = 0; i < colorKeys.Length; i++)
            {
                var color = new Color(reader.Read<float>(), reader.Read<float>(), reader.Read<float>(),
                    reader.Read<float>());
                colorKeys[i] = new GradientColorKey(color, reader.Read<float>());
            }

            var alphaKeys = new GradientAlphaKey[reader.ReadLength(2 * sizeof(float))];
            for (int i = 0; i < alphaKeys.Length; i++)
                alphaKeys[i] = new GradientAlphaKey(reader.Read<float>(), reader.Read<float>());

            var gradient = value ??= new Gradient();
            gradient.colorKeys = colorKeys;
            gradient.alphaKeys = alphaKeys;
            gradient.mode = (GradientMode)reader.Read<int>();
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

            writer.Write(true);
            writer.Write(value.left);
            writer.Write(value.right);
            writer.Write(value.top);
            writer.Write(value.bottom);
        }

        public void Deserialize(ref RectOffset value, ref Reader reader)
        {
            if (!reader.Read<bool>())
            {
                value = null;
                return;
            }

            var offset = value ??= new RectOffset();
            offset.left = reader.Read<int>();
            offset.right = reader.Read<int>();
            offset.top = reader.Read<int>();
            offset.bottom = reader.Read<int>();
        }
    }
}
