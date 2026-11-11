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
        /// <summary>A version-tolerant or keyed body: its length, then its members inside it (§7.5, §7.6, §7.10.3).</summary>
        private void Region(InspectNode node, Action<int> members)
        {
            int at = _r.Pos;
            int outer = _r.End;
            int end = BeginRegion(at);
            var length = node.Add("length", at);
            length.Length = 4;
            int raw = BinaryPrimitives.ReadInt32LittleEndian(_r.Data.Slice(at));
            length.Set(raw.ToString(CultureInfo.InvariantCulture), raw.ToString(CultureInfo.InvariantCulture));
            if (_sectionEnd != 0 && _regions == 1)
                length.Note = $"{_sectionEnd - at - 4} bytes: members {end - at - 4}, then a section of deferred bodies";
            _r.End = end;
            members(end);
            _r.End = outer;
            EndRegion(end, node);
        }

        /// <summary>Reads a region's length at <paramref name="at"/>: where its members end.</summary>
        private int BeginRegion(int at)
        {
            _r.Pos = at;
            int length = _r.I32();
            _regions++;
            if (length >= 0 && length <= _r.Remaining)
                return _r.Pos + length;
            int total = length & 0x7FFFFFFF, start = _r.Pos;
            if (length >= 0 || total < 4 || total > _r.Remaining)
                throw WireReader.Fail(at, $"invalid length {length}: " + (length < 0 && length != int.MinValue && total >= 4
                    ? $"a section of {total} bytes, {_r.Remaining} left"
                    : $"{_r.Remaining} byte(s) left"));
            if (_regions != 1)
                throw WireReader.Fail(at, "deferred bodies in a region inside another region");
            var data = _r.Data;
            int end = total - 4, members = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(start + end));
            if (members < 0 || members >= end)
                throw WireReader.Fail(start + end, $"invalid offset of deferred bodies {members}");
            int last = -1;
            for (int p = members; p != end;)
            {
                if (end - p < 8)
                    throw WireReader.Fail(start + p, "invalid section of deferred bodies");
                int key = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(start + p));
                int size = BinaryPrimitives.ReadInt32LittleEndian(data.Slice(start + p + 4));
                if (key <= last || key > p || size < 0 || size > end - p - 8)
                    throw WireReader.Fail(start + p, "invalid section of deferred bodies");
                last = key;
                p += 8 + size;
            }

            _sectionEnd = start + total;
            return start + members;
        }

        /// <summary>Closes a region; at the outermost one, moves past its section and reads the bodies deferred to it.</summary>
        private void EndRegion(int end, InspectNode node)
        {
            if (--_regions != 0)
            {
                _r.Pos = end;
                return;
            }

            int after = _sectionEnd;
            _sectionEnd = 0;
            _r.Pos = after == 0 ? end : after;
            InspectNode? section = null;
            if (after != 0)
            {
                section = node.Add("(deferred bodies)", end);
                section.Length = after - end;
                section.Note = "the section of the outermost region";
            }

            if (_regionHead < _region.Count)
                ReadSection(end, section);
        }

        /// <summary>The bodies deferred inside the outermost region, each from its section entry (§7.10.3).</summary>
        private void ReadSection(int regionEnd, InspectNode? section)
        {
            int after = _r.Pos, sectionEnd = after - 4;
            if (section == null || sectionEnd <= regionEnd)
                throw WireReader.Fail(regionEnd, "missing deferred bodies: the region has no section of them");
            int start = regionEnd - BinaryPrimitives.ReadInt32LittleEndian(_r.Data.Slice(sectionEnd));
            int outer = _r.End, depth = _depth, entryDepth = Math.Min(depth, InlineDepth - 1) + 1;
            int at = regionEnd;
            _regions = 1;
            while (_regionHead < _region.Count)
            {
                var item = _region[_regionHead++];
                while (true)
                {
                    if (sectionEnd - at < 8)
                        throw WireReader.Fail(at, $"missing the deferred body of {item.Node.Name}: no entry keyed " +
                                                  (item.Position - start));
                    int key = BinaryPrimitives.ReadInt32LittleEndian(_r.Data.Slice(at));
                    int length = BinaryPrimitives.ReadInt32LittleEndian(_r.Data.Slice(at + 4));
                    var entry = section.Add("entry", at);
                    entry.Length = 8 + length;
                    at += 8;
                    if (length < 0 || length > sectionEnd - at)
                        throw WireReader.Fail(at - 4, $"invalid length {length}");
                    int body = at;
                    at += length;
                    if (start + key != item.Position)
                    {
                        entry.Note = $"key {key}: a body nothing read refers to, skipped";
                        continue;
                    }

                    entry.Note = $"key {key}: the body of {item.Node.Name} at {item.Node.Offset}";
                    _r.Pos = body;
                    _r.End = body + length;
                    _depth = entryDepth;
                    DeferredBody(item);
                    if (_r.Pos != body + length)
                        throw Fail($"bytes left over at the end of a deferred body: {body + length - _r.Pos}");
                    break;
                }
            }

            _regions = 0;
            _r.End = outer;
            _depth = depth;
            _r.Pos = after;
            _region.Clear();
            _regionHead = 0;
        }

        /// <summary>Every class body is closed: the bodies deferred outside regions, in order, each from depth 1 (§7.10.2).</summary>
        private void ReadTop()
        {
            if (_topActive || _topHead == _top.Count)
                return;
            _topActive = true;
            while (_topHead < _top.Count)
            {
                _depth = 1;
                DeferredBody(_top[_topHead++]);
            }

            _depth = 0;
            _top.Clear();
            _topHead = 0;
            _topActive = false;
        }

        private void DeferredBody(Deferred item)
        {
            var body = item.Node.Add("(body)", _r.Pos);
            body.Note = $"deferred: would have begun at {item.Position}";
            try
            {
                Members(item.Body, body);
            }
            finally
            {
                body.Length = _r.Pos - body.Offset;
            }
        }
    }
}
