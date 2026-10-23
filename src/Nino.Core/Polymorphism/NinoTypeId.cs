using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using Nino.Core.Internal;

namespace Nino.Core
{
    /// <summary>
    /// Computes the 32-bit type ids written in front of polymorphic values.
    /// <para>
    /// The id is the FNV-1a hash of the type name in runtime form: <c>Namespace.Outer+Inner</c>, with generic
    /// arguments in angle brackets (<c>Ns.Box&lt;System.Int32&gt;</c>) and arrays as <c>System.Int32[]</c>.
    /// <c>[NinoFormerName]</c> replaces the name. The id is never 0, which encodes <c>null</c>.
    /// The source generator computes the same ids at compile time.
    /// </para>
    /// </summary>
    public static class NinoTypeId
    {
        private static readonly ConcurrentDictionary<Type, int> Cache = NinoUnload.Cache<int>();

        /// <summary>Hashes a type name.</summary>
        public static int Compute(string name) => NinoHash.Compute(name);

        /// <summary>Returns the id of <paramref name="type"/>, honoring <see cref="NinoFormerNameAttribute"/>.</summary>
        public static int Of(Type type) => Cache.GetOrAdd(type, t => Compute(GetIdName(t)));

        /// <summary>Returns the name hashed for <paramref name="type"/>.</summary>
        public static string GetIdName(Type type)
        {
            var former = type.GetCustomAttribute<NinoFormerNameAttribute>(false);
            return former?.FormerName ?? GetName(type);
        }

        /// <summary>Returns the runtime-form name of <paramref name="type"/>.</summary>
        public static string GetName(Type type)
        {
            var sb = new StringBuilder();
            AppendName(sb, type);
            return sb.ToString();
        }

        private static void AppendName(StringBuilder sb, Type type)
        {
            if (type.IsArray)
            {
                AppendName(sb, type.GetElementType());
                sb.Append('[');
                sb.Append(',', type.GetArrayRank() - 1);
                sb.Append(']');
                return;
            }

            if (type.IsGenericParameter)
            {
                sb.Append(type.Name);
                return;
            }

            AppendDefinitionName(sb, type);

            if (!type.IsGenericType)
                return;

            var args = type.GetGenericArguments();
            sb.Append('<');
            for (int i = 0; i < args.Length; i++)
            {
                if (i != 0)
                    sb.Append(',');
                AppendName(sb, args[i]);
            }

            sb.Append('>');
        }

        private static void AppendDefinitionName(StringBuilder sb, Type type)
        {
            if (type.IsNested)
            {
                AppendDefinitionName(sb, type.DeclaringType);
                sb.Append('+');
            }
            else if (!string.IsNullOrEmpty(type.Namespace))
            {
                sb.Append(type.Namespace);
                sb.Append('.');
            }

            string name = type.Name;
            int tick = name.IndexOf('`');
            sb.Append(name, 0, tick < 0 ? name.Length : tick);
        }
    }
}
