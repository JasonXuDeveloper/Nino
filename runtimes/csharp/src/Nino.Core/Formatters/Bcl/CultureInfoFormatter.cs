using System;
using System.Buffers;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Nino.Core.CompilerServices;
using Nino.Core.Internal;

namespace Nino.Core
{
    /// <summary>
    /// <c>CultureInfo</c>: its name (null for null; "" is the invariant culture), read back read-only. On .NET 8 and
    /// later only the cultures the runtime ships read back (see <see cref="GetCulture"/>).
    /// </summary>
    public struct CultureInfoFormatter : INinoFormatter<CultureInfo>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(CultureInfo value, ref Writer writer) => writer.Write(value?.Name);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Deserialize(ref Reader reader, ref CultureInfo value)
        {
            string name = reader.ReadString();
            value = name == null ? null : GetCulture(name);
        }

        /// <summary>
        /// The culture of <paramref name="name"/>. With ICU (.NET 5 and later, the net8.0 and net10.0 builds), the
        /// runtime makes a culture of any well-formed name and keeps every one it makes for the life of the process
        /// (CultureData's cache and GetCultureInfo's, neither ever trimmed): names from the data could grow the heap
        /// without bound (200,000 names such as "en-x0001", 160 MB). So only the cultures the runtime ships read back
        /// there (GetCultureInfo's predefinedOnly, a bounded set), and only by names without a singleton subtag: with
        /// one ("en-US-u-ca-buddhist", "en-US-x-anything"), even predefinedOnly accepts and keeps every distinct name.
        /// Custom cultures and names with such extensions are invalid data. The netstandard2.1 build (Mono,
        /// Unity) know only the cultures of their own tables, and keep their own checks.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static CultureInfo GetCulture(string name)
        {
#if NET8_0_OR_GREATER
            // A name read before: the check below asks ICU on every call (544 ns a read against 164 without it).
            if (Accepted.TryGetValue(name, out CultureInfo known))
                return known;
#endif
            try
            {
#if NET8_0_OR_GREATER
                if (name.Length == 0)
                    return CultureInfo.InvariantCulture;
                if (InvariantGlobalization || HasSingleton(name))
                    throw new CultureNotFoundException(nameof(name), name, "not a culture this runtime ships.");
                CultureInfo culture = CultureInfo.GetCultureInfo(name, predefinedOnly: true);
                // A counter, not Accepted.Count (which takes every lock of the dictionary).
                if (Volatile.Read(ref _acceptedCount) < MaxAccepted && Accepted.TryAdd(name, culture))
                    Interlocked.Increment(ref _acceptedCount);
                return culture;
#else
                return CultureInfo.GetCultureInfo(name);
#endif
            }
            catch (CultureNotFoundException e)
            {
                NinoThrow.InvalidValue(typeof(CultureInfo), e);
                return null;
            }
        }

#if NET8_0_OR_GREATER
        /// <summary>
        /// The names <see cref="GetCulture"/> accepted, case ignored as the runtime ignores it: only shipped cultures,
        /// which the runtime keeps anyway, so the data cannot grow it beyond a bounded set, and
        /// <see cref="MaxAccepted"/> bounds it whatever the runtime accepts. Rejected names are never kept.
        /// </summary>
        private static readonly ConcurrentDictionary<string, CultureInfo> Accepted =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// The most names <see cref="Accepted"/> keeps: above the cultures ICU ships (about 900), so that every
        /// name an application writes stays a lookup; past it a name is checked on every read, as without the cache.
        /// </summary>
        internal const int MaxAccepted = 4096;

        private static int _acceptedCount;

        /// <summary>The names kept, for tests.</summary>
        internal static int AcceptedCount => Accepted.Count;

        /// <summary>
        /// Whether globalization runs in invariant mode, where predefinedOnly is not checked and GetCultureInfo makes
        /// (and keeps) a culture of any name when PredefinedCulturesOnly is turned off: only the invariant culture
        /// reads back there.
        /// </summary>
        private static readonly bool InvariantGlobalization =
            AppContext.TryGetSwitch("System.Globalization.Invariant", out bool invariant)
                ? invariant
                : Environment.GetEnvironmentVariable("DOTNET_SYSTEM_GLOBALIZATION_INVARIANT") is { } value &&
                  (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase));

        /// <summary>Whether a subtag of <paramref name="name"/> is one character: an extension or private use.</summary>
        private static bool HasSingleton(string name)
        {
            int start = 0;
            for (int i = 0; i <= name.Length; i++)
            {
                if (i < name.Length && name[i] != '-' && name[i] != '_')
                    continue;
                if (i - start == 1)
                    return true;
                start = i + 1;
            }

            return false;
        }
#endif
    }
}
