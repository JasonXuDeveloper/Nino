// The public API of the UnityEngine.CoreModule types Nino.UnityTypes uses, as Unity declares it, with plain storage
// behind it so the formatters can be tested on .NET. In Unity, Unity's own assembly runs.
#pragma warning disable CS1591

namespace UnityEngine
{
    public enum WrapMode
    {
        Default = 0,
        Once = 1,
        Clamp = 1,
        Loop = 2,
        PingPong = 4,
        ClampForever = 8,
    }

    public enum WeightedMode
    {
        None = 0,
        In = 1,
        Out = 2,
        Both = 3,
    }

    public enum GradientMode
    {
        Blend = 0,
        Fixed = 1,
        PerceptualBlend = 2,
    }

    public struct Color
    {
        public float r, g, b, a;

        public Color(float r, float g, float b, float a)
        {
            this.r = r;
            this.g = g;
            this.b = b;
            this.a = a;
        }
    }

    public struct Keyframe
    {
        public Keyframe(float time, float value) : this(time, value, 0, 0, 1 / 3f, 1 / 3f)
        {
        }

        public Keyframe(float time, float value, float inTangent, float outTangent, float inWeight, float outWeight)
        {
            this.time = time;
            this.value = value;
            this.inTangent = inTangent;
            this.outTangent = outTangent;
            this.inWeight = inWeight;
            this.outWeight = outWeight;
            weightedMode = WeightedMode.None;
        }

        public float time { get; set; }
        public float value { get; set; }
        public float inTangent { get; set; }
        public float outTangent { get; set; }
        public float inWeight { get; set; }
        public float outWeight { get; set; }
        public WeightedMode weightedMode { get; set; }
    }

    public sealed class AnimationCurve
    {
        private Keyframe[] _keys = System.Array.Empty<Keyframe>();

        public AnimationCurve()
        {
        }

        public AnimationCurve(params Keyframe[] keys) => this.keys = keys;

        // Unity copies the keys in and out.
        public Keyframe[] keys
        {
            get => (Keyframe[])_keys.Clone();
            set => _keys = (Keyframe[])value.Clone();
        }

        public WrapMode preWrapMode { get; set; }
        public WrapMode postWrapMode { get; set; }
    }

    public struct GradientColorKey
    {
        public Color color;
        public float time;

        public GradientColorKey(Color col, float time)
        {
            color = col;
            this.time = time;
        }
    }

    public struct GradientAlphaKey
    {
        public float alpha;
        public float time;

        public GradientAlphaKey(float alpha, float time)
        {
            this.alpha = alpha;
            this.time = time;
        }
    }

    public class Gradient
    {
        private GradientColorKey[] _colorKeys = { new GradientColorKey(new Color(1, 1, 1, 1), 0), new GradientColorKey(new Color(1, 1, 1, 1), 1) };
        private GradientAlphaKey[] _alphaKeys = { new GradientAlphaKey(1, 0), new GradientAlphaKey(1, 1) };

        // Unity copies the keys in and out.
        public GradientColorKey[] colorKeys
        {
            get => (GradientColorKey[])_colorKeys.Clone();
            set => _colorKeys = (GradientColorKey[])value.Clone();
        }

        public GradientAlphaKey[] alphaKeys
        {
            get => (GradientAlphaKey[])_alphaKeys.Clone();
            set => _alphaKeys = (GradientAlphaKey[])value.Clone();
        }

        public GradientMode mode { get; set; }
    }

    public class RectOffset
    {
        public RectOffset()
        {
        }

        public RectOffset(int left, int right, int top, int bottom)
        {
            this.left = left;
            this.right = right;
            this.top = top;
            this.bottom = bottom;
        }

        public int left { get; set; }
        public int right { get; set; }
        public int top { get; set; }
        public int bottom { get; set; }
    }
}
