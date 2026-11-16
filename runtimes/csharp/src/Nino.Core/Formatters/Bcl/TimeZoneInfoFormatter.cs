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
    /// <c>TimeZoneInfo</c>: its id (null for null), then, for a zone that is not one of this machine's system zones
    /// (a custom zone), its serialized form (<c>ToSerializedString</c>), else null. A system zone reads back as the
    /// reader's system zone of that id (Windows and IANA ids are converted where the runtime can); a custom one from
    /// its serialized form. System zones are not read from their serialized form: the runtime cannot read back that
    /// of many IANA zones, whose adjustment rules overlap.
    /// </summary>
    public struct TimeZoneInfoFormatter : INinoFormatter<TimeZoneInfo>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(TimeZoneInfo value, ref Writer writer)
        {
            if (value == null)
            {
                writer.Write((string)null);
                return;
            }

            writer.Write(value.Id);
            writer.Write(IsSystemZone(value) ? null : value.ToSerializedString());
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Deserialize(ref Reader reader, ref TimeZoneInfo value)
        {
            // An id is never one char: read without the shared one-char strings' branch: with it, TimeZoneInfo reads were
            // 2.4% slower on Neoverse-N2, and 7.5% (13% on x64) with both strings read in one call out of line.
            string id = reader.ReadStringUnshared();
            if (id == null)
            {
                value = null;
                return;
            }

            // Null for a system zone, the usual case, and long otherwise: decoded out of line, so that the two strings
            // inline do not spend the caller's inlining budget (which left Reader.Skip as a call and the reader in
            // memory).
            string rules = reader.ReadStringOutOfLine();
            value = Find(id, rules);
        }

        // A zone this machine does not have, or rules the runtime cannot parse, is invalid data.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static TimeZoneInfo Find(string id, string rules)
        {
            try
            {
                return rules == null ? TimeZoneInfo.FindSystemTimeZoneById(id) : TimeZoneInfo.FromSerializedString(rules);
            }
            catch (Exception e) when (e is TimeZoneNotFoundException || e is InvalidTimeZoneException ||
                                      e is System.Runtime.Serialization.SerializationException ||
                                      e is ArgumentException || e is System.IO.IOException ||
                                      e is UnauthorizedAccessException || e is System.Security.SecurityException)
            {
                NinoThrow.InvalidValue(typeof(TimeZoneInfo), e);
                return null;
            }
        }

        private static bool IsSystemZone(TimeZoneInfo zone)
        {
            if (ReferenceEquals(zone, TimeZoneInfo.Utc))
                return true;
            try
            {
                // Cached by the runtime: the same instance for a system zone, else one with the same rules.
                var system = TimeZoneInfo.FindSystemTimeZoneById(zone.Id);
                return ReferenceEquals(system, zone) ||
                       (system.BaseUtcOffset == zone.BaseUtcOffset && system.HasSameRules(zone));
            }
            catch (TimeZoneNotFoundException)
            {
                return false;
            }
            catch (InvalidTimeZoneException)
            {
                return false;
            }
        }
    }
}
