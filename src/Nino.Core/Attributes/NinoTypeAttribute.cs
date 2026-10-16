using System;

namespace Nino.Core
{
    /// <summary>
    /// Marks a type to be serialized by Nino. The source generator emits its formatter.
    /// <see href="https://nino.xgamedev.net/en/docs/v4/defining-types"/>
    /// </summary>
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface, Inherited = false)]
    public sealed class NinoTypeAttribute : Attribute
    {
        /// <param name="autoCollect">Serialize every public field and property (otherwise only <c>[NinoMember]</c>s).</param>
        /// <param name="containNonPublicMembers">
        /// Non-public members can be serialized too: every one with <paramref name="autoCollect"/>, the
        /// <c>[NinoMember]</c>s without it. Otherwise a non-public <c>[NinoMember]</c> is an error (NINO009).
        /// </param>
        /// <param name="allowInheritance">Whether subtypes are serializable through this type.</param>
        /// <param name="format">
        /// How the members are written: <see cref="NinoFormat.Positional"/> (the default),
        /// <see cref="NinoFormat.VersionTolerant"/> (members can be appended) or <see cref="NinoFormat.Keyed"/>
        /// (members can be added, removed, reordered or renamed).
        /// </param>
        /// <param name="trackReferences">
        /// Classes only. An object referenced several times in one call is written once and read back as one
        /// instance, so shared references and cycles round-trip. Costs 4 bytes and a hash lookup per object.
        /// Types that must be re-created from constructor arguments cannot be tracked.
        /// </param>
        public NinoTypeAttribute(bool autoCollect = true, bool containNonPublicMembers = false,
            bool allowInheritance = true, NinoFormat format = NinoFormat.Positional, bool trackReferences = false)
        {
            AutoCollect = autoCollect;
            ContainNonPublicMembers = containNonPublicMembers;
            AllowInheritance = allowInheritance;
            Format = format;
            TrackReferences = trackReferences;
        }

        /// <summary>Every public field and property is serialized (otherwise only <c>[NinoMember]</c>s).</summary>
        public bool AutoCollect { get; }

        /// <summary>Non-public members are serialized too (all of them with <see cref="AutoCollect"/>, the <c>[NinoMember]</c>s otherwise).</summary>
        public bool ContainNonPublicMembers { get; }

        /// <summary>Subtypes are serializable through this type.</summary>
        public bool AllowInheritance { get; }

        /// <summary>How the members are written.</summary>
        public NinoFormat Format { get; }

        /// <summary>Shared references and cycles round-trip.</summary>
        public bool TrackReferences { get; }
    }
}
