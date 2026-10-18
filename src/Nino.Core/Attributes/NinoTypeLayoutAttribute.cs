using System;

namespace Nino.Core
{
    /// <summary>What a <c>[NinoType]</c> writes in front of its body.</summary>
    public enum NinoHeader
    {
        /// <summary>Nothing: a struct.</summary>
        None = 0,

        /// <summary>One byte telling null from a value: a sealed class.</summary>
        NullFlag = 1,

        /// <summary>The 4-byte type id of the runtime type (0 for null): a class that is not sealed, an interface.</summary>
        TypeId = 2,
    }

    /// <summary>
    /// The wire layout of a <c>[NinoType]</c>, as its generated formatter writes it. The generator adds one to the
    /// assembly for every <c>[NinoType]</c> it generates a formatter for, so tools (schema checks, diffs, migrations)
    /// can read what Nino writes for a type from metadata, also for types of referenced assemblies, instead of
    /// re-implementing Nino's rules. Read it with <c>assembly.GetCustomAttributes&lt;NinoTypeLayoutAttribute&gt;()</c>,
    /// or from metadata without loading the assembly.
    /// <para>
    /// Two types get the same layout exactly when the generator writes them the same way, and the layout is the
    /// same in every target framework. Compare <see cref="ToString"/> textually; the format of
    /// <see cref="Members"/> is described there.
    /// </para>
    /// <see href="https://nino.xgamedev.net/en/docs/v4/evolving-data#layout-metadata"/>
    /// </summary>
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    public sealed class NinoTypeLayoutAttribute : Attribute
    {
        /// <summary>The type (an open generic type definition for a generic type).</summary>
        public Type Type { get; }

        /// <summary>The format of the body.</summary>
        public NinoFormat Format { get; }

        /// <summary>What is written in front of the body.</summary>
        public NinoHeader Header { get; }

        /// <summary>
        /// Whether values are written with a reference header (null, back reference or new object) after
        /// <see cref="Header"/>: <c>[NinoType(trackReferences: true)]</c>.
        /// </summary>
        public bool TrackReferences { get; }

        /// <summary>Whether the type is an unmanaged struct written as its raw memory (in the canonical layout).</summary>
        public bool Raw { get; }

        /// <summary>The type id written in front of the body, or 0 for a generic type (computed at run time from its arguments).</summary>
        public int Id { get; }

        /// <summary>
        /// The body, one entry per member in the order they are written, separated by <c>;</c>. An entry is
        /// <c>name:type:encoding</c>: the member's C# name, its type (fully qualified, without <c>global::</c>),
        /// and the bytes it is written as (below). In a <see cref="NinoFormat.Keyed"/> body it ends with
        /// <c>:key=0x...</c>, the member's key, and <c>:former=0x...,0x...</c> for the keys of its former names, which
        /// are read too. A <c>[NinoType]</c> collection starts with <c>[elements]::encoding</c>, its elements
        /// written before the members. A <see cref="Raw"/> type is one value copied as raw memory, so its body is
        /// just its raw encoding.
        /// <para>Encodings describe bytes, not the formatters that write them:</para>
        /// <list type="bullet">
        /// <item><c>raw(n)</c>: a primitive, enum, <c>decimal</c>, <c>Guid</c>, <c>DateTime</c>, <c>TimeSpan</c> or
        /// pointer-sized integer of <c>n</c> bytes; <c>raw(n){name:type@offset:encoding,...}</c>: a struct of
        /// <c>n</c> bytes in the canonical layout, each field at its offset and encoded the same way
        /// (<c>raw(8){Value:long@0:raw(8)}</c>). Fields, <c>[StructLayout]</c> <c>Pack</c> and <c>Size</c>, and
        /// explicit offsets of the struct and of every struct inside it are all reflected. <c>raw</c> alone: a size
        /// known only at run time.</item>
        /// <item><c>string</c>: a string as <c>NinoWire</c> describes it; <c>utf8</c>: a <c>[NinoUtf8]</c> string
        /// (int32 char count, -1 = null; for a non-empty string an int32 byte count, then UTF-8).</item>
        /// <item><c>object</c>: a <c>[NinoType]</c> value by its 4-byte type id (0 = null), then its body.</item>
        /// <item><c>type(T)</c>: a <c>[NinoType]</c> (or a type parameter), as its own layout describes it.</item>
        /// <item><c>nullable(E)</c>: a byte, 0 for null, 1 followed by <c>E</c>.</item>
        /// <item><c>seq(E)</c>: an int32 count (-1 = null), then the elements; <c>map(K,V)</c>: a count, then the
        /// key and value of each entry; <c>grid(rank,E)</c>: the length of each dimension (the first -1 = null),
        /// then the elements in row-major order.</item>
        /// <item><c>pair(K,V)</c>, <c>tuple(E,...)</c>: the items in order; <c>group(K,E)</c>: an element count
        /// (-1 = null), the key, then the elements.</item>
        /// <item><c>datetimeoffset</c> (int64 UTC ticks, then int16 offset minutes), <c>bigint</c>
        /// (int32 length, then two's complement little-endian bytes), <c>version</c> (four int32s, major -1 = null),
        /// <c>bits</c> (int32 bit count, -1 = null, then the bits in bytes), <c>timezone</c> (id string, then a
        /// serialized-form string for a custom zone).</item>
        /// <item><c>formatter(F)</c>: written by formatter <c>F</c> (a custom or another library's formatter).</item>
        /// </list>
        /// </summary>
        public string Members { get; }

        public NinoTypeLayoutAttribute(Type type, NinoFormat format, NinoHeader header, bool trackReferences,
            bool raw, int typeId, string members)
        {
            Type = type;
            Format = format;
            Header = header;
            TrackReferences = trackReferences;
            Raw = raw;
            Id = typeId;
            Members = members;
        }

        /// <summary>The whole layout in one line, for textual comparison.</summary>
        public override string ToString() =>
            $"format={Format}; header={Header}; trackReferences={(TrackReferences ? "true" : "false")}; " +
            $"raw={(Raw ? "true" : "false")}; typeId=0x{Id:X8}; members={Members}";
    }
}
