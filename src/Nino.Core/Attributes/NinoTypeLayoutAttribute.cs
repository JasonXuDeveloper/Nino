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
        /// and how it is written: <c>raw(n)</c> for a member copied as <c>n</c> bytes (<c>raw</c> when the size is
        /// that of a struct), otherwise the formatter that writes it (<c>Nino.Core.StringFormatter</c>,
        /// <c>Nino.Core.Utf8StringFormatter</c>, a custom formatter, or the generated formatter of a nested
        /// <c>[NinoType]</c>, whose own layout describes it). A keyed member read under former names too ends
        /// with <c>:former=</c> and their keys. A <c>[NinoType]</c> collection starts with
        /// <c>[elements]::formatter</c>, the formatter of the elements written before the members.
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
