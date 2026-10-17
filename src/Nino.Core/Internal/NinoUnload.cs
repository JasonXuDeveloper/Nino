using System;
using System.Collections.Concurrent;
using System.Reflection;

namespace Nino.Core
{
    /// <summary>Helpers for <see cref="NinoRegistry.Remove"/>: what in Nino's caches belongs to an assembly.</summary>
    internal static class NinoUnload
    {
        /// <summary>
        /// Whether <paramref name="type"/> is declared in <paramref name="assembly"/> or is built from a type that is
        /// (an array, pointer or generic argument of it, at any depth).
        /// </summary>
        public static bool Mentions(Type type, Assembly assembly)
        {
            if (type == null)
                return false;
            if (type.Assembly == assembly)
                return true;
            if (type.HasElementType)
                return Mentions(type.GetElementType(), assembly);
            if (type.IsGenericType && !type.IsGenericTypeDefinition)
            {
                foreach (var argument in type.GetGenericArguments())
                {
                    if (Mentions(argument, assembly))
                        return true;
                }
            }

            return false;
        }

        /// <summary>Removes the entries whose key (or, with <paramref name="value"/>, whose value) mentions the assembly.</summary>
        public static void RemoveFrom<TValue>(ConcurrentDictionary<Type, TValue> cache, Assembly assembly,
            Func<TValue, Type> value = null)
        {
            foreach (var pair in cache)
            {
                if (Mentions(pair.Key, assembly) || (value != null && Mentions(value(pair.Value), assembly)))
                    cache.TryRemove(pair.Key, out _);
            }
        }
    }
}
