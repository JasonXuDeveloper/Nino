using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Nino.TypeScript
{
    /// <summary>The published layout of a <c>[NinoType]</c> (<c>NinoTypeLayoutAttribute</c>), with its type.</summary>
    internal sealed class TypeLayout
    {
        public Type Type = null!;
        /// <summary>0 positional, 1 version tolerant, 2 keyed (<c>NinoFormat</c>).</summary>
        public int Format;
        /// <summary>0 none (struct), 1 null flag (sealed class), 2 type id (<c>NinoHeader</c>).</summary>
        public int Header;
        public bool TrackReferences;
        public bool Raw;
        public int Id;
        public string MembersText = "";
        /// <summary>Set for a <c>[NinoType]</c> collection (its elements come before the members).</summary>
        public WireEncoding? Elements;
        public List<MemberLayout> Members = new();
        /// <summary>The raw encoding of a <see cref="Raw"/> type.</summary>
        public WireEncoding? RawEncoding;

        public const int Positional = 0, VersionTolerant = 1, Keyed = 2;
        public const int NoHeader = 0, NullFlag = 1, TypeId = 2;
    }

    /// <summary>A member of a layout: <c>name:type:encoding[:key=0x..][:former=0x..,..]</c>.</summary>
    internal sealed class MemberLayout
    {
        public string Name = "";
        public string TypeName = "";
        public WireEncoding Encoding = null!;
        public int Key;
        public List<int> FormerKeys = new();
        /// <summary>The member's type, found on the type by name.</summary>
        public Type? Type;
        /// <summary>The type that declares the member.</summary>
        public Type? DeclaringType;
    }

    /// <summary>
    /// Reads the <c>[assembly: NinoTypeLayout]</c>s of an assembly without loading it (MetadataLoadContext), so the
    /// assembly's target framework does not matter.
    /// </summary>
    internal sealed class LayoutReader : IDisposable
    {
        private const string LayoutAttribute = "Nino.Core.NinoTypeLayoutAttribute";
        private readonly MetadataLoadContext _context;

        public LayoutReader(string assemblyPath, IEnumerable<string> references)
        {
            // The references of the build, else the running runtime's assemblies (enough for metadata).
            var paths = new List<string> { Path.GetFullPath(assemblyPath) };
            paths.AddRange(references.Where(File.Exists).Select(Path.GetFullPath));
            if (paths.Count == 1)
                paths.AddRange(Directory.GetFiles(RuntimeEnvironment.GetRuntimeDirectory(), "*.dll"));
            string directory = Path.GetDirectoryName(Path.GetFullPath(assemblyPath))!;
            paths.AddRange(Directory.GetFiles(directory, "*.dll"));
            var byName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in paths)
                byName.TryAdd(Path.GetFileName(path), path);
            _context = new MetadataLoadContext(new PathAssemblyResolver(byName.Values));
            Assembly = _context.LoadFromAssemblyPath(Path.GetFullPath(assemblyPath));
        }

        public Assembly Assembly { get; }

        public void Dispose() => _context.Dispose();

        /// <summary>
        /// The layouts of <see cref="Assembly"/> and of the referenced assemblies that have any, by type (a member
        /// may hold a <c>[NinoType]</c> of another assembly).
        /// </summary>
        public Dictionary<Type, TypeLayout> ReadAll()
        {
            var layouts = new Dictionary<Type, TypeLayout>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pending = new Queue<Assembly>();
            pending.Enqueue(Assembly);
            while (pending.Count != 0)
            {
                var assembly = pending.Dequeue();
                if (!seen.Add(assembly.GetName().Name ?? ""))
                    continue;
                foreach (var layout in Read(assembly))
                    layouts[layout.Type] = layout;
                foreach (var reference in assembly.GetReferencedAssemblies())
                {
                    try
                    {
                        pending.Enqueue(_context.LoadFromAssemblyName(reference));
                    }
                    catch (FileNotFoundException)
                    {
                    }
                }
            }

            return layouts;
        }

        private static IEnumerable<TypeLayout> Read(Assembly assembly)
        {
            IList<CustomAttributeData> attributes;
            try
            {
                attributes = assembly.GetCustomAttributesData();
            }
            catch (FileNotFoundException)
            {
                yield break;
            }

            foreach (var attribute in attributes)
            {
                if (attribute.AttributeType.FullName != LayoutAttribute)
                    continue;
                var args = attribute.ConstructorArguments;
                var layout = new TypeLayout
                {
                    Type = (Type)args[0].Value!,
                    Format = Convert.ToInt32(args[1].Value, CultureInfo.InvariantCulture),
                    Header = Convert.ToInt32(args[2].Value, CultureInfo.InvariantCulture),
                    TrackReferences = (bool)args[3].Value!,
                    Raw = (bool)args[4].Value!,
                    Id = (int)args[5].Value!,
                    MembersText = (string)args[6].Value!,
                };
                Parse(layout);
                yield return layout;
            }
        }

        private static void Parse(TypeLayout layout)
        {
            if (layout.Raw)
            {
                layout.RawEncoding = WireEncoding.Parse(layout.MembersText);
                return;
            }

            var members = MemberSources(layout.Type);
            var used = new HashSet<MemberInfo>();
            foreach (var entry in WireEncoding.SplitMembers(layout.MembersText))
            {
                if (entry.StartsWith("[elements]::", StringComparison.Ordinal))
                {
                    layout.Elements = WireEncoding.Parse(entry.Substring("[elements]::".Length));
                    continue;
                }

                int colon = entry.IndexOf(':');
                int typeEnd = TypeEnd(entry, colon + 1);
                int pos = typeEnd + 1;
                var member = new MemberLayout
                {
                    Name = entry.Substring(0, colon),
                    TypeName = entry.Substring(colon + 1, typeEnd - colon - 1),
                    Encoding = WireEncoding.Parse(entry, ref pos),
                };
                while (pos < entry.Length)
                {
                    int next = entry.IndexOf(':', pos + 1);
                    string suffix = entry.Substring(pos + 1, (next < 0 ? entry.Length : next) - pos - 1);
                    pos = next < 0 ? entry.Length : next;
                    if (suffix.StartsWith("key=", StringComparison.Ordinal))
                        member.Key = Hex(suffix.Substring(4));
                    else if (suffix.StartsWith("former=", StringComparison.Ordinal))
                        member.FormerKeys.AddRange(suffix.Substring(7).Split(',').Select(Hex));
                }

                // The first member of that name not taken yet, base types first: a member hidden with `new` is
                // written before the one hiding it.
                var source = members.FirstOrDefault(m => m.Name == member.Name && !used.Contains(m));
                if (source != null)
                {
                    used.Add(source);
                    member.Type = source is FieldInfo f ? f.FieldType : ((PropertyInfo)source).PropertyType;
                    member.DeclaringType = source.DeclaringType;
                }

                layout.Members.Add(member);
            }
        }

        private static int Hex(string text) =>
            unchecked((int)uint.Parse(text.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture));

        /// <summary>The fields and properties of a type and its bases, base types first.</summary>
        private static List<MemberInfo> MemberSources(Type type)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic |
                                       BindingFlags.DeclaredOnly;
            var chain = new List<Type>();
            for (var t = type; t != null; t = t.BaseType)
                chain.Insert(0, t);
            var members = new List<MemberInfo>();
            foreach (var t in chain)
            {
                members.AddRange(t.GetFields(flags).Where(f => !f.Name.StartsWith('<')));
                members.AddRange(t.GetProperties(flags));
            }

            // A [NinoType] interface's members are its implementations' members of the same name.
            if (type.IsInterface)
                members.AddRange(type.GetInterfaces().SelectMany(i => i.GetProperties()));
            return members;
        }

        /// <summary>The ':' that ends the member's type at <paramref name="pos"/> (outside generic brackets).</summary>
        private static int TypeEnd(string entry, int pos)
        {
            int depth = 0;
            for (int i = pos; i < entry.Length; i++)
            {
                char c = entry[i];
                if (c is '<' or '(' or '[')
                    depth++;
                else if (c is '>' or ')' or ']')
                    depth--;
                else if (c == ':' && depth == 0)
                    return i;
            }

            throw new FormatException($"Expected the member's encoding in \"{entry}\".");
        }
    }
}
