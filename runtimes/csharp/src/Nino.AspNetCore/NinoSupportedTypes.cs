using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Nino.Core;

namespace Nino.AspNetCore
{
    /// <summary>
    /// <see cref="NinoSerializer.IsSupported"/> results, cached because MVC asks on every request through
    /// <see cref="NinoInputFormatter"/> and <see cref="NinoOutputFormatter"/>. Negative results are cached only without
    /// dynamic code (Native AOT): there every registration is generated and no assembly loads later, while
    /// <see cref="NinoSerializer.IsSupported"/> of a type it cannot write throws and catches an exception each call.
    /// </summary>
    internal static class NinoSupportedTypes
    {
        private static readonly ConcurrentDictionary<Type, bool> Supported = new();

        /// <summary>
        /// Whether Nino reads and writes <paramref name="type"/>. A type from a collectible assembly is not cached, so
        /// the cache never keeps its <c>AssemblyLoadContext</c> from unloading.
        /// </summary>
        public static bool IsSupported(Type type) =>
            Supported.TryGetValue(type, out bool supported) ? supported : CheckSupported(type);

        private static bool CheckSupported(Type type)
        {
            bool supported = NinoSerializer.IsSupported(type);
            // With dynamic code a formatter can be registered after a negative lookup.
            if ((supported || !RuntimeFeature.IsDynamicCodeSupported) && !MentionsCollectible(type))
                Supported.TryAdd(type, supported);
            return supported;
        }

        /// <summary>Whether <paramref name="type"/>, its element type or a type argument is in a collectible assembly.</summary>
        internal static bool MentionsCollectible(Type type)
        {
            if (type.Assembly.IsCollectible)
                return true;
            if (type.HasElementType)
                return MentionsCollectible(type.GetElementType()!);
            if (type.IsGenericType)
            {
                foreach (var argument in type.GetGenericArguments())
                {
                    if (MentionsCollectible(argument))
                        return true;
                }
            }

            return false;
        }

        internal static bool IsCached(Type type) => Supported.ContainsKey(type);
    }
}
