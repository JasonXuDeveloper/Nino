using System;
using System.Collections.Generic;
using System.Linq;
using Nino.Codegen.Model;

namespace Nino.Codegen.Targets
{
    /// <summary>Options of the generated TypeScript.</summary>
    public sealed class TypeScriptOptions
    {
        /// <summary>The extension of the runtime's import (<c>""</c>, <c>".js"</c>, <c>".ts"</c>).</summary>
        public string ImportExtension { get; set; } = "";

        /// <summary>
        /// The module the generated code imports the runtime from: <c>./nino</c> (the default, with
        /// <see cref="ImportExtension"/>; <c>nino.ts</c> is written next to the code) or a package or path
        /// (<c>nino-serialization</c>), imported as given.
        /// </summary>
        public string? RuntimeModule { get; set; }

        /// <summary>Whether member names are camelCased (the default) or kept as in C#.</summary>
        public bool CamelCase { get; set; } = true;

        /// <summary>
        /// Whether arrays and lists of fixed-size numbers are typed arrays (<c>Int32Array</c>, <c>Float64Array</c>...;
        /// <c>--numeric-arrays typed</c>) rather than <c>number[]</c> and <c>bigint[]</c> (the default).
        /// </summary>
        public bool TypedNumericArrays { get; set; }
    }
}
