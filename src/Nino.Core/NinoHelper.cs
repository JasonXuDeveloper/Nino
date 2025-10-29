using System;
using System.Runtime.CompilerServices;
using System.Text;

namespace Nino.Core
{
    /// <summary>
    /// Internal helper utilities for Nino serialization
    /// </summary>
    internal static class NinoHelper
    {
        /// <summary>
        /// Gets the generated namespace for a type's assembly.
        /// This must match the logic in NinoTypeHelper.GetNamespace()
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static string GetGeneratedNamespace(Type type)
        {
            var assemblyName = type.Assembly.GetName().Name ?? string.Empty;
            var sb = new StringBuilder();

            if (!string.IsNullOrEmpty(assemblyName))
            {
                foreach (var c in assemblyName.Split('.'))
                {
                    if (string.IsNullOrEmpty(c)) continue;
                    var part = c;
                    if (!char.IsLetter(part[0]) && part[0] != '_')
                        sb.Append('_');
                    for (int i = 0; i < part.Length; i++)
                    {
                        var ch = part[i];
                        if (char.IsLetterOrDigit(ch) || ch == '_')
                        {
                            sb.Append(ch);
                        }
                        else
                        {
                            sb.Append('_');
                        }
                    }

                    sb.Append('.');
                }
            }

            sb.Append("NinoGen");
            return sb.ToString();
        }

        /// <summary>
        /// Creates a standard error message for missing registration
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static string GetRegistrationErrorMessage(string typeFullName, string generatedNamespace)
        {
            return
                $"No serializer/deserializer registered for type {typeFullName}. You must call {generatedNamespace}.Serializer.Init(), {generatedNamespace}.Deserializer.Init(), and {generatedNamespace}.NinoBuiltInType.Init() once before serializing/deserializing this type. If you have already registered them, this means the type '{typeFullName}' is not supported by Nino or has not added attribute [NinoType].";
        }
    }
}