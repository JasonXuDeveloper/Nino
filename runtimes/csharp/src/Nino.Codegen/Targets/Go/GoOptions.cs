using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Nino.Codegen.Model;
using Nino.Core.Internal;

namespace Nino.Codegen.Targets
{
    /// <summary>How the extra struct tags name a member (runtimes/go/README.md, "Tags on generated structs").</summary>
    public enum TagNaming
    {
        Camel,
        Snake,
        Original,
    }

    /// <summary>Options of the generated Go.</summary>
    public sealed class GoOptions
    {
        /// <summary>The package of the generated file.</summary>
        public string Package { get; set; } = "ninotypes";

        /// <summary>The struct tags written after <c>nino</c>, in this order (<c>json</c>, <c>yaml</c>).</summary>
        public List<string> Tags { get; set; } = new() { "json", "yaml" };

        public TagNaming TagNaming { get; set; } = TagNaming.Camel;
    }
}
