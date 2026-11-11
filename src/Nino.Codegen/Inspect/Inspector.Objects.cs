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
        /// <summary>A value of a <c>[NinoType]</c> declared as <paramref name="declared"/> (null: <c>object</c>), §7.1.</summary>
        private void Object(Body? declared, InspectNode node)
        {
            if (declared is { Type.Header: HeaderKind.None })
            {
                ReadBody(declared, node);
                return;
            }

            if (declared is { Type.Header: HeaderKind.Presence })
            {
                if (declared.Type.TrackReferences ? Reference(node, declared.Clr, true) : Presence(node, true))
                {
                    if (declared.Type.TrackReferences)
                        Track(declared, node);
                    ReadBody(declared, node);
                }

                return;
            }

            // Behind a type id: of a tracked declared type, after the reference header (§7.9).
            bool trackedSlot = declared?.Type.TrackReferences == true;
            if (trackedSlot && !Reference(node, declared!.Clr, true))
                return;
            int at = _r.Pos;
            int id = _r.I32();
            var idNode = node.Add("type id", at);
            idNode.Length = 4;
            idNode.Set(TypeNames.Hex(id), id.ToString(CultureInfo.InvariantCulture));
            if (id == 0)
            {
                if (trackedSlot)
                    throw WireReader.Fail(at, "a null type id after a new object's reference header");
                idNode.Note = "null";
                node.SetNull();
                return;
            }

            var runtime = Resolve(id, declared, at);
            idNode.Note = runtime.Name;
            if (node.Type != runtime.Name)
                node.AddNote(runtime.Name);
            if (!trackedSlot && runtime.Type.TrackReferences && !Reference(node, runtime.Clr, false))
                return;
            if (trackedSlot || runtime.Type.TrackReferences)
                Track(runtime, node);
            ReadBody(runtime, node);
        }

        /// <summary>The type a type id names, which must be one a value of <paramref name="declared"/> can be (§9.6).</summary>
        private Body Resolve(int id, Body? declared, int at)
        {
            _byId ??= TypeIds();
            if (_byId.TryGetValue(id, out var types))
            {
                foreach (var type in types)
                    if (declared == null || CodegenModel.AssignableTo(type.Clr, declared.Clr))
                        return type;
                throw WireReader.Fail(at, $"type id {TypeNames.Hex(id)} is {string.Join(" or ", types.Select(t => t.Name))}, " +
                                          $"not a {declared!.Name}");
            }

            throw WireReader.Fail(at, $"unknown type id {TypeNames.Hex(id)}" +
                                      (declared == null ? "" : $" for a {declared.Name}"));
        }

        /// <summary>
        /// The types a type id can name: every concrete <c>[NinoType]</c> with a layout (of the assembly and those it
        /// references), and the closed generic types the assembly names or its polymorphic slots need.
        /// </summary>
        private Dictionary<int, List<Body>> TypeIds()
        {
            var ids = new Dictionary<int, List<Body>>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            void Add(int id, Body body)
            {
                if (!seen.Add(body.Name))
                    return;
                if (!ids.TryGetValue(id, out var list))
                    ids[id] = list = new List<Body>();
                list.Add(body);
            }

            foreach (var type in _model.Types.Where(t => t.TypeId != 0 && t.IsConcrete && !t.IsGenericDefinition))
                Add(type.TypeId, new Body(type, null, type.Clr));
            foreach (var closed in _model.ClosedTypes)
                Add(closed.TypeId, new Body(closed.Definition,
                    new Env(closed.Value.Arguments.Select(a => new Arg(a, null)).ToList()), closed.Clr));
            return ids;
        }

        /// <summary>
        /// A reference header (§7.9): true for a new object, whose body follows; false for null or a back reference,
        /// which must name an object of <paramref name="type"/>. After a type id (<paramref name="nullable"/> false), null
        /// is invalid: the type id says null.
        /// </summary>
        private bool Reference(InspectNode node, Type type, bool nullable)
        {
            int at = _r.Pos;
            int header = _r.I32();
            var child = node.Add("reference", at);
            child.Length = 4;
            child.Set(header.ToString(CultureInfo.InvariantCulture), header.ToString(CultureInfo.InvariantCulture));
            if (header < -1 || header > _objects.Count || header == 0 && !nullable)
                throw WireReader.Fail(at, $"invalid object reference {header}" +
                                          (header > 0 ? $": {_objects.Count} object(s) read so far" : ""));
            if (header == -1)
                return true;
            if (header == 0)
            {
                child.Note = "null";
                node.SetNull();
                return false;
            }

            var target = _objects[header - 1];
            if (!CodegenModel.AssignableTo(target, type))
                throw WireReader.Fail(at, $"object reference {header} is a {TypeNames.Runtime(target)}, not a " +
                                          TypeNames.Runtime(type));
            child.Note = $"back reference to object #{header}";
            node.Set($"-> #{header}", $"{{\"$ref\":{header}}}");
            return false;
        }

        /// <summary>Numbers a new tracked object (§7.9).</summary>
        private void Track(Body body, InspectNode node)
        {
            _objects.Add(body.Clr);
            node.AddNote($"object #{_objects.Count}");
            node.Children.Last(c => c.Name == "reference").Note = $"new object #{_objects.Count}";
        }

        /// <summary>The body of a <c>[NinoType]</c>, deferred or counted as §7.10 says.</summary>
        private void ReadBody(Body body, InspectNode node)
        {
            var type = body.Type;
            if (type.Raw)
            {
                Value(type.RawValue!, body.Env, node);
                return;
            }

            if (type.IsClassBody)
            {
                bool deferrable = type.Deferrable ?? type.IsConcrete;
                if (deferrable && _depth >= InlineDepth)
                {
                    (_regions == 0 ? _top : _region).Add(new Deferred(body, node, _r.Pos));
                    node.AddNote("body deferred");
                    return;
                }

                if (!deferrable && _depth + _structs >= MaxDepth)
                    throw TooDeep(_r.Pos);
                int depth = _depth;
                _depth = depth + 1;
                Members(body, node);
                _depth = depth;
                if (depth == 0)
                    ReadTop();
                return;
            }

            bool counted = Counted(type);
            if (counted && ++_structs + _depth > MaxDepth)
                throw TooDeep(_r.Pos);
            Members(body, node);
            if (counted)
                _structs--;
        }

        private bool Counted(TypeModel type)
        {
            if (!_counted.TryGetValue(type, out bool counted))
                _counted[type] = counted = type.IsGenericDefinition || _model.HoldsItself(type);
            return counted;
        }

        /// <summary>A body's members in its format (§7.4 to §7.7).</summary>
        private void Members(Body body, InspectNode node)
        {
            var type = body.Type;
            if (type.Format != BodyFormat.Positional)
                node.AddNote(type.Format == BodyFormat.Keyed ? "keyed" : "version tolerant");
            if (type.Elements != null && type.Format != BodyFormat.Keyed)
                CollectionElements(body, node);
            switch (type.Format)
            {
                case BodyFormat.Positional:
                    foreach (var member in type.Members)
                        Member(member, body.Env, node, -1);
                    break;
                case BodyFormat.VersionTolerant:
                    Region(node, end =>
                    {
                        for (int i = 0; i < type.Members.Count; i++)
                        {
                            // Members past the region's end are missing: data of an older version.
                            if (_r.Pos == end)
                            {
                                node.Add(type.Members[i].Name, _r.Pos).Note = "missing (older data): default value";
                                continue;
                            }

                            Member(type.Members[i], body.Env, node, i == type.Members.Count - 1 ? end : -1);
                        }

                        if (_r.Pos < end)
                        {
                            var skipped = node.Add("(unknown members)", _r.Pos);
                            skipped.Length = end - _r.Pos;
                            skipped.Note = $"{end - _r.Pos} byte(s) of a newer version skipped";
                        }
                    });
                    break;
                default:
                    Region(node, end =>
                    {
                        if (type.Elements != null)
                            CollectionElements(body, node);
                        var keys = new HashSet<int>();
                        while (_r.Pos < end)
                            Entry(body, node, keys);
                        foreach (var member in type.Members.Where(m => !m.AllKeys.Any(keys.Contains)))
                            node.Add(member.Name, _r.Pos).Note = $"no entry (key {TypeNames.Hex(member.Key)}): default value";
                    });
                    break;
            }
        }

        private void CollectionElements(Body body, InspectNode node)
        {
            var elements = body.Type.Elements!;
            var child = node.Add("[elements]", _r.Pos);
            child.Encoding = elements.Encoding;
            try
            {
                if (elements.Kind == ValueKind.Map)
                    Map(elements, body.Env, child, true);
                else if (elements.Kind == ValueKind.Sequence)
                    Sequence(elements, body.Env, child, true);
                else
                    Value(elements, body.Env, child);
            }
            finally
            {
                child.Length = _r.Pos - child.Offset;
            }
        }

        private void Member(MemberModel member, Env? env, InspectNode node, int opaqueEnd)
        {
            var child = node.Add(member.Name, _r.Pos);
            child.Type = member.Value.IsOpen && env != null
                ? $"{member.TypeName} ({TypeNames.Runtime(ClrOf(member.Value, env))})"
                : member.TypeName;
            child.Encoding = member.Value.Encoding;
            _opaqueEnd = opaqueEnd;
            Value(member.Value, env, child);
        }

        /// <summary>An entry of a keyed body (§7.6): its key, its length, then the member's value filling it exactly.</summary>
        private void Entry(Body body, InspectNode node, HashSet<int> keys)
        {
            int at = _r.Pos;
            int key = _r.I32();
            int outer = _r.End;
            int end = BeginRegion(at + 4);
            _r.End = end;
            var member = body.Type.Members.FirstOrDefault(m => m.AllKeys.Contains(key));
            InspectNode child;
            if (member == null)
            {
                child = node.Add("(unknown key)", at);
                child.Note = $"key {TypeNames.Hex(key)}: {end - _r.Pos} byte(s) skipped";
                _r.Pos = end;
            }
            else
            {
                child = node.Add(member.Name, at);
                child.Type = member.Value.IsOpen && body.Env != null
                    ? $"{member.TypeName} ({TypeNames.Runtime(ClrOf(member.Value, body.Env))})"
                    : member.TypeName;
                child.Encoding = member.Value.Encoding;
                child.Note = $"key {TypeNames.Hex(key)}" + (key == member.Key ? "" : " (a former name)");
                _opaqueEnd = end;
                Value(member.Value, body.Env, child);
                if (_r.Pos != end)
                    throw Fail($"bytes left over at the end of keyed member {member.Name}: {end - _r.Pos}");
            }

            child.Length = end - at;
            if (!keys.Add(key))
                child.AddNote("key repeated: .NET keeps the last");
            _r.End = outer;
            EndRegion(end, node);
        }
    }
}
