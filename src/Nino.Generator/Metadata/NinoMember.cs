using Microsoft.CodeAnalysis;
using System.Linq;

namespace Nino.Generator.Metadata;

public class NinoMember(string name, ITypeSymbol type, ISymbol memberSymbol)
{
    public string Name { get; set; } = name;
    public ITypeSymbol Type { get; set; } = type.GetNormalizedTypeSymbol().GetPureType();
    public ISymbol MemberSymbol { get; set; } = memberSymbol;
    public bool IsCtorParameter { get; set; }
    public bool IsPrivate { get; set; }
    public bool IsProperty { get; set; }
    public bool IsUtf8String { get; set; }

    // Track if we've already reported NINO011 warning for this member to avoid duplicates
    internal bool HasReportedUnrecognizableTypeWarning { get; set; }

    public bool HasCustomFormatter()
    {
        return MemberSymbol.GetAttributes()
            .Any(attr => attr.AttributeClass?.Name == "NinoCustomFormatterAttribute");
    }

    public ITypeSymbol? CustomFormatterType()
    {
        var customFormatterAttr = MemberSymbol.GetAttributes()
            .FirstOrDefault(attr => attr.AttributeClass?.Name == "NinoCustomFormatterAttribute");
        
        if (customFormatterAttr?.ConstructorArguments.Length > 0)
        {
            var arg = customFormatterAttr.ConstructorArguments[0];
            if (arg.Value is ITypeSymbol typeSymbol)
                return typeSymbol;
        }
        return null;
    }

    public override string ToString()
    {
        return
            $"{Type.GetDisplayString()} {Name} " +
            $"[Ctor: {IsCtorParameter}, " +
            $"Private: {IsPrivate}, " +
            $"Property: {IsProperty}, " +
            $"Utf8String: {IsUtf8String}]";
    }
}