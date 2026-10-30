using System;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Nino.Codegen.Model
{
    /// <summary>Names of .NET types and members, and the hash Nino's type ids and keys use.</summary>
    public static class TypeNames
    {
        /// <summary>A type name without its generic arity (<c>Box`1</c> → <c>Box</c>).</summary>
        public static string WithoutArity(string name)
        {
            int tick = name.IndexOf('`');
            return tick < 0 ? name : name.Substring(0, tick);
        }

        /// <summary>The runtime-form name (<c>NinoTypeId.GetRuntimeName</c>): <c>Namespace.Outer+Inner</c>, <c>Box&lt;System.Int32&gt;</c>.</summary>
        internal static string Runtime(Type type)
        {
            var sb = new StringBuilder();
            AppendRuntime(sb, type);
            return sb.ToString();
        }

        private static void AppendRuntime(StringBuilder sb, Type type)
        {
            if (type.IsArray)
            {
                AppendRuntime(sb, type.GetElementType()!);
                sb.Append('[').Append(',', type.GetArrayRank() - 1).Append(']');
                return;
            }

            if (type.IsGenericParameter)
            {
                sb.Append(type.Name);
                return;
            }

            AppendDefinition(sb, type);
            if (!type.IsGenericType)
                return;
            sb.Append('<');
            var args = type.GetGenericArguments();
            for (int i = 0; i < args.Length; i++)
            {
                if (i != 0)
                    sb.Append(',');
                AppendRuntime(sb, args[i]);
            }

            sb.Append('>');
        }

        private static void AppendDefinition(StringBuilder sb, Type type)
        {
            if (type.IsNested)
            {
                AppendDefinition(sb, type.DeclaringType!);
                sb.Append('+');
            }
            else if (!string.IsNullOrEmpty(type.Namespace))
                sb.Append(type.Namespace).Append('.');

            sb.Append(WithoutArity(type.Name));
        }

        /// <summary>
        /// The name generated code gives a type when no other type has it: nested and generic types joined with
        /// <c>_</c> (<c>Outer_Inner</c>, <c>Box_Int32</c>).
        /// </summary>
        internal static string Simple(Type type)
        {
            string name = WithoutArity(type.Name);
            if (type.IsGenericType)
                name += "_" + string.Join("_", type.GetGenericArguments().Select(Simple));
            return type.IsNested ? Simple(type.DeclaringType!) + "_" + name : name;
        }

        /// <summary>
        /// The C# name with its leading capitals lower-cased, keeping the last when a lower-case letter follows:
        /// <c>Health</c> → <c>health</c>, <c>URLPath</c> → <c>urlPath</c>, <c>ID</c> → <c>id</c>.
        /// </summary>
        public static string Camel(string name)
        {
            if (name.Length == 0 || !char.IsUpper(name[0]))
                return name;
            int upper = 0;
            while (upper < name.Length && char.IsUpper(name[upper]))
                upper++;
            int lower = upper == name.Length ? upper : Math.Max(1, upper - 1);
            return string.Concat(name.Substring(0, lower).ToLowerInvariant(), name.AsSpan(lower));
        }

        /// <summary>
        /// The C# name as lower-case words joined by <c>_</c>, digits kept with the word before them:
        /// <c>IntList</c> → <c>int_list</c>, <c>URLPath</c> → <c>url_path</c>, <c>Utf8A</c> → <c>utf8_a</c>.
        /// </summary>
        public static string Snake(string name)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < name.Length; i++)
            {
                char c = name[i];
                if (char.IsUpper(c))
                {
                    bool previousLower = i > 0 && (char.IsLower(name[i - 1]) || char.IsDigit(name[i - 1]));
                    bool nextLower = i + 1 < name.Length && char.IsLower(name[i + 1]);
                    bool previousUpper = i > 0 && char.IsUpper(name[i - 1]);
                    if (sb.Length != 0 && sb[^1] != '_' && (previousLower || previousUpper && nextLower))
                        sb.Append('_');
                    sb.Append(char.ToLowerInvariant(c));
                }
                else
                    sb.Append(c);
            }

            return sb.ToString();
        }

        /// <summary>The C# name with its first letter upper-cased (<c>value</c> → <c>Value</c>).</summary>
        public static string Pascal(string name) =>
            name.Length == 0 || char.IsUpper(name[0]) ? name : char.ToUpperInvariant(name[0]) + name.Substring(1);

        /// <summary>
        /// <see cref="Simple"/>, but a generic definition by its name alone (<c>Box</c>, not <c>Box_T</c>): its
        /// parameters are the generated type's.
        /// </summary>
        internal static string Definition(Type type) => type.IsGenericTypeDefinition
            ? (type.IsNested ? Definition(type.DeclaringType!) + "_" : "") + WithoutArity(type.Name)
            : Simple(type);

        /// <summary>
        /// The parts of a C# name joined, each with a capital (<c>Outer_Inner</c> → <c>OuterInner</c>): an exported Go
        /// name, a Rust type name.
        /// </summary>
        internal static string Joined(string name) =>
            string.Concat(name.Split(Underscore, StringSplitOptions.RemoveEmptyEntries).Select(Pascal));

        private static readonly char[] Underscore = { '_' };

        /// <summary>
        /// The name of a closed generic type in a slot, as every language names one (<c>BoxInt32</c>,
        /// <c>BoxInt32Array</c> for <c>Box&lt;int[]&gt;</c>): an identifier in each.
        /// </summary>
        internal static string Closed(Type type) => Joined(new string(Simple(type).Replace("[", "Array_")
            .Where(c => char.IsAsciiLetterOrDigit(c) || c == '_').ToArray()));

        /// <summary>FNV-1a over the UTF-16 code units, 0 mapped to 1 (wire-format spec, appendix B.1).</summary>
        public static int Hash(string name)
        {
            uint h = 0x811C9DC5;
            foreach (char c in name)
            {
                h = unchecked((h ^ (uint)(c & 0xFF)) * 0x01000193);
                h = unchecked((h ^ (uint)(c >> 8)) * 0x01000193);
            }

            return unchecked((int)(h == 0 ? 1 : h));
        }

        /// <summary><c>0x1234ABCD</c>.</summary>
        public static string Hex(int value) => "0x" + value.ToString("X8", CultureInfo.InvariantCulture);
    }
}
