using System.Collections.Generic;
using System.Linq;
using Nino.Generator.Common;
using MemberRange = Nino.Generator.Generation.Emit.RawBlock.MemberRange;
using static Nino.Generator.Generation.Emit.MemberCode;
using static Nino.Generator.Generation.TypeNames;

namespace Nino.Generator.Generation.Emit;

internal sealed partial class BodyEmitter
{
    private bool LocalReader => _m.LocalReader;

    /// <summary>
    /// Whether the read body counts its nesting with the reader's <c>EnterStruct</c>: a struct whose nesting no bound
    /// covers (<see cref="DepthModel.Bound"/>, a struct that can reach itself or a type that is not known here),
    /// which opens no class body of its own to count it.
    /// </summary>
    private bool CountsStructDepth =>
        _m.Kind == FormatterKind.Struct && !_mode.Shallow && _m.Depth.Nests && _m.Depth.Bound == TypeFacts.Unbounded;

    private bool LocalWriter => _m.LocalWriter;

    /// <summary><see cref="TypeModel.ReadTop"/>, for the body of the type's own formatter.</summary>
    private bool ReadTop => !_mode.Shallow && _m.ReadTop;

    private bool SplitFresh => _m.SplitFresh;

    public void EmitReadBody()
    {
        if (SplitFresh)
        {
            EmitSplitReadBody();
            return;
        }

        _w.Open(EmitForwardersReturningSignature("ReadBody", Signature.ReadParameters(T)));
        if (CountsStructDepth)
        {
            // A struct that can hold itself (through a collection, a nullable, a Lazy or an interface) opens no class
            // body, so no depth counts its nesting: the reader counts it here, and throws before the stack runs out.
            _w.Line($"reader.EnterStruct(typeof({T}));");
            _w.Open("try");
            EmitReadBodyContents(fresh: false, local: false);
            _w.Close();
            _w.Open("finally");
            _w.Line("reader.ExitStruct();");
            _w.Close();
            _w.Close();
            return;
        }

        // A __Top twin's inlinable body is read through the local of the out-of-line wrapper (EmitForwardersReturningSignature).
        EmitReadBodyContents(fresh: false, local: LocalReader && !_m.InlineBody && !_mode.Shallow && !ReadTop);
        _w.Close();
    }

    /// <summary>
    /// The reads of a body, on a local copy of the reader when <paramref name="local"/>. <paramref name="present"/>:
    /// <c>value</c> is never null here (the reuse body of a <see cref="TypeModel.SplitFresh"/> type, which only its
    /// <c>ReadBody</c> calls, after sending a null slot to the new-instance body), so no instance is created.
    /// </summary>
    private void EmitReadBodyContents(bool fresh, bool local, bool present = false)
    {
        if (local)
        {
            BeginLocalCopy(_w, reader: true);
            EmitReads(fresh, "__r", present);
            EndLocalCopy(_w, reader: true);
        }
        else
            EmitReads(fresh, "reader", present);
    }

    private void EmitReads(bool fresh, string r, bool present)
    {
        // The static [NinoOnDeserializing]s, before the instance is taken or created; the rest go with the instance.
        EmitCallbacks(CallbackKind.Deserializing, null);
        if (fresh)
        {
            // The slot held null and the pool, if any, had nothing: a new instance, whose members are null.
            // The test always passes (the forwarder calls this for a null slot only), but it is kept: a method
            // without a branch gets no profile from tiered PGO, and without one the JIT stops inlining the
            // collection constructors of the member reads. An instance passed in anyway is still read correctly
            // (its members are replaced, not reused).
            ReadIntoLocal(_m.Need(_m.Construction.Creator, nameof(ConstructionModel.Creator)) + "()");
            using (InMode(_mode with { Fresh = true, Target = "__v" }))
                EmitInPlaceMembers(r);
        }
        else if (_m.Construction.Recreate)
            EmitRecreatingRead(r);
        else
            EmitInPlaceRead(r, present);
    }

    /// <summary>Copies the reader or writer into a local before emitting its body.</summary>
    internal static void BeginLocalCopy(CodeWriter w, bool reader)
    {
        w.Line(reader ? Core + "Reader __r = default;" : Core + "Writer __w = default;");
        w.Line(reader ? "reader.CopyTo(ref __r);" : "writer.CopyTo(ref __w);");
    }

    /// <summary>Copies the local reader or writer back to the parameter after its body.</summary>
    internal static void EndLocalCopy(CodeWriter w, bool reader) =>
        w.Line(reader ? "__r.CopyTo(ref reader);" : "__w.CopyTo(ref writer);");

    /// <summary>
    /// The local <c>__v</c>, which the reads that follow go to (<see cref="BodyMode.Target"/>): the instance in
    /// <c>value</c>, or when null a new one (<paramref name="create"/>) stored in both.
    /// </summary>
    private void ReadIntoLocal(string create)
    {
        _w.Line("var __v = value;");
        _w.Line("if (__v == null)");
        _w.IndentedLine($"value = __v = {create};");
    }

    /// <summary>
    /// The statements of the inlined <c>ReadBody</c> of a <see cref="TypeModel.SplitFresh"/> body (named
    /// <paramref name="body"/>): a null slot is read as a new instance (a pooled type takes one from its pool
    /// first, read as an existing one).
    /// </summary>
    public void EmitFreshForwarder(string body)
    {
        _w.Open("if (value == null)");
        if (_m.Pooled)
        {
            _w.Line($"var __p = {T}.RentFromNinoPool();");
            _w.Open("if (__p == null)");
            _w.Line($"{body}Fresh(ref reader, ref value);");
            _w.Line("return;");
            _w.Close();
            _w.Line("value = __p;");
        }
        else
        {
            _w.Line($"{body}Fresh(ref reader, ref value);");
            _w.Line("return;");
        }

        _w.Close();
        _w.Line($"{body}(ref reader, ref value);");
    }

    private void EmitSplitReadBody()
    {
        string parameters = Signature.ReadParameters(T);
        string body = _mode.Shallow ? "__ReadBodyShallow" : "__ReadBody";
        if (!_mode.Shallow)
        {
            // The shallow twin's forwarder is its struct's (FormatterEmitter).
            _w.Line(_names.Inline);
            _w.Open($"public void ReadBody({parameters})");
            EmitFreshForwarder(body);
            _w.Close();
            _w.Line();
        }

        // Private: the forwarder above (or the __Shallow twin's, nested in this struct) is the only caller, and
        // sends a null slot to {body}Fresh, so value is never null here.
        _w.Line(_names.NoInline);
        _w.Open($"private static void {body}({parameters})");
        EmitReadBodyContents(fresh: false, local: LocalReader && !_mode.Shallow, present: true);
        _w.Close();
        _w.Line();
        _w.Line(_names.NoInline);
        if (ReadTop)
        {
            // The top twin's new-instance body is inlinable and called here on a local copy of the reader.
            _w.Open($"private static void {body}Fresh({parameters})");
            BeginLocalCopy(_w, reader: true);
            _w.Line($"{body}FreshInline(ref __r, ref value);");
            EndLocalCopy(_w, reader: true);
            _w.Close();
            _w.Line();
            _w.Line(_names.Inline);
            _w.Open($"private static void {body}FreshInline({parameters})");
            EmitReadBodyContents(fresh: true, local: false);
            _w.Close();
            return;
        }

        _w.Open($"private static void {body}Fresh({parameters})");
        EmitReadBodyContents(fresh: true, local: LocalReader && !_mode.Shallow);
        _w.Close();
    }

    private void EmitInPlaceRead(string r, bool present)
    {
        if (_m.Kind != FormatterKind.Struct)
        {
            string create = Create(_m, _names.CompactSyntax);
            // The instance in a local (a collection formatter may replace value, so not for collections).
            if (!_m.IsCollection)
            {
                if (present)
                    _w.Line("var __v = value;");
                else
                    ReadIntoLocal(create);
                using (InMode(_mode with { Target = "__v" }))
                    EmitInPlaceMembers(r);
                return;
            }

            _w.Line("if (value == null)");
            _w.IndentedLine($"value = {create};");
        }

        EmitInPlaceMembers(r);
    }

    /// <summary>
    /// The reads of an instance in <see cref="BodyMode.Target"/> (or of a struct in place), in its regions, between
    /// the callbacks around them.
    /// </summary>
    private void EmitInPlaceMembers(string r)
    {
        EmitCallbacks(CallbackKind.Deserializing, _mode.Target);
        EmitInPlaceReads(r);
        EmitCallbacks(CallbackKind.Deserialized, _mode.Target);
    }

    private void EmitInPlaceReads(string r)
    {
        if (_m.TrackReferences)
            _w.Line($"reader.SetReference({_mode.Target});");

        // Keyed: the region holds everything; version tolerant: the members (the elements of a collection come first).
        string outer = r;
        if (_m.Keyed)
            r = BeginRegionRead(r);
        if (_m.IsCollection)
        {
            _w.Line($"default({CollectionFormatter}).Deserialize(ref {r}, ref value);");
            // The elements are the object: a count of -1 (null) would leave nothing to read the members into.
            _w.Line($"{CoreCompilerServices}NinoObject.EnsureElements(value);");
        }

        if (Tolerant)
            r = BeginRegionRead(r);

        if (_m.Inheritance.EmitMembers && !_mode.Shallow && !_mode.Fresh)
            _w.Line($"ReadMembers(ref {r}, {_mode.Target});");
        else
            EmitMemberReads(r, inPlace: true, eof: $"goto {EndLabel};");

        if (Region)
            EndRegionRead(outer);
    }

    /// <summary>The label a version-tolerant read jumps to at the end of its region (one per copy of the reads).</summary>
    private string EndLabel => _mode.Fresh ? "__endFresh" : "__end";

    /// <summary>Opens the region of the members (<c>__reader</c>, read from <paramref name="r"/>).</summary>
    private string BeginRegionRead(string r)
    {
        _w.Line($"var __reader = {r}.ReadRegion();");
        return "__reader";
    }

    /// <summary>
    /// Closes the region, the target of the version-tolerant reads' jump when it ends before the members do.
    /// </summary>
    private void EndRegionRead(string outer)
    {
        if (Tolerant)
        {
            // These members do not register references or queue deferred bodies.
            // ReadRegion still validates a section written by a newer version whose members we do not read.
            if (!_m.IsCollection && _m.Members.All(PreservesReadContext))
            {
                _w.Line(EndLabel + ":;");
                return;
            }

            _w.Line(EndLabel + ":");
        }

        _w.Line($"{outer}.EndRegion(in __reader);");
    }

    /// <summary>Reads every member into locals, then constructs the instance from them.</summary>
    private void EmitRecreatingRead(string r)
    {
        var c = _m.Construction;
        foreach (var member in _m.Members)
            _w.Line($"{member.TypeName} {Local(member)} = default;");
        string outer = r;
        if (Region)
            r = BeginRegionRead(r);
        EmitMemberReads(r, inPlace: false, eof: "goto __end;");
        if (Region)
            EndRegionRead(outer);

        if (c.Creator.Length == 0)
        {
            // The name as users write it: not global::-qualified, which the file's alias of the type would replace.
            string name = EscapeString(T.Replace("global::", ""));
            _w.Line($"throw new global::System.NotSupportedException(\"Nino: cannot construct {name}\");");
            return;
        }

        // A class is built in a local and then replaces the old instance (which is recycled); a struct is
        // assigned in place.
        bool isClass = _m.Kind != FormatterKind.Struct;
        string target = isClass ? "__new" : "value";
        string assign = isClass ? "var __new = " : "value = ";

        // The planner already matched each argument to its serialized member name.
        string call = assign + c.Creator + "(" + string.Join(", ", c.Arguments.Select(Local)) + ")";
        var args = c.Arguments.Length > MaxScannedArguments ? new HashSet<string>(c.Arguments) : null;
        bool initialized = false;
        foreach (var member in _m.Members)
        {
            if (member.Access != MemberAccess.Direct || member.Unassignable ||
                IsConstructorArgument(c.Arguments, args, member.Name))
                continue;
            if (!initialized)
            {
                _w.Line(call);
                _w.Open();
                initialized = true;
            }
            _w.Line($"{Identifier(member.SourceName)} = {Local(member)},");
        }
        if (initialized)
            _w.Close(";");
        else
            _w.Line(call + ";");

        foreach (var member in _m.Members)
            if (member.Access != MemberAccess.Direct && !member.Unassignable &&
                !IsConstructorArgument(c.Arguments, args, member.Name))
                _w.Line(Set(_m, member, target, Local(member)));

        if (isClass)
            _w.Line($"{Core}NinoRecycler.Replace(ref value, __new);");
        EmitCallbacks(CallbackKind.Deserialized, "value");
    }

    private static bool IsConstructorArgument(EquatableArray<string> arguments, HashSet<string>? lookup, string name)
    {
        if (lookup != null)
            return lookup.Contains(name);
        foreach (string argument in arguments)
            if (argument == name)
                return true;
        return false;
    }

    /// <param name="eof">
    /// Version tolerant: the statement that ends the reads when the region ends before a member (older data).
    /// </param>
    private void EmitMemberReads(string r, bool inPlace, string eof)
    {
        _blocks = 0;
        // Base members through the base formatter (in place only: recreated types read everything into locals).
        bool delegates = inPlace && Delegates;
        var members = inPlace ? OwnMembers : AllMembers;
        if (delegates)
            _w.Line($"{BaseFormatter}.ReadMembers(ref {r}, {_mode.Target});");

        if (_m.Keyed)
        {
            EmitKeyedReads(members, r, inPlace);
            return;
        }

        foreach (var group in RawBlock.Groups(members))
        {
            if (group.Count == 1)
            {
                if (Tolerant)
                    _w.Line($"if ({r}.Eof) {eof}");
                EmitMemberRead(group[0], r, inPlace);
                continue;
            }

            var block = new RawBlock(group);
            if (!Tolerant)
            {
                EmitRawBlockRead(group, block, r, inPlace);
                continue;
            }

            // Data written by an older version may end before any member (the rest keep their current or default
            // value); members appended by a newer one are skipped with the region. A run of raw members that is
            // all there (always, but for older data) is read as one block, under one check.
            _w.Open($"if ((uint){r}.Remaining >= {(block.IsConstant ? block.Total : $"(uint)({block.Total})")})");
            EmitRawBlockRead(group, block, r, inPlace);
            _w.Close();
            _w.Open("else");
            for (int i = 0; i < group.Count; i++)
            {
                _w.Line($"if ({r}.Eof) {eof}");
                EmitMemberRead(group[i], r, inPlace);
            }

            _w.Close();
        }
    }

    /// <summary>A read of the raw <paramref name="member"/> at <paramref name="offset"/> of block <paramref name="p"/>.</summary>
    private static string ReadRaw(MemberModel member, string p, string offset) => member.Raw!.Read switch
    {
        RawRead.Bool => $"{CoreCompilerServices}NinoUnsafe.ReadBool(ref {p}, {offset})",
        RawRead.Checked => $"{CoreCompilerServices}NinoUnsafe.ReadChecked<{member.TypeName}>(ref {p}, {offset})",
        _ => $"{CoreCompilerServices}NinoUnsafe.Read<{member.TypeName}>(ref {p}, {offset})",
    };

    /// <summary>The raw members of <paramref name="group"/>, read from one block of <paramref name="r"/>.</summary>
    private void EmitRawBlockRead(MemberRange group, RawBlock block, string r, bool inPlace)
    {
        string p = "__p" + _blocks++;
        _w.Line($"ref byte {p} = ref {r}.ReadBlock({block.Total});");
        for (int i = 0; i < group.Count; i++)
        {
            // A bool through its own read, which rejects a byte other than 0 or 1; types with values to check in
            // Untrusted mode through theirs.
            _w.Line(Store(_m, group[i], inPlace, ReadRaw(group[i], p, block.Offsets[i]), _mode.Target));
        }
    }

    /// <summary>Reads one member: raw members directly, others through their formatter.</summary>
    private void EmitMemberRead(MemberModel member, string r, bool inPlace)
    {
        if (member.Raw != null)
        {
            _w.Line(Store(_m, member, inPlace, member.Raw.Read switch
            {
                RawRead.Bool => $"{r}.ReadBool()",
                RawRead.Checked => $"{r}.ReadChecked<{member.TypeName}>()",
                _ => $"{r}.UnsafeRead<{member.TypeName}>()",
            }, _mode.Target));
            return;
        }

        // An existing instance's string is passed in, and kept when unchanged; a new instance's is null.
        bool existing = inPlace && !(_mode.Fresh && member.Fresh);
        if (existing && member.IsString && member.PlainStore)
        {
            // An unchanged string (kept by the read) is not stored again: a store into an existing object is a write
            // barrier call. Only where the store does nothing else (a field, an auto-property's setter).
            _w.Open();
            _w.Line($"var __c = {Get(_m, member, _mode.Target)};");
            _w.Line($"var __s = {DirectRead(member, r, "__c")};");
            _w.Line("if (!object.ReferenceEquals(__s, __c))");
            _w.Line("    " + Set(_m, member, _mode.Target, "__s"));
            _w.Close();
            return;
        }

        var direct = DirectRead(member, r, existing ? Get(_m, member, _mode.Target) : null);
        if (direct != null)
        {
            _w.Line(Store(_m, member, inPlace, direct, _mode.Target));
            return;
        }

        string formatter = Formatter(member);
        if (!inPlace)
        {
            _w.Line($"default({formatter}).Deserialize(ref {r}, ref {Local(member)});");
            return;
        }

        if (_mode.Fresh && member.Fresh)
        {
            // The member of a new instance is null: read from a null local, which the formatter (inlined) knows,
            // so its reuse path folds away; then stored.
            _w.Open();
            _w.Line($"{member.TypeName} __t = null;");
            _w.Line($"default({formatter}).Deserialize(ref {r}, ref __t);");
            _w.Line(Set(_m, member, _mode.Target, "__t"));
            _w.Close();
            return;
        }

        var reference = Ref(_m, member, _mode.Target);
        if (reference != null)
        {
            _w.Line($"default({formatter}).Deserialize(ref {r}, ref {reference});");
            return;
        }

        // Properties: read through a local so the existing instance is reused, then assign it back.
        _w.Open();
        _w.Line($"var __t = {Get(_m, member, _mode.Target)};");
        _w.Line($"default({formatter}).Deserialize(ref {r}, ref __t);");
        _w.Line(Set(_m, member, _mode.Target, "__t"));
        _w.Close();
    }
}
