using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Nino.Codegen.Model;

namespace Nino.Codegen.Targets
{
    /// <summary>Options of the generated Rust.</summary>
    public sealed class RustOptions
    {
        /// <summary>Derive serde's traits behind the crate's <c>serde</c> feature.</summary>
        public bool Serde { get; set; }

        /// <summary>The names serde gives the members.</summary>
        public TagNaming SerdeNaming { get; set; } = TagNaming.Camel;

        /// <summary>
        /// Strings as <c>nino::WideString</c> (UTF-16, keeps lone surrogates, as .NET does) instead of <c>String</c>
        /// (which reads one as U+FFFD).
        /// </summary>
        public bool WideStrings { get; set; }

        /// <summary>
        /// Maps and sets as <c>std::collections::HashMap</c> and <c>HashSet</c> instead of <c>indexmap</c>'s
        /// <c>IndexMap</c> and <c>IndexSet</c> (no dependency, but written in the hash table's order, not the order
        /// read).
        /// </summary>
        public bool StdMaps { get; set; }
    }
}
