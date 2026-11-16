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
    /// <c>Version</c>: major (-1 = null), minor, build and revision as int32s; an undefined build or revision is -1.
    /// </summary>
    public struct VersionFormatter : INinoFormatter<Version>
    {
        public void Serialize(Version value, ref Writer writer)
        {
            if (writer.TryWriteNullLength(value))
                return;

            // The four parts in one reservation.
            ref byte parts = ref writer.Reserve(4 * sizeof(int));
            NinoUnsafe.Write(ref parts, value.Major);
            NinoUnsafe.Write(ref parts, sizeof(int), value.Minor);
            NinoUnsafe.Write(ref parts, 2 * sizeof(int), value.Build);
            NinoUnsafe.Write(ref parts, 3 * sizeof(int), value.Revision);
        }

        public void Deserialize(ref Reader reader, ref Version value)
        {
            int major = reader.UnsafeRead<int>();
            if (major == NinoWire.NullLength)
            {
                value = null;
                return;
            }

            ref byte parts = ref reader.ReadBlock(3 * sizeof(int));
            int minor = NinoUnsafe.Read<int>(ref parts, 0), build = NinoUnsafe.Read<int>(ref parts, sizeof(int)),
                revision = NinoUnsafe.Read<int>(ref parts, 2 * sizeof(int));
            // What the constructors throw ArgumentOutOfRangeException for (an undefined build or revision is -1).
            if ((major | minor) < 0)
                NinoThrow.InvalidData("invalid Version.");
            if (value is not null && value.Major == major && value.Minor == minor &&
                value.Build == build && value.Revision == revision)
                return;
            value = build < 0 ? new Version(major, minor)
                : revision < 0 ? new Version(major, minor, build)
                : new Version(major, minor, build, revision);
        }
    }
}
