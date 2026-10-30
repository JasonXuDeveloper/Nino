using System.Collections.Generic;
using System.Linq;
using Nino.Generator.Common;
using static Nino.Generator.Generation.Emit.MemberCode;
using static Nino.Generator.Generation.TypeNames;

namespace Nino.Generator.Generation.Emit;

/// <summary>Emits the source file of one type's formatter.</summary>
internal static class FormatterEmitter
{
    public static string Emit(TypeModel m)
    {
        var names = new FormatterNames(m);
        var w = new CodeWriter(4096, indent: 1);
        EmitFormatter(w, m, names);
        AccessorEmitter.EmitGenericUnsafeAccessors(w, m);
        // The type's own name, which most lines of a formatter repeat, through an alias: not a generic type's, whose
        // name has the formatter's type parameters.
        var aliases = new List<string>();
        string body = GeneratedCode.WithAliases(w.ToString(), m.IsGeneric ? null : m.TypeName, aliases);

        string file = GeneratedCode.File(LayoutEmitter.Emit(m) + "\n", names.Declarations(aliases), body);
        return m.Partial.Declarations.Length > 0 ? file + AccessorEmitter.EmitPartial(m) : file;
    }

    private static void EmitFormatter(CodeWriter w, TypeModel m, FormatterNames names)
    {
        string t = m.TypeName;
        string self = m.Self;
        string iface = m.Kind == FormatterKind.Polymorphic
            ? $"{Core}INinoFormatter<{t}>"
            : $"{CoreCompilerServices}INinoBodyFormatter<{t}>";
        if (m.RawStruct)
            iface += $", {CoreCompilerServices}INinoRawFormatter";
        if (m.FixedSize)
            iface += $", {CoreCompilerServices}INinoFixedSizeFormatter<{t}>";
        if (m.RawBody)
            iface += $", {CoreCompilerServices}INinoRawBodyFormatter<{t}>";
        if (m.Block)
            iface += $", {CoreCompilerServices}INinoBlockFormatter<{t}>";
        iface += Prefetch(m);

        w.Open($"internal {(m.IsDispatchRoot ? "partial " : "")}struct {self} : {iface}{m.FormatterConstraints}");
        var body = new BodyEmitter(m, w, names);

        // TypeId, Traits and Create are a few bytes of IL, which every runtime inlines without being told to. The
        // entry points and body forwarders are marked AggressiveInlining all the same: the JIT counts what it inlines
        // on its own judgement, and everything that inlines into that in turn, against the caller's inlining budget
        // (only a chain of AggressiveInlining methods from the caller raises the budget), and a caller that spent it
        // leaves even the small Nino.Core helpers its members' reads and writes inline as calls.
        if (m.Kind == FormatterKind.Polymorphic)
        {
            // Behind a reference header, subtypes are dispatched without one of their own.
            if (m.TrackReferences)
                EmitEntryPoints(w, m, names, $"{CoreCompilerServices}NinoPolymorphic<{t}>.SerializeReferenced(value, ref writer)",
                    $"{CoreCompilerServices}NinoPolymorphic<{t}>.DeserializeReferenced(reader.UnsafeRead<int>(), ref value, ref reader)");
            else
                EmitDispatchingEntryPoints(w, m, names, ownId: false);
            w.Line();
            if (m.Inheritance.EmitMembers)
            {
                body.EmitMembersMethods();
                w.Line();
            }

            EmitPrefetch(w, m, names);
            w.Close();
            return;
        }

        EmitTypeId(w, m);
        w.Line();
        EmitDeferral(w, m);
        w.Line();
        switch (m.Kind)
        {
            case FormatterKind.Struct:
                w.Line(names.Inline);
                w.Line($"{Signature.Serialize(t)} => WriteBody(value, ref writer);");
                w.Line();
                w.Line(names.Inline);
                w.Line($"{Signature.Deserialize(t)} => ReadBody(ref value, ref reader);");
                break;
            case FormatterKind.SealedClass when m.TrackReferences:
                // The reference header already says the object is not null.
                EmitEntryPoints(w, m, names, $"{CoreCompilerServices}NinoObject.WriteBody<{t}, {self}>(value, ref writer)",
                    $"{CoreCompilerServices}NinoObject.ReadBody<{t}, {self}>(ref value, ref reader)");
                break;
            case FormatterKind.SealedClass when m.BoundedEntry:
                // The twin's bodies when the depth leaves room for the whole nesting bound (see TypeFacts.DepthBound).
                EmitEntryPoints(w, m, names,
                    $"{CoreCompilerServices}NinoObject.SerializeSealed<{t}, {self}, {self}{Shallow}>(value, ref writer, {m.Depth.Bound})",
                    $"{CoreCompilerServices}NinoObject.DeserializeSealed<{t}, {self}, {self}{Shallow}>(ref value, ref reader, {m.Depth.Bound})");
                break;
            case FormatterKind.SealedClass:
                EmitEntryPoints(w, m, names, $"{CoreCompilerServices}NinoObject.SerializeSealed{Raw(m)}<{t}, {self}>(value, ref writer)",
                    $"{CoreCompilerServices}NinoObject.DeserializeSealed<{t}, {self}>(ref value, ref reader)");
                break;
            case FormatterKind.Class when m.TrackReferences:
                EmitEntryPoints(w, m, names, $"{CoreCompilerServices}NinoObject.SerializeReferenced<{t}, {self}>(value, ref writer)",
                    $"{CoreCompilerServices}NinoObject.DeserializeReferenced<{t}, {self}>(ref value, ref reader)");
                break;
            case FormatterKind.Class when m.DispatchesSubtypes:
                EmitDispatchingEntryPoints(w, m, names, ownId: true);
                break;
            default:
                EmitEntryPoints(w, m, names, $"{CoreCompilerServices}NinoObject.Serialize{Raw(m)}<{t}, {self}>(value, ref writer)",
                    $"{CoreCompilerServices}NinoObject.Deserialize<{t}, {self}>(ref value, ref reader)");
                break;
        }

        w.Line();
        if (m.RawStruct)
        {
            body.EmitRawStruct();
        }
        else
        {
            body.EmitWriteBody();
            w.Line();
            body.EmitReadBody();
        }

        if (m.FixedSize)
        {
            w.Line();
            body.EmitFixed();
        }

        if (m.Block)
        {
            w.Line();
            body.EmitBlock();
        }

        if (m.HasTop)
        {
            w.Line();
            EmitTop(w, m, names, body);
        }

        if (m.Depth.Shallow)
        {
            if (m.ShallowBodies)
            {
                w.Line();
                body.EmitShallowBodies();
            }

            w.Line();
            EmitShallow(w, m, names, body);
        }

        EmitPrefetch(w, m, names);
        if (!m.IsGeneric)
            AccessorEmitter.EmitUnsafeAccessors(w, m);

        w.Close();
    }

    /// <summary>The interface of a formatter whose collections prefetch (see <see cref="TypeModel.PrefetchBytes"/>).</summary>
    private static string Prefetch(TypeModel m) =>
        m.PrefetchBytes > 0 ? $", {CoreCompilerServices}INinoPrefetchFormatter" : "";

    /// <summary>
    /// INinoPrefetchFormatter of a formatter or twin: the bytes an element brings into the
    /// cache, and the prefetch of the objects its members point to, which Nino.Core's helper for long collections
    /// calls once per run of elements (inlined there).
    /// </summary>
    private static void EmitPrefetch(CodeWriter w, TypeModel m, FormatterNames names)
    {
        if (m.PrefetchBytes == 0)
            return;
        string t = m.TypeName;
        string prefetch = $"{CoreCompilerServices}INinoPrefetchFormatter";
        w.Line();
        w.Line($"int {prefetch}.PrefetchBytes => {m.PrefetchBytes};");
        w.Line(names.Inline);
        if (m.PrefetchMembers.Length == 0)
        {
            w.Line($"int {prefetch}.PrefetchMembers(ref object first, int count) => 0;");
            return;
        }

        w.Line($"int {prefetch}.PrefetchMembers(ref object first, int count)");
        w.Open();
        w.Line("int sum = 0;");
        w.Line($"ref var item = ref global::System.Runtime.CompilerServices.Unsafe.As<object, {t}>(ref first);");
        w.Line("for (; count > 0; count--, item = ref global::System.Runtime.CompilerServices.Unsafe.Add(ref item, 1))");
        w.Open();
        w.Line("var v = item;");
        w.Line("if (v != null)");
        w.Indented(() => w.Line("sum += " + string.Join(" + ",
            m.PrefetchMembers.Select(name => $"{CoreCompilerServices}NinoPrefetched.At(v.{name})")) + ";"));
        w.Close();
        w.Line("return sum;");
        w.Close();
    }

    /// <summary>
    /// The formatter the intercepted top-level calls use (see <see cref="TypeModel.HasTop"/>): the same bytes, with
    /// no depth check, and the body inlined into the caller (<see cref="TypeModel.TopInline"/>) or called directly.
    /// </summary>
    private static void EmitTop(CodeWriter w, TypeModel m, FormatterNames names, BodyEmitter body)
    {
        string t = m.TypeName;
        string top = m.Self + Top;
        // An out-of-line body on a local writer (TypeModel.LocalWriter, not inlined at the top): the presence
        // byte is written there too, on the local, rather than by the caller on a writer whose address the call
        // takes (each write a load and store of its cursor in memory). The top-level calls that create their writer
        // have it created in a method of its own (__WriteTop, see INinoTopFormatter), where the JIT keeps it in
        // registers and knows it starts empty (no region open, no context) instead of loading it from the caller's
        // memory and storing it back.
        bool serializeTop = !m.TopInline && m.LocalWriter;
        if (serializeTop)
        {
            w.Line(names.NoInline);
            w.Open($"private static void __SerializeTop({Signature.WriteParameters(t)})");
            BodyEmitter.OnLocalCopy(w, reader: false, () => EmitTopWrite(w));
            w.Close();
            w.Line();
            w.Line(names.NoInline);
            w.Open($"private static void __WriteTop({t} value, {Core}NinoArrayBufferWriter buffer)");
            w.Line($"var __w = new {Core}Writer(buffer);");
            EmitTopWrite(w);
            w.Line("__w.Flush();");
            w.Line("__w.ReleaseReferences();");
            w.Close();
            w.Line();
        }

        // The same for the top-level reads of a new value (TypeModel.ReadTop): the reader is created in __ReadTop.
        if (m.ReadTop)
        {
            w.Line(names.NoInline);
            w.Open($"private static {t} __ReadTop(global::System.ReadOnlySpan<byte> data)");
            w.Line($"var __r = new {Core}Reader(data);");
            w.Line($"{t} value = null;");
            w.Line("if (__r.ReadPresent())");
            w.Indented(() => w.Line($"{(m.SplitFresh ? "__ReadBodyFreshInline" : "__ReadBodyInline")}(ref value, ref __r);"));
            w.Line("__r.ReleaseReferences();");
            w.Line("return value;");
            w.Close();
            w.Line();
        }

        OpenTwin(w, m, TopTwinName, $"{CoreCompilerServices}INinoBodyFormatter<{t}>, {CoreCompilerServices}INinoTopFormatter<{t}>{Prefetch(m)}");
        w.Line(names.Inline);
        w.Line(serializeTop
            ? $"{Signature.Serialize(t)} => __SerializeTop(value, ref writer);"
            : $"{Signature.Serialize(t)} => {SerializeTop(m, top)};");
        w.Line(names.Inline);
        w.Line(serializeTop
            ? $"public void Write({t} value, {Core}NinoArrayBufferWriter buffer) => __WriteTop(value, buffer);"
            : $"public void Write({t} value, {Core}NinoArrayBufferWriter buffer) => {CoreCompilerServices}NinoDirect.Write<{t}, {top}>(value, buffer);");
        w.Line(names.Inline);
        w.Line($"{Signature.Deserialize(t)} => {CoreCompilerServices}NinoObject.DeserializeTop<{t}, {top}>(ref value, ref reader);");
        w.Line(names.Inline);
        w.Line(m.ReadTop
            ? $"public {t} Read(global::System.ReadOnlySpan<byte> data) => __ReadTop(data);"
            : $"public {t} Read(global::System.ReadOnlySpan<byte> data) => {CoreCompilerServices}NinoDirect.Deserialize<{t}, {top}>(data);");
        ForwardWriteBody(w, m, names, m.TopInline ? "__WriteBodyInline" : null);
        ForwardReadBody(w, m, names, m.TopInline ? "__ReadBodyInline" : null);
        EmitPrefetch(w, m, names);
        w.Close();
    }

    /// <summary>
    /// Opens the twin struct <paramref name="name"/> of the formatter: its type id, traits and empty instance are the
    /// formatter's.
    /// </summary>
    private static void OpenTwin(CodeWriter w, TypeModel m, string name, string interfaces)
    {
        w.Open($"internal struct {name} : {interfaces}");
        w.Line("public int TypeId => Id;");
        w.Line($"public int Traits => default({m.Self}).Traits;");
        w.Line($"public {m.TypeName} Create() => default({m.Self}).Create();");
    }

    /// <summary>A twin's <c>Serialize</c>: the null flag, then the body with no depth check.</summary>
    private static string SerializeTop(TypeModel m, string twin) => m.RawBody
        ? $"{CoreCompilerServices}NinoObject.SerializeTopRaw<{m.TypeName}, {m.Self}>(value, ref writer)"
        : $"{CoreCompilerServices}NinoObject.SerializeTop<{m.TypeName}, {twin}>(value, ref writer)";

    /// <summary>
    /// A twin's <c>WriteBody</c>: a call to the static body <paramref name="body"/>, or (null) to the formatter's
    /// <c>WriteBody</c>.
    /// </summary>
    private static void ForwardWriteBody(CodeWriter w, TypeModel m, FormatterNames names, string? body)
    {
        w.Line(names.Inline);
        w.Line(body == null
            ? $"{Signature.WriteBody(m.TypeName)} => default({m.Self}).WriteBody(value, ref writer);"
            : $"{Signature.WriteBody(m.TypeName)} => {body}({BodyEmitter.ValueArgument(m)}, ref writer);");
    }

    /// <summary>A twin's <c>ReadBody</c>: a call to <paramref name="body"/>, or (null) to the formatter's.</summary>
    private static void ForwardReadBody(CodeWriter w, TypeModel m, FormatterNames names, string? body)
    {
        w.Line(names.Inline);
        w.Line($"{Signature.ReadBody(m.TypeName)} => {body ?? $"default({m.Self}).ReadBody"}(ref value, ref reader);");
    }

    /// <summary>The null flag and the body of <c>value</c>, on the local writer <c>__w</c>.</summary>
    private static void EmitTopWrite(CodeWriter w)
    {
        w.Line("if (value == null)");
        w.Indented(() => w.Line($"__w.UnsafeWrite((byte){NinoWire.Null});"));
        w.Open("else");
        w.Line($"__w.UnsafeWrite((byte){NinoWire.NotNull});");
        w.Line("__WriteBodyInline(value, ref __w);");
        w.Close();
    }

    /// <summary>
    /// The twin for where the nesting bound is known to stay below <c>NinoDepth.Inline</c> (see
    /// <see cref="DepthModel.Shallow"/>): the same bytes, with no depth counted or checked, here or in the members
    /// (their formatters' twins). Used by the bodies of other twins, by <see cref="TypeModel.BoundedEntry"/> and by the
    /// intercepted top-level calls.
    /// </summary>
    private static void EmitShallow(CodeWriter w, TypeModel m, FormatterNames names, BodyEmitter body)
    {
        string t = m.TypeName;
        string self = m.Self;
        string twin = self + Shallow;
        OpenTwin(w, m, ShallowTwinName, $"{CoreCompilerServices}INinoBodyFormatter<{t}>{Prefetch(m)}");
        w.Line(names.Inline);
        w.Line(m.Kind == FormatterKind.Struct
            ? $"{Signature.Serialize(t)} => WriteBody(value, ref writer);"
            : $"{Signature.Serialize(t)} => {SerializeTop(m, twin)};");
        w.Line(names.Inline);
        w.Line(m.Kind == FormatterKind.Struct
            ? $"{Signature.Deserialize(t)} => ReadBody(ref value, ref reader);"
            : $"{Signature.Deserialize(t)} => {CoreCompilerServices}NinoObject.DeserializeTop<{t}, {twin}>(ref value, ref reader);");

        ForwardWriteBody(w, m, names, m.ShallowBodies ? "__WriteBodyShallow" : null);
        if (m.ShallowBodies && m.SplitFresh)
        {
            w.Line(names.Inline);
            w.Open(Signature.ReadBody(t));
            body.EmitFreshForwarder("__ReadBodyShallow");
            w.Close();
        }
        else
        {
            ForwardReadBody(w, m, names, m.ShallowBodies ? "__ReadBodyShallow" : null);
        }

        EmitPrefetch(w, m, names);
        w.Close();
    }

    /// <summary>The suffix of the <c>NinoObject</c> methods for a body that is one raw block (<see cref="TypeModel.RawBody"/>).</summary>
    private static string Raw(TypeModel m) => m.RawBody ? "Raw" : "";

    private static void EmitTypeId(CodeWriter w, TypeModel m)
    {
        if (m.IsGeneric)
        {
            w.Line($"private static readonly int s_typeId = {Core}NinoTypeId.Of(typeof({m.TypeName}));");
            w.Line("public int TypeId => s_typeId;");
            return;
        }

        w.Line($"public const int Id = {m.TypeId};");
        w.Line("public int TypeId => Id;");
    }

    /// <summary>Whether a body nested too deep can be read later, and the empty instance it is read into.</summary>
    private static void EmitDeferral(CodeWriter w, TypeModel m)
    {
        string t = m.TypeName;
        // NinoBodyTraits.Nests and NinoBodyTraits.Deferrable.
        int traits = (m.Depth.Nests ? 1 : 0) | (m.Depth.Deferrable ? 2 : 0);
        w.Line($"public int Traits => {traits};");
        if (!m.Depth.Deferrable)
        {
            w.Line($"public {t} Create() => default;");
            return;
        }

        w.Line($"public {t} Create() => {BodyEmitter.Create(m)};");
    }

    /// <summary>Serialize/Deserialize forwarding to the given calls, behind a reference header when tracked.</summary>
    private static void EmitEntryPoints(CodeWriter w, TypeModel m, FormatterNames names, string write, string read)
    {
        string t = m.TypeName;
        if (!m.TrackReferences)
        {
            w.Line(names.Inline);
            w.Line($"{Signature.Serialize(t)} => {write};");
            w.Line();
            w.Line(names.Inline);
            w.Line($"{Signature.Deserialize(t)} => {read};");
            return;
        }

        w.Line(names.Inline);
        w.Block($"{Signature.Serialize(t)}", () =>
        {
            w.Line("if (writer.WriteReference(value))");
            w.Indented(() => w.Line($"{write};"));
        });
        w.Line();
        w.Line(names.Inline);
        w.Block($"{Signature.Deserialize(t)}", () =>
        {
            w.Line("if (!reader.ReadReference(ref value, out int __index))");
            w.Indented(() => w.Line("return;"));
            w.Line($"{read};");
            w.Line("reader.EndReference(__index, value);");
        });
    }

    /// <summary>
    /// Type id (0 = null) and body. A class's own type is checked first; subtypes go through the generated dispatch
    /// (<see cref="DispatchEmitter"/>).
    /// </summary>
    private static void EmitDispatchingEntryPoints(CodeWriter w, TypeModel m, FormatterNames names, bool ownId)
    {
        string t = m.TypeName;
        string self = m.Self;
        // The root's dispatch covers every subtype of this class. Writing needs nothing more (its checks are exact);
        // reading checks that the type the data named is one of this class. A generic type is its own root: its
        // dispatch selects one of its closed instances (DispatchEmitter).
        bool root = m.IsDispatchRoot;
        string rootFormatter = "", rootType = "";
        if (m.DispatchesSubtypes && !root)
        {
            rootFormatter = m.Need(m.Inheritance.DispatchRootFormatter, nameof(m.Inheritance.DispatchRootFormatter));
            rootType = m.Need(m.Inheritance.DispatchRoot, nameof(m.Inheritance.DispatchRoot));
        }

        string write = !m.DispatchesSubtypes ? $"{CoreCompilerServices}NinoPolymorphic<{t}>.Serialize(value, ref writer)"
            : root ? "__WriteSubtype(value, ref writer)"
            : $"{rootFormatter}.__WriteSubtype(value, ref writer)";
        string read = !m.DispatchesSubtypes ? $"{CoreCompilerServices}NinoPolymorphic<{t}>.DeserializeOther(id, ref value, ref reader);"
            : root ? "__ReadSubtype(id, ref value, ref reader);"
            : $"{rootFormatter}.__ReadSubtype(id, ref {CoreCompilerServices}NinoObject.AsBaseSlot<{t}, {rootType}>(ref value), ref reader); {CoreCompilerServices}NinoObject.EnsureSubtype<{t}>(value);";
        w.Line(names.Inline);
        w.Line($"{Signature.Serialize(t)}");
        w.Indented(() => w.Line(ownId
            ? $$"""{ if (!{{CoreCompilerServices}}NinoObject.TryWriteOwn{{Raw(m)}}<{{t}}, {{self}}>(value, ref writer)) {{write}}; }"""
            : $$"""{ if (value == null) writer.UnsafeWrite({{NinoWire.NullTypeId}}); else {{write}}; }"""));
        w.Line();
        w.Line(names.Inline);
        w.Line($"{Signature.Deserialize(t)}");
        w.Indented(() => w.Line(ownId
            ? $$"""{ int id = reader.UnsafeRead<int>(); if (!{{CoreCompilerServices}}NinoObject.TryReadOwn<{{t}}, {{self}}>(id, ref value, ref reader)) { {{read}} } }"""
            : $$"""{ int id = reader.UnsafeRead<int>(); {{read}} }"""));
    }
}
