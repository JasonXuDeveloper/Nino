using System.ComponentModel;

namespace Nino.Core
{
    /// <summary>
    /// For Nino's own packages (Nino.Immutable): the settings of <see cref="NinoSettings"/> that read paths check, as
    /// they are in effect, frozen when first read (reading one here freezes them, as a read or write would).
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static class NinoEffectiveSettings
    {
        /// <summary><see cref="NinoSettings.MaxHashCollisions"/>, frozen.</summary>
        public static int MaxHashCollisions => NinoLimits.MaxHashCollisions;
    }
}
