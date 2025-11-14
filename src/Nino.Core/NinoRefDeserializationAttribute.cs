using System;

namespace Nino.Core
{
    /// <summary>
    /// Mark a public static parameterless method that returns an instance of the type.
    /// This method will be called to instantiate objects during deserialization,
    /// allowing custom object pooling or initialization logic.
    /// <para>
    /// The method must be:
    /// - Public
    /// - Static
    /// - Parameterless
    /// - Return the same type as the declaring class
    /// </para>
    /// <para>
    /// When this attribute is present:
    /// - In Deserialize: calls this method, then uses DeserializeRef to populate the instance
    /// - In DeserializeRef: calls this method only if the ref parameter is null
    /// </para>
    /// </summary>
    [AttributeUsage(AttributeTargets.Method)]
    public class NinoRefDeserializationAttribute : Attribute
    {
    }
}
