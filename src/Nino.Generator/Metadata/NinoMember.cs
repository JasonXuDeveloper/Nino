using Microsoft.CodeAnalysis;

namespace Nino.Generator.Metadata;

public class NinoMember
{
    public string Name { get; set; }
    public ITypeSymbol Type { get; set; }
    public bool IsCtorParameter { get; set; }
    public bool IsPrivate { get; set; }
    public bool IsProperty { get; set; }
    public bool IsUtf8String { get; set; }

    public NinoMember(string name, ITypeSymbol type)
    {
        Name = name;
        Type = type;
    }

    public override string ToString()
    {
        return
            $"{Type.ToDisplayString()} {Name} " +
            $"[Ctor: {IsCtorParameter}, " +
            $"Private: {IsPrivate}, " +
            $"Property: {IsProperty}, " +
            $"Utf8String: {IsUtf8String}]";
    }
}