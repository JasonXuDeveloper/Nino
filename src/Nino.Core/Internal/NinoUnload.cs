using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Threading;

namespace Nino.Core
{
    /// <summary>
    /// What <see cref="NinoRegistry.Remove"/> runs: each cache that can hold types of an assembly registers here, when it
    /// is created, what forgets them (<see cref="OnRemove"/>, or <see cref="Cache{TValue}"/> for a dictionary keyed by
    /// type), so the registry names none of them and a cache never used costs nothing.
    /// </summary>
    internal static class NinoUnload
    {
        private struct Hook
        {
            /// <summary>The type the hook belongs to (null: Nino.Core's own); dropped once its assembly is removed.</summary>
            public Type Owner;
            public Action<Assembly> Remove;
        }

        private static readonly object Lock = new object();
        private static Hook[] _hooks = Array.Empty<Hook>();

        /// <summary>
        /// Registers <paramref name="remove"/>, run for every assembly removed from now on. A hook of a generic cache
        /// closed over user types passes its type argument as <paramref name="owner"/>: the hook is dropped (and its
        /// type released) when that type's assembly is removed.
        /// </summary>
        public static void OnRemove(Action<Assembly> remove, Type owner = null)
        {
            lock (Lock)
            {
                var hooks = new Hook[_hooks.Length + 1];
                _hooks.CopyTo(hooks, 0);
                hooks[_hooks.Length] = new Hook { Owner = owner, Remove = remove };
                Volatile.Write(ref _hooks, hooks);
            }
        }

        /// <summary>Registers <paramref name="remove"/> (<see cref="OnRemove"/>) and returns <paramref name="cache"/>.</summary>
        public static T With<T>(T cache, Action<Assembly> remove)
        {
            OnRemove(remove);
            return cache;
        }

        /// <summary>
        /// A dictionary keyed by type whose entries that mention a removed assembly (in their key or, with
        /// <paramref name="value"/>, in the type of their value) are dropped.
        /// </summary>
        public static ConcurrentDictionary<Type, TValue> Cache<TValue>(Func<TValue, Type> value = null)
        {
            var cache = new ConcurrentDictionary<Type, TValue>();
            OnRemove(assembly => RemoveFrom(cache, assembly, value));
            return cache;
        }

        /// <summary>Runs every hook for <paramref name="assembly"/>, then drops the hooks of its own types.</summary>
        public static void Remove(Assembly assembly)
        {
            foreach (var hook in Volatile.Read(ref _hooks))
                hook.Remove(assembly);
            lock (Lock)
            {
                var kept = Array.FindAll(_hooks, hook => !Mentions(hook.Owner, assembly));
                if (kept.Length != _hooks.Length)
                    Volatile.Write(ref _hooks, kept);
            }
        }

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
        private static void RemoveFrom<TValue>(ConcurrentDictionary<Type, TValue> cache, Assembly assembly,
            Func<TValue, Type> value)
        {
            foreach (var pair in cache)
            {
                if (Mentions(pair.Key, assembly) || (value != null && Mentions(value(pair.Value), assembly)))
                    cache.TryRemove(pair.Key, out _);
            }
        }
    }
}
