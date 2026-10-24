using System;
using System.Collections.Generic;
using System.Linq;
using static Nino.Generator.Generation.Emit.MemberCode;
using static Nino.Generator.Generation.TypeNames;

namespace Nino.Generator.Generation.Emit;

/// <summary>
/// Emits a type's body: <c>WriteBody</c>/<c>ReadBody</c> and the static <c>WriteMembers</c>/<c>ReadMembers</c> that
/// derived types reuse. Three member layouts share the code below:
/// <list type="bullet">
/// <item>positional: members back to back, consecutive raw members as one block;</item>
/// <item>version tolerant: positional, the members of the whole hierarchy in one length-prefixed region, checking
/// for the region's end before each member;</item>
/// <item>keyed: (key, length, value) per member inside a slice, read in order with a fallback dispatch loop.</item>
/// </list>
/// </summary>
internal sealed class BodyEmitter
{
    private readonly TypeModel _m;
    private readonly CodeWriter _w;
    private readonly FormatterNames _names;
    private int _blocks;

    /// <summary>
    /// Which body is being emitted. Set for the extent of one copy of the body (<see cref="In"/>), never changed in
    /// the middle of one.
    /// </summary>
    /// <param name="Shallow">
    /// The <c>__Shallow</c> twin's body (<see cref="TypeModel.ShallowBodies"/>): its members go through the twins of
    /// their formatters, which count and check no depth.
    /// </param>
    /// <param name="Fresh">The reads of a new instance (<see cref="TypeModel.FreshRead"/>).</param>
    /// <param name="Target">
    /// The instance member reads go to: <c>value</c>, or the local a class body caches it in (a store through
    /// <c>ref value</c> would otherwise make the JIT reload the reference after every call).
    /// </param>
    private sealed record BodyMode(bool Shallow, bool Fresh, string Target)
    {
        /// <summary>The type's own formatter's body, reading into <c>value</c>.</summary>
        public static readonly BodyMode Own = new(false, false, "value");
    }

    private BodyMode _mode = BodyMode.Own;

    /// <summary>Emits <paramref name="emit"/> in <paramref name="mode"/>, then returns to the mode around it.</summary>
    private void In(BodyMode mode, Action emit)
    {
        var outer = _mode;
        _mode = mode;
        try
        {
            emit();
        }
        finally
        {
            _mode = outer;
        }
    }

    public BodyEmitter(TypeModel model, CodeWriter writer, FormatterNames names)
    {
        _m = model;
        _w = writer;
        _names = names;
    }

    private string T => _m.TypeName;

    /// <summary>Whether the members are in one length-prefixed region (version tolerant, positional).</summary>
    private bool Tolerant => _m.Tolerant;

    /// <summary>Whether WriteBody/ReadBody put the members in a region (version tolerant or keyed).</summary>
    private bool Region => _m.Tolerant || _m.Keyed;

    private bool Delegates => _m.Inheritance.BaseFormatter.Length != 0 && !(_mode.Shallow && _m.ShallowInlinesBase);

    private IEnumerable<MemberModel> OwnMembers => Delegates ? _m.Members.Skip(_m.Inheritance.BaseMemberCount) : _m.Members;

    private string Formatter(MemberModel member) =>
        _names.Use(_mode.Shallow ? member.ShallowFormatter : member.Formatter);

    private string CollectionFormatter
    {
        get
        {
            var collection = _m.Need(_m.Collection, nameof(_m.Collection));
            return (_mode.Shallow ? collection.ShallowFormatter : collection.Formatter).ToString();
        }
    }

    private string BaseFormatter => _m.Need(_m.Inheritance.BaseFormatter, nameof(_m.Inheritance.BaseFormatter));

    /// <summary>
    /// The bodies of the <c>__Shallow</c> twin (<see cref="TypeModel.ShallowBodies"/>): <c>__WriteBodyShallow</c> and
    /// <c>__ReadBodyShallow</c>, the bodies with their members' formatters replaced by their twins. Out of line, as
    /// the bodies are (a member that has a twin calls a formatter).
    /// </summary>
    public void EmitShallowBodies()
    {
        In(_mode with { Shallow = true }, () =>
        {
            EmitWriteBody();
            _w.Line();
            EmitReadBody();
        });
    }

    // ----- entry ---------------------------------------------------------------------------------------------

    public void EmitRawStruct()
    {
        _w.Line("// Unmanaged and every field serialized: the struct is written as its raw memory.");
        _w.Line(_names.Inline);
        _w.Line($"{Signature.WriteBody(T)} => writer.WriteValue(value);");
        _w.Line();
        _w.Line(_names.Inline);
        _w.Line($"{Signature.ReadBody(T)} => value = reader.ReadValue<{T}>();");
    }

    /// <summary>
    /// A body that calls formatters (strings, collections, other types) is compiled on its own rather than inlined
    /// into the caller: inlined, it would use up the caller's inlining budget (a share of the caller's own size)
    /// before the small helpers it and the caller use (bounds checks, depth tracking, flush) are inlined. Bodies of
    /// raw members only are a few stores and stay inlinable.
    /// </summary>
    private bool OutOfLine => _m.OutOfLineBody;

    /// <summary>
    /// Writes the methods in front of a body method (the instance forwarder, and the wrapper that calls an
    /// inlinable body on a local copy of the reader or writer), with the attributes of the body method, and returns
    /// the body method's signature, for the caller to write its statements under. An out-of-line body is a static method behind an inlined instance forwarder:
    /// a call to an instance method of the (empty) formatter struct stores a dummy byte on the caller's stack and
    /// passes its address as <c>this</c>. A body inlined wherever the formatter is used
    /// (<see cref="TypeModel.InlineBody"/>) is an inlinable static method (<c>__ReadBodyInline</c>) that the forwarder
    /// and the <c>__Top</c> twin call, with no out-of-line copy. An out-of-line body on a local copy of the writer
    /// (<see cref="TypeModel.LocalWriter"/>), or whose new-instance read the <c>__Top</c> twin shares
    /// (<see cref="TypeModel.ReadTop"/>), calls an inlinable one on that copy.
    /// </summary>
    private string EmitForwardersReturningSignature(string name, string parameters)
    {
        bool write = name == "WriteBody";
        // The instance method's parameters, and the static bodies' (a struct's by reference: see ValueArgument).
        string forwarded = parameters;
        string value = write ? ValueArgument(_m) : "ref value";
        if (write && value != "value")
            parameters = "ref " + parameters;
        if (_mode.Shallow)
        {
            _w.Line(_names.NoInline);
            return $"internal static void __{name}Shallow({parameters})";
        }

        if (!OutOfLine)
            return $"public void {name}({forwarded})";
        string body = "__" + name;
        string target = _m.InlineBody ? body + "Inline" : body;
        // A one-call forwarder, which the JIT inlines without being told to.
        _w.Line($"public void {name}({forwarded}) => {target}({value}, ref {(write ? "writer" : "reader")});");
        _w.Line();
        if (_m.InlineBody)
        {
            _w.Line(_names.Inline);
            return $"private static void {target}({parameters})";
        }

        _w.Line(_names.NoInline);
        if (write ? !LocalWriter : !ReadTop)
            return $"private static void {body}({parameters})";
        // The members are written or read by the inlinable twin (__Top's, or one made for this), through the local.
        _w.Block($"private static void {body}({parameters})", () => OnLocalCopy(_w, reader: !write,
            () => _w.Line($"{body}Inline({value}, ref {(write ? "__w" : "__r")});")));
        _w.Line();
        _w.Line(_names.Inline);
        return $"private static void {body}Inline({parameters})";
    }

    /// <summary>
    /// The value as the static write bodies take it: a struct by reference, since passed by value every call copies
    /// it to the stack (an element of a list of structs of 48 bytes: one rep movsq per element). Not <c>in</c>:
    /// reading a property of a struct that is not readonly through an <c>in</c> parameter copies the struct first.
    /// </summary>
    internal static string ValueArgument(TypeModel m) => m.Kind == FormatterKind.Struct ? "ref value" : "value";

    /// <summary>
    /// The instance a class is read into when the slot is null: its <c>[NinoFactory]</c> or constructor, after the
    /// pool of a pooled type. Also the formatter's <c>Create</c>.
    /// </summary>
    internal static string Create(TypeModel m)
    {
        var c = m.Construction;
        string create = (c.Factory.Length != 0 ? c.Factory : m.Need(c.Creator, nameof(c.Creator))) + "()";
        return m.Pooled ? m.TypeName + ".RentFromNinoPool() ?? " + create : create;
    }

    public void EmitWriteBody()
    {
        if (_m.RawBody && !_mode.Shallow)
        {
            EmitRawBody();
            return;
        }

        string signature = EmitForwardersReturningSignature("WriteBody", Signature.WriteParameters(T));
        if (CountsStructDepth)
        {
            // As the read body: a struct that can hold itself opens no class body, so the writer counts its nesting
            // here, and an object graph nested too deep (or holding itself through a collection) throws before the
            // stack runs out.
            _w.Block(signature, () =>
            {
                _w.Line($"writer.EnterStruct(typeof({T}));");
                _w.Open("try");
                EmitWriteBodyContents();
                _w.Close();
                _w.Open("finally");
                _w.Line("writer.ExitStruct();");
                _w.Close();
            });
            if (_m.Inheritance.EmitMembers && !_mode.Shallow)
            {
                _w.Line();
                EmitMembersMethods();
            }

            return;
        }

        if (_m.Inheritance.EmitMembers && !_mode.Shallow && !Region && !_m.IsCollection)
        {
            _w.Line($"{signature} => WriteMembers(value, ref writer);");
            _w.Line();
            EmitMembersMethods();
            return;
        }

        _w.Block(signature, EmitWriteBodyContents);

        if (_m.Inheritance.EmitMembers && !_mode.Shallow)
        {
            _w.Line();
            EmitMembersMethods();
        }
    }

    /// <summary>The writes of a body (<see cref="EmitWriteBody"/>).</summary>
    private void EmitWriteBodyContents()
    {
        if (Region && !(_m.Inheritance.EmitMembers && !_mode.Shallow) && TryEmitRegionWithBlock())
            return;
        // Keyed: the region holds everything; version tolerant: the members (the elements of a collection come
        // first, as positional ones).
        if (_m.Keyed)
            _w.Line("int __length = writer.BeginRegion();");
        if (_m.IsCollection)
            _w.Line($"default({CollectionFormatter}).Serialize(value, ref writer);");
        if (Tolerant)
            _w.Line("int __length = writer.BeginRegion();");
        if (_m.Inheritance.EmitMembers && !_mode.Shallow)
            _w.Line("WriteMembers(value, ref writer);");
        else
            EmitMemberWrites();
        if (Region)
            _w.Line("writer.EndRegion(__length);");
    }

    /// <summary>
    /// The write side of a body that is one raw block (<see cref="TypeModel.RawBody"/>): <c>BodySize</c> and
    /// <c>WriteRawBody</c> (<c>INinoRawBodyFormatter</c>), which the header writes of <c>NinoObject</c> call into
    /// their own reservation, and <c>WriteBody</c> (and <c>WriteMembers</c>) reserving the block for it.
    /// </summary>
    private void EmitRawBody()
    {
        var block = new RawBlock(_m.Members.ToList());
        string self = _m.Self;
        _w.Line($"public int BodySize => {block.Total};");
        _w.Line();
        _w.Line(_names.Inline);
        _w.Block($"public void WriteRawBody({T} value, ref byte destination, int offset)", () =>
        {
            for (int i = 0; i < _m.Members.Length; i++)
                _w.Line(RawWrite(_m.Members[i], "destination", block.Offsets[i] == "0" ? "offset" : "offset + " + block.Offsets[i]));
        });
        _w.Line();
        _w.Line(_names.Inline);
        _w.Line($"{Signature.WriteBody(T)} => WriteRawBody(value, ref writer.Reserve(BodySize), 0);");
        if (!_m.Inheritance.EmitMembers)
            return;
        _w.Line();
        _w.Line(_names.Inline);
        _w.Line($"internal static void WriteMembers({Signature.WriteParameters(T)}) => default({self}).WriteBody(value, ref writer);");
        _w.Line();
        _w.Line(_names.Inline);
        _w.Block($"internal static void ReadMembers({T} value, ref {Core}Reader reader)",
            () => EmitMemberReads("reader", inPlace: true, eof: "return;"));
    }

    /// <summary>Static member (de)serializers that derived types call instead of repeating these members.</summary>
    public void EmitMembersMethods()
    {
        // Inlined so the writer/reader state stays in registers across levels, as if the members were written inline.
        _w.Line(_names.Inline);
        _w.Block($"internal static void WriteMembers({Signature.WriteParameters(T)})", EmitMemberWrites);
        _w.Line();
        _w.Line(_names.Inline);
        // Version tolerant: reading the region of the type that called it, which may end before any member.
        _w.Block($"internal static void ReadMembers({T} value, ref {Core}Reader reader)",
            () => EmitMemberReads("reader", inPlace: true, eof: "return;"));
    }

    /// <summary>
    /// <c>FixedSize</c> and <c>WriteFixed</c> of a type of fixed size (<see cref="TypeModel.FixedSize"/>): the bytes of
    /// <c>Serialize</c> for a non-null value (a sealed class's flag, then the members at constant offsets) written
    /// into memory the caller sized, without a writer.
    /// </summary>
    public void EmitFixed()
    {
        int constant = 0;
        var symbolic = new List<string>();
        string Offset()
        {
            var parts = new List<string>(symbolic);
            if (constant != 0 || parts.Count == 0)
                parts.Insert(0, constant.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return string.Join(" + ", parts);
        }

        var lines = new List<string>();
        if (_m.Kind == FormatterKind.SealedClass)
        {
            lines.Add($"destination = {NinoWire.NotNull};");
            constant = 1;
        }


        foreach (var member in _m.Members)
        {
            string offset = Offset();
            if (member.Raw != null)
            {
                lines.Add(RawWrite(member, "destination", offset));
                if (member.Raw.IsConstant)
                    constant += member.Raw.Bytes;
                else
                    symbolic.Add(member.Raw.Expression);
                continue;
            }

            string formatter = Formatter(member);
            lines.Add($"default({formatter}).WriteFixed({Get(_m, member, "value")}, ref {CoreGenerated}NinoUnsafe.Add(ref destination, {offset}));");
            symbolic.Add($"default({formatter}).FixedSize");
        }

        _w.Line($"public int FixedSize => {Offset()};");
        _w.Line();
        _w.Line(_names.Inline);
        _w.Block($"public void WriteFixed({T} value, ref byte destination)", () =>
        {
            foreach (var line in lines)
                _w.Line(line);
        });
    }

    // ----- writes --------------------------------------------------------------------------------------------

    private void EmitMemberWrites()
    {
        _blocks = 0;
        if (Delegates)
            _w.Line($"{BaseFormatter}.WriteMembers(value, ref writer);");
        if (_m.Keyed)
        {
            EmitKeyedWrites(RawBlock.Groups(_m.Members));
            return;
        }

        EmitPositionalWrites(OwnMembers);
    }

    /// <summary>
    /// A version-tolerant or keyed body whose first members are raw values (written here, not by a base's
    /// WriteMembers, nor after a collection's elements): the region's length and those members (keyed: with their
    /// headers) in one reservation, one capacity check rather than two.
    /// </summary>
    private bool TryEmitRegionWithBlock()
    {
        if (Delegates || _m.IsCollection)
            return false;
        var groups = RawBlock.Groups(OwnMembers);
        if (groups.Count == 0 || groups[0][0].Raw == null)
            return false;
        _blocks = 0;
        var block = new RawBlock(groups[0], headers: _m.Keyed, start: NinoWire.LengthSize);
        EmitRawBlock(groups[0], block, $"writer.BeginRegion({block.Total}, out int __length)");
        if (_m.Keyed)
            EmitKeyedWrites(groups.Skip(1));
        else
            EmitPositionalWrites(groups.Skip(1).SelectMany(g => g));
        _w.Line("writer.EndRegion(__length);");
        return true;
    }

    /// <summary>
    /// The raw members of <paramref name="block"/> (keyed: each after its key and length) in the reservation that
    /// <paramref name="reserve"/> returns.
    /// </summary>
    private string EmitRawBlock(List<MemberModel> group, RawBlock block, string reserve)
    {
        string p = "__p" + _blocks++;
        _w.Line($"ref byte {p} = ref {reserve};");
        bool headers = block.HeaderOffsets.Count != 0;
        for (int i = 0; i < group.Count; i++)
        {
            if (headers)
            {
                _w.Line($"{CoreGenerated}NinoUnsafe.Write<int>(ref {p}, {block.HeaderOffsets[2 * i]}, {group[i].Key});");
                _w.Line($"{CoreGenerated}NinoUnsafe.Write<int>(ref {p}, {block.HeaderOffsets[2 * i + 1]}, {group[i].Raw!.Expression});");
            }

            _w.Line(RawWrite(group[i], p, block.Offsets[i]));
        }

        return p;
    }

    private void EmitPositionalWrites(IEnumerable<MemberModel> members)
    {
        foreach (var group in RawBlock.Groups(members))
        {
            var first = group[0];
            if (first.Raw == null)
            {
                EmitFormatterWrite(first);
                continue;
            }

            if (group.Count == 1)
            {
                if (WriteFrom(first) != null)
                {
                    _w.Line(RawWrite(first, $"writer.Reserve({first.Raw!.Expression})", "0"));
                    continue;
                }

                string method = RawBlock.IsStruct(first) ? "WriteValue" : "UnsafeWrite";
                _w.Line($"writer.{method}<{first.TypeName}>({Get(_m, first, "value")});");
                continue;
            }

            var block = new RawBlock(group);
            EmitRawBlock(group, block, $"writer.Reserve({block.Total})");
        }
    }

    private void EmitFormatterWrite(MemberModel member)
    {
        string value = Get(_m, member, "value");
        _w.Line(DirectWrite(member, value) ?? $"default({Formatter(member)}).Serialize({value}, ref writer);");
    }

    // Struct members may have padding, which WriteValue clears.
    private string RawWrite(MemberModel member, string p, string offset)
    {
        string method = RawBlock.IsStruct(member) ? "WriteValue" : "Write";
        return WriteFrom(member) is { } field
            ? $"{CoreGenerated}NinoUnsafe.{method}In<{member.TypeName}>(ref {p}, {offset}, in {field});"
            : $"{CoreGenerated}NinoUnsafe.{method}<{member.TypeName}>(ref {p}, {offset}, {Get(_m, member, "value")});";
    }

    /// <summary>
    /// The field to write a raw member from by reference (<c>NinoUnsafe.WriteIn</c>), or null to pass it by value:
    /// fields wider than 8 bytes (Guid, decimal, structs), which by value the JIT copies to the stack and loads
    /// again. Narrower values are better by value, where the load folds the field offset.
    /// </summary>
    private string? WriteFrom(MemberModel member) =>
        !member.IsProperty && member.Access is MemberAccess.Direct or MemberAccess.Cast &&
        (RawBlock.IsStruct(member) || member.Raw!.Bytes > 8)
            ? MemberCode.Ref(_m, member, "value")
            : null;

    /// <summary>
    /// Each member as (key, length, value). Consecutive raw members share one reservation, which also holds the key
    /// and length of the member after them; any other member's key and length are one reservation.
    /// </summary>
    private void EmitKeyedWrites(IEnumerable<List<MemberModel>> groups)
    {
        int lengths = 0;
        List<MemberModel>? run = null;
        foreach (var group in groups)
        {
            var first = group[0];
            if (first.Raw != null)
            {
                run = group;
                continue;
            }

            string l = "__l" + lengths++;
            var block = new RawBlock(run ?? new List<MemberModel>(), headers: true);
            string reserve = $"writer.BeginRegionAfter({block.Plus(NinoWire.KeySize)}, out int {l})";
            string p = run != null ? EmitRawBlock(run, block, reserve) : "__p" + _blocks++;
            if (run == null)
                _w.Line($"ref byte {p} = ref {reserve};");
            _w.Line($"{CoreGenerated}NinoUnsafe.Write<int>(ref {p}, {block.Total}, {first.Key});");
            run = null;
            EmitFormatterWrite(first);
            _w.Line($"writer.EndRegion({l});");
        }

        if (run != null)
        {
            var block = new RawBlock(run, headers: true);
            EmitRawBlock(run, block, $"writer.Reserve({block.Total})");
        }
    }

    // ----- reads ---------------------------------------------------------------------------------------------

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

    /// <summary>The reads of a body, on a local copy of the reader when <paramref name="local"/>.</summary>
    private void EmitReadBodyContents(bool fresh, bool local)
    {
        if (local)
            OnLocalCopy(_w, reader: true, () => EmitReads(fresh, "__r"));
        else
            EmitReads(fresh, "reader");
    }

    private void EmitReads(bool fresh, string r)
    {
        if (fresh)
        {
            // The slot held null and the pool, if any, had nothing: a new instance, whose members are null.
            // The test always passes (the forwarder calls this for a null slot only), but it is kept: a method
            // without a branch gets no profile from tiered PGO, and without one the JIT stops inlining the
            // collection constructors of the member reads. An instance passed in anyway is still read correctly
            // (its members are replaced, not reused).
            ReadIntoLocal(_m.Need(_m.Construction.Creator, nameof(ConstructionModel.Creator)) + "()");
            In(_mode with { Fresh = true, Target = "__v" }, () => EmitInPlaceMembers(r));
        }
        else if (_m.Construction.Recreate)
            EmitRecreatingRead(r);
        else
            EmitInPlaceRead(r);
    }

    /// <summary>
    /// <paramref name="body"/> on a local copy of the reader (<c>__r</c>) or writer (<c>__w</c>), copied from the
    /// parameter before and written back after (see <see cref="TypeModel.LocalReader"/>).
    /// </summary>
    internal static void OnLocalCopy(CodeWriter w, bool reader, Action body)
    {
        var (type, parameter, local) = reader ? ("Reader", "reader", "__r") : ("Writer", "writer", "__w");
        w.Line($"{Core}{type} {local} = default;");
        w.Line($"{parameter}.CopyTo(ref {local});");
        body();
        w.Line($"{local}.CopyTo(ref {parameter});");
    }

    /// <summary>
    /// The local <c>__v</c>, which the reads that follow go to (<see cref="BodyMode.Target"/>): the instance in
    /// <c>value</c>, or when null a new one (<paramref name="create"/>) stored in both.
    /// </summary>
    private void ReadIntoLocal(string create)
    {
        _w.Line("var __v = value;");
        _w.Line("if (__v == null)");
        _w.Indented(() => _w.Line($"value = __v = {create};"));
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
            _w.Line($"{body}Fresh(ref value, ref reader);");
            _w.Line("return;");
            _w.Close();
            _w.Line("value = __p;");
        }
        else
        {
            _w.Line($"{body}Fresh(ref value, ref reader);");
            _w.Line("return;");
        }

        _w.Close();
        _w.Line($"{body}(ref value, ref reader);");
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

        _w.Line(_names.NoInline);
        _w.Open($"internal static void {body}({parameters})");
        EmitReadBodyContents(fresh: false, local: LocalReader && !_mode.Shallow);
        _w.Close();
        _w.Line();
        _w.Line(_names.NoInline);
        if (ReadTop)
        {
            // The new-instance read is also __ReadTop's: inlinable, called here on a local copy of the reader.
            _w.Block($"internal static void {body}Fresh({parameters})",
                () => OnLocalCopy(_w, reader: true, () => _w.Line($"{body}FreshInline(ref value, ref __r);")));
            _w.Line();
            _w.Line(_names.Inline);
            _w.Open($"private static void {body}FreshInline({parameters})");
            EmitReadBodyContents(fresh: true, local: false);
            _w.Close();
            return;
        }

        _w.Open($"internal static void {body}Fresh({parameters})");
        EmitReadBodyContents(fresh: true, local: LocalReader && !_mode.Shallow);
        _w.Close();
    }

    private void EmitInPlaceRead(string r)
    {
        if (_m.Kind != FormatterKind.Struct)
        {
            string create = Create(_m);
            // The instance in a local (a collection formatter may replace value, so not for collections).
            if (!_m.IsCollection)
            {
                ReadIntoLocal(create);
                In(_mode with { Target = "__v" }, () => EmitInPlaceMembers(r));
                return;
            }

            _w.Line("if (value == null)");
            _w.Indented(() => _w.Line($"value = {create};"));
        }

        EmitInPlaceMembers(r);
    }

    /// <summary>The reads of an instance in <see cref="BodyMode.Target"/> (or of a struct in place), in its regions.</summary>
    private void EmitInPlaceMembers(string r)
    {
        if (_m.TrackReferences)
            _w.Line($"reader.SetReference({_mode.Target});");

        // Keyed: the region holds everything; version tolerant: the members (the elements of a collection come first).
        string outer = r;
        if (_m.Keyed)
            r = BeginRegionRead(r);
        if (_m.IsCollection)
            _w.Line($"default({CollectionFormatter}).Deserialize(ref value, ref {r});");
        if (Tolerant)
            r = BeginRegionRead(r);

        if (_m.Inheritance.EmitMembers && !_mode.Shallow && !_mode.Fresh)
            _w.Line($"ReadMembers({_mode.Target}, ref {r});");
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
            _w.Line(EndLabel + ":");
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
            _w.Line($"throw new global::System.NotSupportedException(\"Nino: cannot construct {EscapeString(T)}\");");
            return;
        }

        // A class is built in a local and then replaces the old instance (which is recycled); a struct is
        // assigned in place.
        bool isClass = _m.Kind != FormatterKind.Struct;
        string target = isClass ? "__new" : "value";
        string assign = isClass ? "var __new = " : "value = ";

        var args = new HashSet<string>(c.Arguments);
        var assigned = _m.Members.Where(x => !args.Contains(x.Name) && !x.Unassignable).ToList();
        var initializers = assigned.Where(x => x.Access == MemberAccess.Direct).ToList();
        string call = c.Creator + "(" +
                      string.Join(", ", c.Arguments.Select(a => Local(_m.Members.First(x => x.Name == a)))) + ")";
        if (initializers.Count == 0)
        {
            _w.Line($"{assign}{call};");
        }
        else
        {
            _w.Line($"{assign}{call}");
            _w.Open();
            foreach (var member in initializers)
                _w.Line($"{Identifier(member.SourceName)} = {Local(member)},");
            _w.Close(";");
        }

        foreach (var member in assigned.Where(x => x.Access != MemberAccess.Direct))
            _w.Line(Set(_m, member, target, Local(member)));

        if (isClass)
            _w.Line($"{Core}NinoRecycler.Replace(ref value, __new);");
    }

    /// <param name="eof">
    /// Version tolerant: the statement that ends the reads when the region ends before a member (older data).
    /// </param>
    private void EmitMemberReads(string r, bool inPlace, string eof)
    {
        _blocks = 0;
        // Base members through the base formatter (in place only: recreated types read everything into locals).
        bool delegates = inPlace && Delegates;
        var members = inPlace ? OwnMembers : _m.Members;
        if (delegates)
            _w.Line($"{BaseFormatter}.ReadMembers({_mode.Target}, ref {r});");

        if (_m.Keyed)
        {
            EmitKeyedReads(members.ToList(), r, inPlace);
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
            _w.Open($"if ((uint){r}.Remaining >= (uint)({block.Total}))");
            EmitRawBlockRead(group, block, r, inPlace);
            _w.Close();
            _w.Open("else");
            foreach (var member in group)
            {
                _w.Line($"if ({r}.Eof) {eof}");
                EmitMemberRead(member, r, inPlace);
            }

            _w.Close();
        }
    }

    /// <summary>Whether <paramref name="member"/> is a <c>bool</c>, which is read with a check of its byte.</summary>
    private static bool IsBool(MemberModel member) => member.TypeName is "bool" or "global::System.Boolean";

    /// <summary>
    /// Whether the raw <paramref name="member"/> may hold values no constructor makes, which Nino.Core checks in
    /// <c>NinoSettings.Untrusted</c> mode (<c>ReadChecked</c>): a struct (not a primitive or enum, whose sizes are
    /// constants), or a <c>DateTime</c>, <c>decimal</c>, <c>DateOnly</c> or <c>TimeOnly</c>.
    /// </summary>
    private static bool MayCheck(MemberModel member) =>
        !member.Raw!.IsConstant || member.TypeName is "global::System.DateTime" or "decimal" or "global::System.Decimal"
            or "global::System.DateOnly" or "global::System.TimeOnly";

    private static string RawRead(MemberModel member, string p, string offset) =>
        IsBool(member) ? $"{CoreGenerated}NinoUnsafe.ReadBool(ref {p}, {offset})"
        : MayCheck(member) ? $"{CoreGenerated}NinoUnsafe.ReadChecked<{member.TypeName}>(ref {p}, {offset})"
        : $"{CoreGenerated}NinoUnsafe.Read<{member.TypeName}>(ref {p}, {offset})";

    /// <summary>The raw members of <paramref name="group"/>, read from one block of <paramref name="r"/>.</summary>
    private void EmitRawBlockRead(List<MemberModel> group, RawBlock block, string r, bool inPlace)
    {
        string p = "__p" + _blocks++;
        _w.Line($"ref byte {p} = ref {r}.ReadBlock({block.Total});");
        for (int i = 0; i < group.Count; i++)
        {
            // A bool through its own read, which rejects a byte other than 0 or 1; types with values to check in
            // Untrusted mode through theirs.
            _w.Line(Store(_m, group[i], inPlace, RawRead(group[i], p, block.Offsets[i]), _mode.Target));
        }
    }

    /// <summary>Reads one member: raw members directly, others through their formatter.</summary>
    private void EmitMemberRead(MemberModel member, string r, bool inPlace)
    {
        if (member.Raw != null)
        {
            _w.Line(Store(_m, member, inPlace,
                IsBool(member) ? $"{r}.ReadBool()"
                : MayCheck(member) ? $"{r}.ReadChecked<{member.TypeName}>()"
                : $"{r}.UnsafeRead<{member.TypeName}>()", _mode.Target));
            return;
        }

        // An existing instance's string is passed in, and kept when unchanged; a new instance's is null.
        bool existing = inPlace && !(_mode.Fresh && member.Fresh);
        var direct = DirectRead(member, r, existing ? Get(_m, member, _mode.Target) : null);
        if (direct != null)
        {
            _w.Line(Store(_m, member, inPlace, direct, _mode.Target));
            return;
        }

        string formatter = Formatter(member);
        if (!inPlace)
        {
            _w.Line($"default({formatter}).Deserialize(ref {Local(member)}, ref {r});");
            return;
        }

        if (_mode.Fresh && member.Fresh)
        {
            // The member of a new instance is null: read from a null local, which the formatter (inlined) knows,
            // so its reuse path folds away; then stored.
            _w.Open();
            _w.Line($"{member.TypeName} __t = null;");
            _w.Line($"default({formatter}).Deserialize(ref __t, ref {r});");
            _w.Line(Set(_m, member, _mode.Target, "__t"));
            _w.Close();
            return;
        }

        var reference = Ref(_m, member, _mode.Target);
        if (reference != null)
        {
            _w.Line($"default({formatter}).Deserialize(ref {reference}, ref {r});");
            return;
        }

        // Properties: read through a local so the existing instance is reused, then assign it back.
        _w.Open();
        _w.Line($"var __t = {Get(_m, member, _mode.Target)};");
        _w.Line($"default({formatter}).Deserialize(ref __t, ref {r});");
        _w.Line(Set(_m, member, _mode.Target, "__t"));
        _w.Close();
    }

    // ----- keyed ---------------------------------------------------------------------------------------------

    /// <summary>(key, length) as the int64 a reader sees, for raw members of constant size; else null.</summary>
    private static string? KeyHeader(MemberModel member)
    {
        if (member.Raw is not { IsConstant: true } raw)
            return null;
        int size = raw.Bytes;
        uint key = unchecked((uint)member.Key);
        long le = (long)size << 32 | key;
        long be = (long)key << 32 | (uint)size;
        return le == be ? $"{le}L" : $"(global::System.BitConverter.IsLittleEndian ? {le}L : {be}L)";
    }

    /// <summary>
    /// Reads members written in declaration order straight through (raw members check key and length with one
    /// 8-byte compare); at the first unexpected key, falls back to a loop that dispatches any key.
    /// </summary>
    private void EmitKeyedReads(List<MemberModel> members, string r, bool inPlace)
    {
        foreach (var member in members)
        {
            var header = KeyHeader(member);
            if (header != null)
            {
                _w.Line($"if (!{r}.TryReadInt64({header})) goto __keyed;");
                EmitMemberRead(member, r, inPlace);
                continue;
            }

            _w.Line($"if (!{r}.TryReadInt32({member.Key})) goto __keyed;");
            _w.Open();
            _w.Line($"var __m = {r}.ReadRegion();");
            EmitMemberRead(member, "__m", inPlace);
            // A member's value fills its entry exactly: bytes left over are invalid data.
            _w.Line($"{r}.EndMember(in __m);");
            _w.Close();
        }

        // Unknown members appended by a newer version, or members in another order.
        if (members.Count != 0)
            _w.Line("__keyed:");
        _w.Open($"while (!{r}.Eof)");
        _w.Line($"int __key = {r}.UnsafeRead<int>();");
        _w.Line($"var __m = {r}.ReadRegion();");
        if (members.Count != 0)
        {
            _w.Open("switch (__key)");
            foreach (var member in members)
            {
                _w.Line($"case {member.Key}:");
                // Data written before a [NinoFormerName] rename; writes always use the current name.
                foreach (int formerKey in member.FormerKeys)
                    _w.Line($"case {formerKey}:");
                _w.Open();
                EmitMemberRead(member, "__m", inPlace);
                _w.Line($"{r}.EndMember(in __m);");
                _w.Line("break;");
                _w.Close();
            }

            _w.Close();
        }

        _w.Close();
    }
}
