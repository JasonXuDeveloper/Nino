using System;
using System.Diagnostics;

namespace Nino.Core
{
    /// <summary>
    /// The layout baseline of an assembly: the wire layouts of its version-tolerant and keyed <c>[NinoType]</c>s (and
    /// of every type their members reach) that data already written depends on. With one in the assembly, Nino's
    /// analyzer compares each type with its section and reports a change that breaks reading old data or lets old
    /// code misread new data (NINO034), drops data silently (NINO035) or only needs care (NINO036), on the line that
    /// made it.
    /// <para>
    /// The argument is a TOML document, one table per type (<c>['Namespace.Type']</c> with <c>format</c>,
    /// <c>header</c>, <c>id</c>, <c>members</c>, ...), written and kept up to date by the analyzer's code fixes or by
    /// <c>nino-gen layouts</c>; edit it through them. Put it in a file of its own (<c>NinoLayouts.cs</c>):
    /// <c>[assembly: NinoLayouts(@"...")]</c>.
    /// </para>
    /// <para>
    /// The attribute is conditional on <c>NINO_LAYOUTS</c>, a symbol nobody defines: the compiler checks it and the
    /// analyzer reads it from source, but it is never written to the assembly, so the baseline costs nothing at run
    /// time.
    /// </para>
    /// <see href="https://nino.xgamedev.net/en/docs/v4/evolving-data#layout-baseline"/>
    /// </summary>
    [AttributeUsage(AttributeTargets.Assembly)]
    [Conditional("NINO_LAYOUTS")]
    public sealed class NinoLayoutsAttribute : Attribute
    {
        /// <summary>The baseline, a TOML document.</summary>
        public string Layouts { get; }

        /// <param name="layouts">The baseline, a TOML document (see the class).</param>
        public NinoLayoutsAttribute(string layouts)
        {
            Layouts = layouts;
        }
    }
}
