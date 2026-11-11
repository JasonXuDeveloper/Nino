using System;
using System.Linq;
using MemberRange = Nino.Generator.Generation.Emit.RawBlock.MemberRange;
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
internal sealed partial class BodyEmitter
{
    /// <summary>
    /// Keyed fallback switches of at most this many members open the member reader in each known case (the JIT then
    /// drops the unused child reader: 1,000 reused <c>KeyedStruct</c>s 8.4 to 7.4 µs, M4); larger switches share one
    /// region read so it is not repeated at every case (`audit-generated-code`, read-paths.md).
    /// </summary>
    private const int MaxSplitRegionMembers = 3;

    /// <summary>
    /// Constructor argument lists up to this long are scanned for each member; longer ones go through a set (the unit
    /// tests' constructors take at most three; `audit-generator`, emit-text.md).
    /// </summary>
    private const int MaxScannedArguments = 8;

    private readonly TypeModel _m;
    private readonly CodeWriter _w;
    private readonly FormatterNames _names;
    private int _blocks;

    /// <summary>
    /// Which body is being emitted. Set for the extent of one copy of the body (<see cref="InMode"/>), never changed in
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
    private readonly record struct BodyMode(bool Shallow, bool Fresh, string Target)
    {
        /// <summary>The type's own formatter's body, reading into <c>value</c>.</summary>
        public static readonly BodyMode Own = new(false, false, "value");
    }

    private BodyMode _mode = BodyMode.Own;

    /// <summary>Switches to a mode until the emitted section ends.</summary>
    private ModeScope InMode(BodyMode mode) => new(this, mode);

    private readonly struct ModeScope : IDisposable
    {
        private readonly BodyEmitter _emitter;
        private readonly BodyMode _previous;

        public ModeScope(BodyEmitter emitter, BodyMode mode)
        {
            _emitter = emitter;
            _previous = emitter._mode;
            emitter._mode = mode;
        }

        public void Dispose() => _emitter._mode = _previous;
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

    private MemberRange AllMembers => new(_m.Members, 0, _m.Members.Length);

    private MemberRange OwnMembers => Delegates
        ? AllMembers.Slice(_m.Inheritance.BaseMemberCount, _m.Members.Length - _m.Inheritance.BaseMemberCount)
        : AllMembers;

    private string Formatter(MemberModel member) =>
        (_mode.Shallow ? member.ShallowFormatter : member.Formatter).ToString();

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
        using (InMode(_mode with { Shallow = true }))
        {
            EmitWriteBody();
            _w.Line();
            EmitReadBody();
        }
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
    /// Writes the methods in front of a body method (an instance forwarder, and the wrapper that calls an inlinable
    /// body on a local copy of the reader or writer), with the attributes of the body method, and returns the body
    /// method's signature, for the caller to write its statements under. Where the body goes depends on who calls it:
    /// <list type="bullet">
    /// <item>A body of raw members only (not <see cref="OutOfLine"/>) is the instance <c>WriteBody</c> /
    /// <c>ReadBody</c> itself, AggressiveInlining.</item>
    /// <item>An out-of-line read body that only the formatter's own <c>ReadBody</c> calls (most of them) is that
    /// instance method itself, <c>[NoInline]</c>: merging it with its forwarder kept the no-inline boundary, removed
    /// the forwarder's source and IL, and measured on par or faster (709af0ae; read-paths.md).</item>
    /// <item>An out-of-line write body stays a static <c>[NoInline]</c> method (<c>__WriteBody</c>) behind an
    /// inlined instance forwarder. As the instance method itself (8ca095f4, reverted), every call stored a dummy
    /// byte on the caller's stack and passed its address as <c>this</c>, and a subtype dispatch calls one body per
    /// element: an array of 1,000 subtypes with a tuple member serialized 16-22% slower on x64, and on par again
    /// with this and the exact dispatch checks restored (write-paths.md).</item>
    /// <item>A body inlined wherever the formatter is used (<see cref="TypeModel.InlineBody"/>) is an inlinable static
    /// method (<c>__ReadBodyInline</c>, <c>__WriteBodyInline</c>) that the forwarder and the <c>__Top</c> twin call,
    /// with no out-of-line copy.</item>
    /// <item>A body another method shares stays a static <c>[NoInline]</c> method (<c>__WriteBody</c>,
    /// <c>__ReadBody</c>) behind an inlined forwarder: a write on a local copy of the writer
    /// (<see cref="TypeModel.LocalWriter"/>) or a read whose new-instance path the <c>__Top</c> twin shares
    /// (<see cref="TypeModel.ReadTop"/>) calls an inlinable <c>...Inline</c> twin on that copy.</item>
    /// <item>A struct's write stays a static method, to take the struct by reference (<see cref="ValueArgument"/>): an
    /// instance body taking it by value grew its machine code by up to twice and wrote lists of structs 5-13% slower.</item>
    /// </list>
    /// Shallow bodies (<see cref="EmitShallowBodies"/>) are always static <c>__WriteBodyShallow</c> /
    /// <c>__ReadBodyShallow</c> methods.
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

        // A read out of line, and called only through this instance method: the body is the method itself. Write
        // bodies, and a read body that a twin shares, stay static, below.
        if (!write && OutOfLine && !_m.InlineBody && !ReadTop)
        {
            _w.Line(_names.NoInline);
            return $"public void {name}({forwarded})";
        }

        // AggressiveInlining, as the entry points (see FormatterEmitter.EmitFormatter).
        _w.Line(_names.Inline);
        if (!OutOfLine)
            return $"public void {name}({forwarded})";
        string body = "__" + name;
        string target = _m.InlineBody ? body + "Inline" : body;
        // Reads take the reader first, writes the value first, as the formatter interfaces do.
        _w.Line($"public void {name}({forwarded}) => {target}({(write ? $"{value}, ref writer" : "ref reader, ref value")});");
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
        _w.Open($"private static void {body}({parameters})");
        BeginLocalCopy(_w, reader: !write);
        _w.Line($"{body}Inline({(write ? $"{value}, ref __w" : "ref __r, ref value")});");
        EndLocalCopy(_w, reader: !write);
        _w.Close();
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
    internal static string Create(TypeModel m, bool compactSyntax)
    {
        var c = m.Construction;
        string creator = c.Factory.Length != 0 ? c.Factory : m.Need(c.Creator, nameof(c.Creator));
        if (compactSyntax && !m.Pooled && creator.StartsWith("new ", StringComparison.Ordinal))
            return "new()";
        string create = creator + "()";
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
            _w.Open(signature);
            _w.Line($"writer.EnterStruct(typeof({T}));");
            _w.Open("try");
            EmitWriteBodyContents();
            _w.Close();
            _w.Open("finally");
            _w.Line("writer.ExitStruct();");
            _w.Close();
            _w.Close();
            if (_m.Inheritance.EmitMembers && !_mode.Shallow)
            {
                _w.Line();
                EmitMembersMethods();
            }

            return;
        }

        if (_m.Block && !_mode.Shallow)
        {
            // Keep the body's inlining boundary, but reserve all fixed-size writes in one step.
            _w.Open(signature);
            _w.Line($"var __formatter = default({_m.Self});");
            _w.Line("__formatter.WriteFixed(in value, ref writer.Reserve(__formatter.FixedSize));");
            _w.Close();
            return;
        }

        if (_m.Inheritance.EmitMembers && !_mode.Shallow && !Region && !_m.IsCollection && _m.Callbacks == null)
        {
            _w.Line($"{signature} => WriteMembers(value, ref writer);");
            _w.Line();
            EmitMembersMethods();
            return;
        }

        _w.Open(signature);
        EmitWriteBodyContents();
        _w.Close();

        if (_m.Inheritance.EmitMembers && !_mode.Shallow)
        {
            _w.Line();
            EmitMembersMethods();
        }
    }

    /// <summary>The writes of a body (<see cref="EmitWriteBody"/>), between the callbacks around them.</summary>
    private void EmitWriteBodyContents()
    {
        EmitCallbacks(CallbackKind.Serializing, "value");
        EmitWrites();
        EmitCallbacks(CallbackKind.Serialized, "value");
    }

    /// <summary>
    /// The calls of the <paramref name="kind"/> callbacks (<see cref="TypeModel.Callbacks"/>) on
    /// <paramref name="target"/>, the value; the static ones when it is null (before an instance exists). Nothing for a
    /// type without callbacks.
    /// </summary>
    private void EmitCallbacks(CallbackKind kind, string? target)
    {
        if (_m.Callbacks is not { } callbacks)
            return;
        foreach (var call in callbacks.Of(kind))
        {
            if (call.Static != (target == null))
                continue;
            _w.Line(call.Static ? $"{call.Owner}.{call.Method}();"
                : call.Cast ? $"(({call.Owner}){target}).{call.Method}();"
                : $"{target}.{call.Method}();");
        }
    }

    private static bool CanPatchMember(MemberModel member) =>
        member.Raw != null || !member.OpensBodies ||
        (member.Traits & (FormatterTraits.String | FormatterTraits.Utf8String)) != 0;

    // Version-tolerant collection elements are written before the member region opens.
    private bool CanPatchBody =>
        (Tolerant || _m.Collection is not { OpensBodies: true }) && _m.Members.All(CanPatchMember);

    private void EmitWrites()
    {
        if (Region && !(_m.Inheritance.EmitMembers && !_mode.Shallow) && TryEmitRegionWithBlock())
            return;
        // Keyed: the region holds everything; version tolerant: the members (the elements of a collection come
        // first, as positional ones).
        bool patchOnly = Region && CanPatchBody;
        if (_m.Keyed)
            _w.Line(patchOnly ? "int __length = writer.ReserveInt32();" : "int __length = writer.BeginRegion();");
        if (_m.IsCollection)
            _w.Line($"default({CollectionFormatter}).Serialize(value, ref writer);");
        if (Tolerant)
            _w.Line(patchOnly ? "int __length = writer.ReserveInt32();" : "int __length = writer.BeginRegion();");
        if (_m.Inheritance.EmitMembers && !_mode.Shallow)
            _w.Line("WriteMembers(value, ref writer);");
        else
            EmitMemberWrites();
        if (Region)
            _w.Line(patchOnly ? "writer.PatchLength(__length);" : "writer.EndRegion(__length);");
    }

    /// <summary>
    /// The write side of a body that is one raw block (<see cref="TypeModel.RawBody"/>): <c>BodySize</c> and
    /// <c>WriteRawBody</c> (<c>INinoRawBodyFormatter</c>), which the header writes of <c>NinoObject</c> call into
    /// their own reservation, and <c>WriteBody</c> (and <c>WriteMembers</c>) reserving the block for it.
    /// </summary>
    private void EmitRawBody()
    {
        var block = new RawBlock(AllMembers);
        string self = _m.Self;
        _w.Line($"public int BodySize => {block.Total};");
        _w.Line();
        _w.Line(_names.Inline);
        _w.Open($"public void WriteRawBody({T} value, ref byte destination, int offset)");
        for (int i = 0; i < _m.Members.Length; i++)
            _w.Line(RawWrite(_m.Members[i], "destination", block.Offsets[i] == "0" ? "offset" : "offset + " + block.Offsets[i]));
        _w.Close();
        _w.Line();
        _w.Line(_names.Inline);
        _w.Line($"{Signature.WriteBody(T)} => WriteRawBody(value, ref writer.Reserve(BodySize), 0);");
        if (!_m.Inheritance.EmitMembers)
            return;
        _w.Line();
        _w.Line(_names.Inline);
        _w.Line($"{Signature.WriteMembers(T)} => default({self}).WriteBody(value, ref writer);");
        _w.Line();
        _w.Line(_names.Inline);
        _w.Open(Signature.ReadMembers(T));
        EmitMemberReads("reader", inPlace: true, eof: "return;");
        _w.Close();
    }

    /// <summary>Static member (de)serializers that derived types call instead of repeating these members.</summary>
    public void EmitMembersMethods()
    {
        // Inlined so the writer/reader state stays in registers across levels, as if the members were written inline.
        _w.Line(_names.Inline);
        _w.Open(Signature.WriteMembers(T));
        EmitMemberWrites();
        _w.Close();
        _w.Line();
        _w.Line(_names.Inline);
        // Version tolerant: reading the region of the type that called it, which may end before any member.
        _w.Open(Signature.ReadMembers(T));
        EmitMemberReads("reader", inPlace: true, eof: "return;");
        _w.Close();
    }

    /// <summary>
    /// The size of a fixed-size value and, when requested, its member offsets in serialization order.
    /// A sealed class's flag comes first. Constant sizes are summed into each expression.
    /// </summary>
    private (string[] Offsets, string Size) FixedLayout(bool emitOffsets)
    {
        int constant = _m.Kind == FormatterKind.SealedClass ? 1 : 0;
        string symbolic = "";

        var members = _m.Members;
        var offsets = emitOffsets ? new string[members.Length] : Array.Empty<string>();
        for (int i = 0; i < members.Length; i++)
        {
            var member = members[i];
            if (emitOffsets)
                offsets[i] = RawBlock.Offset(constant, symbolic);
            if (member.Raw is { IsConstant: true } raw)
            {
                constant += raw.Bytes;
                continue;
            }

            string size = member.Raw?.Expression ?? (IsBlockItem(member)
                ? $"{CoreCompilerServices}NinoBlocks.FixedSize<{member.TypeName}, {Formatter(member)}>()"
                : $"default({Formatter(member)}).FixedSize");
            symbolic = RawBlock.AppendSymbolic(symbolic, size);
        }

        return (offsets, RawBlock.Offset(constant, symbolic));
    }

    /// <summary>
    /// Whether a member of a type of fixed size is a value tuple or pair of raw values (<c>TypeFacts.IsBlockItem</c>),
    /// written by Nino.Core's formatter as a block (<c>NinoBlocks</c>); else, when not raw, it is a generated struct of
    /// fixed size.
    /// </summary>
    private static bool IsBlockItem(MemberModel member) =>
        member.Raw == null && (member.Traits & FormatterTraits.Generated) == 0;

    /// <summary>The statements writing the members of a value of fixed size at <c>destination</c> (<see cref="FixedLayout"/>).</summary>
    private void EmitFixedWrites(string[] offsets)
    {
        if (_m.Kind == FormatterKind.SealedClass)
            _w.Line($"destination = {NinoWire.NotNull};");
        for (int i = 0; i < offsets.Length; i++)
        {
            var member = _m.Members[i];
            string offset = offsets[i];
            if (member.Raw != null)
            {
                _w.Line(RawWrite(member, "destination", offset));
                continue;
            }

            string formatter = Formatter(member);
            string at = $"ref {CoreCompilerServices}NinoUnsafe.Add(ref destination, {offset})";
            _w.Line(IsBlockItem(member)
                ? $"{CoreCompilerServices}NinoBlocks.WriteFixed<{member.TypeName}, {formatter}>({Get(_m, member, "value")}, {at});"
                : $"default({formatter}).WriteFixed({Get(_m, member, "value")}, {at});");
        }
    }

    /// <summary>
    /// <c>FixedSize</c> and <c>WriteFixed</c> of a type of fixed size (<see cref="TypeModel.FixedSize"/>): the bytes of
    /// <c>Serialize</c> for a non-null value (a sealed class's flag, then the members at constant offsets) written
    /// into memory the caller sized, without a writer. A block struct's collections write each value with it too.
    /// </summary>
    public void EmitFixed()
    {
        bool rawBody = _m.RawBody;
        var (offsets, size) = FixedLayout(emitOffsets: !rawBody);
        _w.Line($"public int FixedSize => {size};");
        _w.Line();
        _w.Line(_names.Inline);
        string signature = $"public void WriteFixed(in {T} value, ref byte destination)";
        if (rawBody)
        {
            _w.Open(signature);
            // Storing through the destination must not change which instance the body writes.
            _w.Line("var __value = value;");
            _w.Line($"destination = {NinoWire.NotNull};");
            _w.Line("WriteRawBody(__value, ref destination, 1);");
            _w.Close();
            return;
        }

        // A struct with a member generated code reaches through an accessor: those take the struct by `ref`, which an
        // `in` parameter cannot give.
        if (_m.Kind == FormatterKind.SealedClass || _m.Members.Any(x => x.Access != MemberAccess.Direct))
        {
            // A class's members read through the reference passed by value: through the `in` reference, the JIT
            // loads the object again for every member, as a store into the destination might change it.
            _w.Line(signature + " => __WriteFixed(value, ref destination);");
            _w.Line();
            _w.Line(_names.Inline);
            signature = $"private static void __WriteFixed({T} value, ref byte destination)";
        }

        _w.Open(signature);
        EmitFixedWrites(offsets);
        _w.Close();
        if (_m.Block)
        {
            _w.Line();
            EmitBlock(offsets);
        }
    }

    /// <summary>
    /// The <c>INinoBlockFormatter</c> of a struct of fixed size (<see cref="TypeModel.Block"/>): its blocks are the
    /// bytes of <c>WriteFixed</c> (<see cref="EmitFixed"/>); <c>ReadBlock</c> reads them as <c>ReadBody</c> does, with
    /// the same checks; and <c>BlockMap</c> says where each member lies in the struct's memory (a field by its
    /// reference, an auto-property where a marked value stored through it lands), which lets collections of it move
    /// several values at a time by vector shuffles.
    /// </summary>
    private void EmitBlock(string[] offsets)
    {
        string iface = $"{CoreCompilerServices}INinoBlockFormatter<{T}>";
        _w.Open($"{CoreCompilerServices}NinoBlockMap {iface}.BlockMap()");
        _w.Line($"var __v = default({T});");
        _w.Line($"return new {CoreCompilerServices}NinoBlockMap()");
        for (int i = 0; i < offsets.Length; i++)
        {
            var member = _m.Members[i];
            string offset = offsets[i];
            string memory = member.IsProperty
                ? $"{CoreCompilerServices}NinoBlocks.Find<{T}>({CoreCompilerServices}NinoUnsafe.SizeOf<{member.TypeName}>(), () => " +
                  $$"""{ var __m = default({{T}}); {{Set(_m, member, "__m", $"{CoreCompilerServices}NinoBlocks.Marked<{member.TypeName}>()")}} return __m; })"""
                : $"{CoreCompilerServices}NinoBlocks.Offset(ref __v, ref {Get(_m, member, "__v")})";
            string item = member.Raw != null
                ? $"Raw<{member.TypeName}>"
                : $"Block<{member.TypeName}, {Formatter(member)}>";
            _w.Line($"    .{item}({memory}, {offset}){(i == offsets.Length - 1 ? ";" : "")}");
        }
        _w.Close();
        _w.Line();
        _w.Line(_names.Inline);
        _w.Open($"void {iface}.ReadBlock(ref {T} value, ref byte source)");
        for (int i = 0; i < offsets.Length; i++)
        {
            var member = _m.Members[i];
            string offset = offsets[i];
            // As ReadBody: a bool through its own read, which rejects a byte other than 0 or 1.
            if (member.Raw != null)
            {
                _w.Line(Set(_m, member, "value", ReadRaw(member, "source", offset)));
                continue;
            }

            string read = $"{CoreCompilerServices}NinoBlocks.ReadBlock<{member.TypeName}, {Formatter(member)}>(ref ";
            string at = $", ref {CoreCompilerServices}NinoUnsafe.Add(ref source, {offset}));";
            if (Ref(_m, member, "value") is { } field)
            {
                _w.Line($"{read}{field}{at}");
                continue;
            }

            _w.Open();
            _w.Line($"var __t = default({member.TypeName});");
            _w.Line($"{read}__t{at}");
            _w.Line(Set(_m, member, "value", "__t"));
            _w.Close();
        }
        _w.Close();
    }
}
