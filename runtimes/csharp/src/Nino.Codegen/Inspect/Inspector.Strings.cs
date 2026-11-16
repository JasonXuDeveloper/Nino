using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Nino.Codegen.Model;

namespace Nino.Codegen.Inspect
{
    internal sealed partial class Inspector
    {
        private void String(InspectNode node)
        {
            int at = _r.Pos;
            uint header = _r.Header();
            if (header == 0)
            {
                node.SetNull();
                return;
            }

            long chars = header >> 1;
            bool latin1 = (header & 1) == 1;
            long bytes = latin1 ? chars : chars * 2;
            if (bytes > _r.Remaining)
                throw WireReader.Fail(at, $"string of {chars} char(s) ({bytes} bytes) runs past the end: " +
                                          $"{_r.Remaining} byte(s) left");
            var span = _r.Take((int)bytes);
            // UTF-16 as it is: a lone surrogate is kept, not replaced (§4.1).
            string text = latin1
                ? Encoding.Latin1.GetString(span)
                : new string(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, char>(span));
            SetString(node, text, latin1 ? "Latin-1" : "UTF-16");
        }

        private void Utf8(InspectNode node)
        {
            int at = _r.Pos;
            uint header = _r.Header();
            if (header <= 1)
            {
                if (header == 0)
                    node.SetNull();
                else
                    SetString(node, "", "UTF-8");
                return;
            }

            long chars = header - 1L;
            uint count = _r.Header();
            if (chars > int.MaxValue || count > int.MaxValue || count < chars || count > 3 * chars)
                throw WireReader.Fail(at, $"invalid UTF-8 string: {chars} char(s) in {count} byte(s)");
            if (count > _r.Remaining)
                throw WireReader.Fail(at, $"UTF-8 string of {count} bytes runs past the end: {_r.Remaining} left");
            string text;
            try
            {
                text = Strict.GetString(_r.Take((int)count));
            }
            catch (DecoderFallbackException)
            {
                throw WireReader.Fail(at, "invalid UTF-8 bytes");
            }

            if (text.Length != chars)
                throw WireReader.Fail(at, $"UTF-8 string of {text.Length} char(s), not the {chars} its header says");
            SetString(node, text, "UTF-8");
        }

        private static readonly UTF8Encoding Strict = new(false, true);

        private static void SetString(InspectNode node, string text, string form)
        {
            string json = InspectNode.JsonString(text);
            node.Set(text.Length <= ShownChars
                ? json
                : InspectNode.JsonString(text.Substring(0, ShownChars)) + $" ... ({text.Length} chars)", json, json);
            node.AddNote($"{form}, {text.Length} char(s)");
        }
    }
}
