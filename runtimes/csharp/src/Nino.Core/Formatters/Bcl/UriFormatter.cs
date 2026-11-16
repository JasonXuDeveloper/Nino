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
    /// <summary><c>Uri</c>: its original string (null for null), read back relative or absolute.</summary>
    public struct UriFormatter : INinoFormatter<Uri>
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Serialize(Uri value, ref Writer writer) => writer.Write(value?.OriginalString);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Deserialize(ref Reader reader, ref Uri value)
        {
            string text = reader.ReadString();
            if (text == null)
                value = null;
            else
                value = Parse(text);
        }

        // Out of line, as the constructor it replaces was: what it rejects is invalid data.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Uri Parse(string text)
        {
            if (!Uri.TryCreate(text, UriKind.RelativeOrAbsolute, out var uri))
                NinoThrow.InvalidData("invalid Uri.");
            return uri;
        }
    }
}
