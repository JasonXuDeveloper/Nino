namespace Nino.Generator.Common;

/// <summary>
/// Names of Nino.Core for the places that cannot compare symbols: syntax-only filters, attributes of referenced
/// assemblies read without a compilation, and metadata lookups. Compiled into both the generator and the code fixes.
/// </summary>
internal static class NinoNames
{
    /// <summary>Name of the runtime assembly.</summary>
    public const string CoreAssembly = "Nino.Core";

    /// <summary>Namespace of the runtime API.</summary>
    public const string CoreNamespace = "Nino.Core";

    /// <summary><c>[NinoType]</c> as written in source, and its type name.</summary>
    public const string NinoType = "NinoType", NinoTypeAttribute = NinoType + "Attribute";

    /// <summary><c>[NinoMember]</c> and <c>[NinoIgnore]</c> as written in source.</summary>
    public const string NinoMember = "NinoMember", NinoIgnore = "NinoIgnore";

    /// <summary>Metadata name of <c>[NinoType]</c>.</summary>
    public const string NinoTypeAttributeMetadataName = CoreNamespace + "." + NinoTypeAttribute;

    /// <summary>The static classes of the top-level API.</summary>
    public const string Serializer = "NinoSerializer", Deserializer = "NinoDeserializer";

    /// <summary>Metadata name of the Nino.Core type <paramref name="name"/> (e.g. <c>NinoFormatter`1</c>).</summary>
    public static string Core(string name) => CoreNamespace + "." + name;
}
