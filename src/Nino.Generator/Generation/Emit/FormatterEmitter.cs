using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Nino.Generator.Common;
using static Nino.Generator.Generation.Emit.MemberCode;
using static Nino.Generator.Generation.TypeNames;

namespace Nino.Generator.Generation.Emit;

/// <summary>Emits the source file of one type's formatter.</summary>
internal static class FormatterEmitter
{
    public static string Emit(TypeModel m, bool fileScoped)
    {
        var names = new FormatterNames(m, fileScoped);
        var scratch = TextScratch.Rent(2048);
        var w = new CodeWriter(scratch);
        var aliases = new List<string>(4);
        EmitFormatter(w, m, names, aliases);
        AccessorEmitter.EmitGenericUnsafeAccessors(w, m, aliases);
        // The type's own name, which most lines of a formatter repeat, through an alias: not a generic type's, whose
        // name has the formatter's type parameters.
        names.AddInliningAliases(aliases);
        var body = GeneratedCode.WithAliases(w, m.IsGeneric ? null : m.TypeName, aliases, names.Formatters,
            names.RawTypes);
        TextScratch.Return(scratch);

        // The partial classes of the members' accessors follow in the types' own namespaces.
        string preamble = LayoutEmitter.Emit(m);
        if (m.Partial.Declarations.Length == 0)
            return GeneratedCode.File(preamble, aliases, body, fileScoped);

        var file = GeneratedCode.BuildBlockFile(preamble, aliases, body, tailCapacity: m.Pooled ? 3072 : 1024);
        var accessors = new CodeWriter(file);
        AccessorEmitter.EmitPartial(accessors, m);
        string source = accessors.Finish();
        TextScratch.Return(file);
        return source;
    }

    private static void EmitFormatter(CodeWriter w, TypeModel m, FormatterNames names, List<string> aliases)
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
                EmitEntryPoints(w, m, names, $"{CoreCompilerServices}NinoSubtypes<{t}>.SerializeReferenced(value, ref writer)",
                    $"{CoreCompilerServices}NinoSubtypes<{t}>.DeserializeReferenced(reader.UnsafeRead<int>(), ref reader, ref value)");
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
        EmitDeferral(w, m, names);
        w.Line();
        switch (m.Kind)
        {
            case FormatterKind.Struct:
                w.Line(names.Inline);
                w.Line($"{Signature.Serialize(t)} => WriteBody(value, ref writer);");
                w.Line();
                w.Line(names.Inline);
                w.Line($"{Signature.Deserialize(t)} => ReadBody(ref reader, ref value);");
                break;
            case FormatterKind.SealedClass when m.TrackReferences:
                // The reference header already says the object is not null.
                EmitEntryPoints(w, m, names, $"{CoreCompilerServices}NinoObject.WriteBody<{t}, {self}>(value, ref writer)",
                    $"{CoreCompilerServices}NinoObject.ReadBody<{t}, {self}>(ref reader, ref value)");
                break;
            case FormatterKind.SealedClass when m.BoundedEntry:
                // The twin's bodies when the depth leaves room for the whole nesting bound (see TypeFacts.DepthBound).
                EmitEntryPoints(w, m, names,
                    $"{CoreCompilerServices}NinoObject.SerializeSealed<{t}, {self}, {ShallowTwinName}>(value, ref writer, {m.Depth.Bound})",
                    $"{CoreCompilerServices}NinoObject.DeserializeSealed<{t}, {self}, {ShallowTwinName}>(ref reader, ref value, {m.Depth.Bound})");
                break;
            case FormatterKind.SealedClass:
                EmitEntryPoints(w, m, names,
                    $"{CoreCompilerServices}NinoObject.{(m.RawBody ? "SerializeSealedRaw" : "SerializeSealed")}<{t}, {self}>(value, ref writer)",
                    $"{CoreCompilerServices}NinoObject.DeserializeSealed<{t}, {self}>(ref reader, ref value)");
                break;
            case FormatterKind.Class when m.TrackReferences:
                EmitEntryPoints(w, m, names, $"{CoreCompilerServices}NinoObject.SerializeReferenced<{t}, {self}>(value, ref writer)",
                    $"{CoreCompilerServices}NinoObject.DeserializeReferenced<{t}, {self}>(ref reader, ref value)");
                break;
            case FormatterKind.Class when m.DispatchesSubtypes:
                EmitDispatchingEntryPoints(w, m, names, ownId: true);
                break;
            default:
                EmitEntryPoints(w, m, names,
                    $"{CoreCompilerServices}NinoObject.{(m.RawBody ? "SerializeRaw" : "Serialize")}<{t}, {self}>(value, ref writer)",
                    $"{CoreCompilerServices}NinoObject.Deserialize<{t}, {self}>(ref reader, ref value)");
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

        if (m.HasTop)
        {
            w.Line();
            EmitTop(w, m, names);
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
            AccessorEmitter.EmitUnsafeAccessors(w, m, aliases);
        AccessorEmitter.EmitInterfaceAccessors(w, m);

        w.Close();
    }

    /// <summary>The interface of a formatter whose collections prefetch (see <see cref="TypeModel.PrefetchBytes"/>).</summary>
    private static string Prefetch(TypeModel m) =>
        m.PrefetchBytes == 0 ? "" : m.ObjectOnlyPrefetch
            ? $", {CoreCompilerServices}INinoObjectPrefetchFormatter"
            : $", {CoreCompilerServices}INinoPrefetchFormatter";

    /// <summary>
    /// INinoPrefetchFormatter of a formatter: the bytes an element brings into the cache, and the prefetch of the
    /// objects one element's members point to, which Nino.Core's helper for long collections calls for each element of
    /// a run (inlined there). A twin (<paramref name="twin"/>) calls its formatter's.
    /// </summary>
    private static void EmitPrefetch(CodeWriter w, TypeModel m, FormatterNames names, bool twin = false)
    {
        if (m.PrefetchBytes == 0)
            return;
        const string signature = "public int PrefetchMembers(object value)";
        w.Line();
        w.Line($"public int PrefetchBytes => {m.PrefetchBytes};");
        if (m.ObjectOnlyPrefetch)
            return;
        w.Line(names.Inline);
        if (m.PrefetchMembers.Length == 0)
            w.Line($"{signature} => 0;");
        else if (twin)
            w.Line($"{signature} => default({m.Self}).PrefetchMembers(value);");
        else
        {
            // The element as its class, without a cast: the loop passes only elements of this formatter's collections.
            string element = $"global::System.Runtime.CompilerServices.Unsafe.As<{m.TypeName}>(value)";
            const string at = CoreCompilerServices + "NinoPrefetched.At";
            if (m.PrefetchMembers.Length == 1)
            {
                w.Line($"{signature} => {at}({element}.{m.PrefetchMembers[0]});");
                return;
            }

            w.Open(signature);
            w.Line($"var v = {element};");
            w.Line("return " + string.Join(" + ", m.PrefetchMembers.Select(name => $"{at}(v.{name})")) + ";");
            w.Close();
        }
    }

    /// <summary>
    /// The formatter the intercepted top-level calls use (see <see cref="TypeModel.HasTop"/>): the same bytes, with
    /// no depth check, and the body inlined into the caller (<see cref="TypeModel.TopInline"/>) or called directly.
    /// </summary>
    private static void EmitTop(CodeWriter w, TypeModel m, FormatterNames names)
    {
        string t = m.TypeName;
        string top = TopTwinName;
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
            BodyEmitter.BeginLocalCopy(w, reader: false);
            EmitTopWrite(w);
            BodyEmitter.EndLocalCopy(w, reader: false);
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

        OpenTwin(w, m, names, top, $"{CoreCompilerServices}INinoBodyFormatter<{t}>, {CoreCompilerServices}INinoTopFormatter<{t}>{Prefetch(m)}");
        w.Line(names.Inline);
        w.Line(serializeTop
            ? $"{Signature.Serialize(t)} => __SerializeTop(value, ref writer);"
            : $"{Signature.Serialize(t)} => {SerializeTop(m, top)};");
        w.Line(names.Inline);
        // A null value is its presence byte alone, written inline: the writer is created out of line (__WriteTop).
        w.Line(serializeTop
            ? $"public void Write({t} value, {Core}NinoArrayBufferWriter buffer) {{ if (value == null) {CoreCompilerServices}NinoDirect.WriteNull(buffer); else __WriteTop(value, buffer); }}"
            : $"public void Write({t} value, {Core}NinoArrayBufferWriter buffer) => {CoreCompilerServices}NinoDirect.Write<{t}, {top}>(value, buffer);");
        w.Line(names.Inline);
        w.Line($"{Signature.Deserialize(t)} => {CoreCompilerServices}NinoObject.DeserializeTop<{t}, {top}>(ref reader, ref value);");
        if (m.ReadTop)
        {
            // A null value (its presence byte 0) is returned inline; the reader is created in a static method out of
            // line (an instance method out of line takes the twin's address: a byte stored on the caller's stack).
            w.Line(names.Inline);
            w.Line($"public {t} Read(global::System.ReadOnlySpan<byte> data) => !data.IsEmpty && data[0] == {NinoWire.Null} ? null : __ReadTop(data);");
            w.Line(names.NoInline);
            w.Open($"private static {t} __ReadTop(global::System.ReadOnlySpan<byte> data)");
            w.Line($"var __r = new {Core}Reader(data);");
            w.Line("if (!__r.ReadPresent())");
            w.IndentedLine("return null;");
            w.Line($"{t} value = null;");
            w.Line($"{(m.SplitFresh ? "__ReadBodyFreshInline" : "__ReadBodyInline")}(ref __r, ref value);");
            w.Line("__r.ReleaseReferences();");
            w.Line("return value;");
            w.Close();
        }
        else
        {
            w.Line(names.Inline);
            w.Line($"public {t} Read(global::System.ReadOnlySpan<byte> data) => {CoreCompilerServices}NinoDirect.Deserialize<{t}, {top}>(data);");
        }
        ForwardWriteBody(w, m, names, m.TopInline ? "__WriteBodyInline" : null);
        ForwardReadBody(w, m, names, m.TopInline ? "__ReadBodyInline" : null);
        EmitPrefetch(w, m, names, twin: true);
        w.Close();
    }

    /// <summary>
    /// Opens the twin struct <paramref name="name"/> of the formatter: its type id, traits and empty instance are the
    /// formatter's.
    /// </summary>
    private static void OpenTwin(CodeWriter w, TypeModel m, FormatterNames names, string name, string interfaces)
    {
        w.Open($"internal struct {name} : {interfaces}");
        EmitTypeId(w, m);
        EmitDeferral(w, m, names, forwardCreate: true);
    }

    /// <summary>A twin's <c>Serialize</c>: the presence byte, then the body with no depth check.</summary>
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
        w.Line($"{Signature.ReadBody(m.TypeName)} => {body ?? $"default({m.Self}).ReadBody"}(ref reader, ref value);");
    }

    /// <summary>The presence byte and the body of <c>value</c>, on the local writer <c>__w</c>.</summary>
    private static void EmitTopWrite(CodeWriter w)
    {
        w.Line($"byte __present = (byte)(value == null ? {NinoWire.Null} : {NinoWire.NotNull});");
        w.Line("__w.UnsafeWrite(__present);");
        w.Line($"if (__present != {NinoWire.Null})");
        w.IndentedLine("__WriteBodyInline(value, ref __w);");
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
        // The twin by its own name: the code naming it is in the formatter, where its nested types are in scope.
        string twin = ShallowTwinName;
        OpenTwin(w, m, names, twin, $"{CoreCompilerServices}INinoBodyFormatter<{t}>{Prefetch(m)}");
        w.Line(names.Inline);
        w.Line(m.Kind == FormatterKind.Struct
            ? $"{Signature.Serialize(t)} => WriteBody(value, ref writer);"
            : $"{Signature.Serialize(t)} => {SerializeTop(m, twin)};");
        w.Line(names.Inline);
        w.Line(m.Kind == FormatterKind.Struct
            ? $"{Signature.Deserialize(t)} => ReadBody(ref reader, ref value);"
            : $"{Signature.Deserialize(t)} => {CoreCompilerServices}NinoObject.DeserializeTop<{t}, {twin}>(ref reader, ref value);");

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

        EmitPrefetch(w, m, names, twin: true);
        w.Close();
    }

    private static void EmitTypeId(CodeWriter w, TypeModel m)
    {
        if (m.IsGeneric)
        {
            w.Line($"private static readonly int s_typeId = {Core}NinoTypeId.Of(typeof({m.TypeName}));");
            w.Line("public int TypeId => s_typeId;");
            return;
        }

        w.Line($"public int TypeId => {m.TypeId.ToString(CultureInfo.InvariantCulture)};");
    }

    /// <summary>Whether a body nested too deep can be read later, and the empty instance it is read into.</summary>
    private static void EmitDeferral(CodeWriter w, TypeModel m, FormatterNames names, bool forwardCreate = false)
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

        string create = forwardCreate ? $"default({m.Self}).Create()" : BodyEmitter.Create(m, names.CompactSyntax);
        w.Line($"public {t} Create() => {create};");
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
        w.Open(Signature.Serialize(t));
        w.Line("if (writer.WriteReference(value))");
        w.IndentedLine($"{write};");
        w.Close();
        w.Line();
        w.Line(names.Inline);
        w.Open(Signature.Deserialize(t));
        w.Line("if (!reader.ReadReference(ref value, out int __index))");
        w.IndentedLine("return;");
        w.Line($"{read};");
        w.Line("reader.EndReference(__index, value);");
        w.Close();
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

        string write = !m.DispatchesSubtypes ? $"{CoreCompilerServices}NinoSubtypes<{t}>.Serialize(value, ref writer);"
            : root ? "__WriteSubtype(value, ref writer);"
            : $"{rootFormatter}.__WriteSubtype(value, ref writer);";
        string[] read = !m.DispatchesSubtypes ? new[] { $"{CoreCompilerServices}NinoSubtypes<{t}>.DeserializeOther(id, ref reader, ref value);" }
            : root ? new[] { "__ReadSubtype(id, ref reader, ref value);" }
            : new[]
            {
                $"{rootFormatter}.__ReadSubtype(id, ref reader, ref {CoreCompilerServices}NinoObject.AsBaseSlot<{t}, {rootType}>(ref value));",
                $"{CoreCompilerServices}NinoObject.EnsureSubtype<{t}>(value);",
            };
        w.Line(names.Inline);
        w.Open(Signature.Serialize(t));
        if (ownId)
        {
            w.Line($"if (!{CoreCompilerServices}NinoObject.{(m.RawBody ? "TryWriteOwnRaw" : "TryWriteOwn")}<{t}, {self}>(value, ref writer))");
            w.IndentedLine(write);
        }
        else
        {
            w.Line("if (value == null)");
            w.IndentedLine($"writer.UnsafeWrite({NinoWire.NullTypeId});");
            w.Line("else");
            w.IndentedLine(write);
        }
        w.Close();
        w.Line();
        w.Line(names.Inline);
        w.Open(Signature.Deserialize(t));
        w.Line("int id = reader.UnsafeRead<int>();");
        if (!ownId)
        {
            foreach (var line in read)
                w.Line(line);
        }
        else
        {
            w.Line($"if (!{CoreCompilerServices}NinoObject.TryReadOwn<{t}, {self}>(id, ref reader, ref value))");
            if (read.Length == 1)
                w.IndentedLine(read[0]);
            else
            {
                w.Open();
                foreach (var line in read)
                    w.Line(line);
                w.Close();
            }
        }
        w.Close();
    }
}
