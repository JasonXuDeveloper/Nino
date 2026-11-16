// The other libraries' attributes on the benchmark models, as attributes that do nothing: the models build in C# 9
// without MemoryPack, MessagePack, protobuf-net or LuminPack (Nino.Benchmark.Schema.csproj).
#pragma warning disable CS1591
using System;

namespace MemoryPack
{
    public enum GenerateType { Object, VersionTolerant, CircularReference, Collection, NoGenerate }
    public sealed class MemoryPackableAttribute : Attribute { public MemoryPackableAttribute(GenerateType type = GenerateType.Object) { } }
    [AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
    public sealed class MemoryPackUnionAttribute : Attribute { public MemoryPackUnionAttribute(ushort tag, Type type) { } }
    public sealed class MemoryPackOrderAttribute : Attribute { public MemoryPackOrderAttribute(int order) { } }
    public sealed class MemoryPackIgnoreAttribute : Attribute { }
}

namespace MessagePack
{
    public sealed class MessagePackObjectAttribute : Attribute
    {
        public MessagePackObjectAttribute(bool keyAsPropertyName = false) { }
        public bool AllowPrivate { get; set; }
    }

    public sealed class KeyAttribute : Attribute
    {
        public KeyAttribute(int x) { }
        public KeyAttribute(string x) { }
    }

    [AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
    public sealed class UnionAttribute : Attribute { public UnionAttribute(int key, Type subType) { } }
    public sealed class IgnoreMemberAttribute : Attribute { }
}

namespace ProtoBuf
{
    public sealed class ProtoContractAttribute : Attribute { public bool SkipConstructor { get; set; } }
    public sealed class ProtoMemberAttribute : Attribute
    {
        public ProtoMemberAttribute(int tag) { }
        public bool IsPacked { get; set; }
        public bool IsRequired { get; set; }
    }

    [AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
    public sealed class ProtoIncludeAttribute : Attribute { public ProtoIncludeAttribute(int tag, Type knownType) { } }
    public sealed class ProtoIgnoreAttribute : Attribute { }
    public sealed class NullWrappedValueAttribute : Attribute { public bool AsGroup { get; set; } }
    public enum CompatibilityLevel { NotSpecified = 0, Level200 = 200, Level240 = 240, Level300 = 300 }
    public sealed class CompatibilityLevelAttribute : Attribute { public CompatibilityLevelAttribute(CompatibilityLevel level) { } }
}

namespace LuminPack.Attribute
{
    public enum GeneratorType { Object, VersionTolerant, CircularReference }
    public sealed class LuminPackableAttribute : System.Attribute { public LuminPackableAttribute(GeneratorType type = GeneratorType.Object) { } }
    [AttributeUsage(AttributeTargets.All, AllowMultiple = true)]
    public sealed class LuminPackUnionAttribute : System.Attribute { public LuminPackUnionAttribute(ushort tag, Type type) { } }
    public sealed class LuminPackOrderAttribute : System.Attribute { public LuminPackOrderAttribute(int order) { } }
}

namespace MemoryPack
{
    /// <summary>MemoryPack's nullable form of an unmanaged value, which the MemoryPack copies of the nullable models hold.</summary>
    public readonly struct SafeNullable<T> where T : unmanaged
    {
        private readonly T? _value;
        private SafeNullable(T? value) => _value = value;
        public static implicit operator SafeNullable<T>(T? value) => new SafeNullable<T>(value);
        public static implicit operator T?(SafeNullable<T> value) => value._value;
        public T? ToNullable() => _value;
    }
}
