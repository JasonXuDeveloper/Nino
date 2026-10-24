using System;
using System.Collections.Generic;
using System.Threading;
using Nino.Core.CompilerServices;

namespace Nino.Core
{
    /// <summary>
    /// The type ids a polymorphic read looked for and did not find (<see cref="NinoPolymorphic{TBase}"/>), so that
    /// reading the same unknown id again (untrusted or stale data) does not redo the search: every generated registry
    /// asked twice, every loaded assembly scanned and every resolved formatter type checked. A miss holds while
    /// nothing that search depends on has changed: no registry added or removed, no formatter published
    /// (<see cref="NinoRegistry.Changed"/>) and no assembly loaded or unloaded.
    /// </summary>
    internal static class NinoUnknownIds
    {
        /// <summary>At most this many ids are remembered; past it the set starts over.</summary>
        private const int Capacity = 1024;

        /// <summary>What a search depended on, read before it.</summary>
        internal readonly struct Stamp
        {
            public readonly int Epoch, Loads, Assemblies;

            public Stamp(int epoch, int loads, int assemblies)
            {
                Epoch = epoch;
                Loads = loads;
                Assemblies = assemblies;
            }

            public bool Equals(Stamp other) =>
                Epoch == other.Epoch && Loads == other.Loads && Assemblies == other.Assemblies;
        }

        private sealed class Misses
        {
            public readonly Stamp Stamp;
            public readonly HashSet<int> Ids = new HashSet<int>();

            public Misses(Stamp stamp) => Stamp = stamp;
        }

        private static Misses _misses;

        /// <summary>
        /// The current stamp. Where <c>AssemblyLoad</c> is counted (<see cref="NinoRegistry.LoadsCounted"/>), the load
        /// count tells every load, an unload followed by a load included, without listing the assemblies; elsewhere
        /// the assembly count does what it can.
        /// </summary>
        public static Stamp Now() => new Stamp(NinoRegistry.Epoch, NinoRegistry.Loads,
            NinoRegistry.LoadsCounted ? 0 : AppDomain.CurrentDomain.GetAssemblies().Length);

        /// <summary>Whether a search for <paramref name="id"/> found nothing, with nothing changed since.</summary>
        public static bool Contains(int id, Stamp now)
        {
            var misses = Volatile.Read(ref _misses);
            if (misses == null || !misses.Stamp.Equals(now))
                return false;
            lock (misses)
                return misses.Ids.Contains(id);
        }

        /// <summary>Records that a search for <paramref name="id"/>, started at <paramref name="before"/>, found nothing.</summary>
        public static void Add(int id, Stamp before)
        {
            var misses = Volatile.Read(ref _misses);
            if (misses == null || !misses.Stamp.Equals(before))
            {
                // Something changed during the search: what it found may be stale, so it is not recorded.
                if (!before.Equals(Now()))
                    return;
                var fresh = new Misses(before);
                misses = Interlocked.CompareExchange(ref _misses, fresh, misses) == misses ? fresh : Volatile.Read(ref _misses);
                if (misses == null || !misses.Stamp.Equals(before))
                    return;
            }

            lock (misses)
            {
                if (misses.Ids.Count >= Capacity)
                    misses.Ids.Clear();
                misses.Ids.Add(id);
            }
        }
    }
}
