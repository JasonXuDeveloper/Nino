using Microsoft.CodeAnalysis;

namespace Nino.Generator.Metadata;

public class NinoMember(string name, ITypeSymbol type)
{
    public string Name { get; set; } = name;
    public ITypeSymbol Type { get; set; } = type;
    public bool IsCtorParameter { get; set; }
    public bool IsPrivate { get; set; }
    public bool IsProperty { get; set; }
    public bool IsUtf8String { get; set; }

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