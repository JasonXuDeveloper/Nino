using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace Nino.Codegen.Layouts
{
    /// <summary>
    /// The closed generic types an assembly's metadata names: in a type, field, method, property or local signature,
    /// a type specification (<c>new Box&lt;int&gt;()</c>, <c>typeof</c>, a member of a closed type) or a generic
    /// method's arguments (<c>NinoPolymorphic.Register&lt;Box&lt;int&gt;&gt;()</c>). These are the closed types of the
    /// generic <c>[NinoType]</c>s the assembly's code can create or register, so the ones a polymorphic slot can hold:
    /// their type ids hash their closed .NET names, which a reader in another language cannot form from the
    /// definition. Types with a type parameter in them are left out.
    /// </summary>
    internal sealed class ClosedTypeScan : ISignatureTypeProvider<Type?, object?>
    {
        private readonly Assembly _assembly;
        private readonly Func<AssemblyName, Assembly?> _load;
        private readonly HashSet<Type> _found = new();

        private ClosedTypeScan(Assembly assembly, Func<AssemblyName, Assembly?> load)
        {
            _assembly = assembly;
            _load = load;
        }

        /// <summary>The closed generic types <paramref name="assembly"/> (read from <paramref name="path"/>) names.</summary>
        public static IReadOnlyCollection<Type> Scan(string path, Assembly assembly, Func<AssemblyName, Assembly?> load)
        {
            var scan = new ClosedTypeScan(assembly, load);
            using var stream = File.OpenRead(path);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata)
                return scan._found;
            var md = pe.GetMetadataReader();
            for (int row = 1, count = md.GetTableRowCount(TableIndex.TypeSpec); row <= count; row++)
            {
                var handle = MetadataTokens.TypeSpecificationHandle(row);
                Try(() => md.GetTypeSpecification(handle).DecodeSignature(scan, null));
            }

            for (int row = 1, count = md.GetTableRowCount(TableIndex.MethodSpec); row <= count; row++)
            {
                var handle = MetadataTokens.MethodSpecificationHandle(row);
                Try(() => md.GetMethodSpecification(handle).DecodeSignature(scan, null));
            }
            foreach (var handle in md.FieldDefinitions)
                Try(() => md.GetFieldDefinition(handle).DecodeSignature(scan, null));
            foreach (var handle in md.MethodDefinitions)
                Try(() => md.GetMethodDefinition(handle).DecodeSignature(scan, null));
            foreach (var handle in md.PropertyDefinitions)
                Try(() => md.GetPropertyDefinition(handle).DecodeSignature(scan, null));
            foreach (var handle in md.MemberReferences)
            {
                var reference = md.GetMemberReference(handle);
                Try(() =>
                {
                    if (reference.GetKind() == MemberReferenceKind.Field)
                        reference.DecodeFieldSignature(scan, null);
                    else
                        reference.DecodeMethodSignature(scan, null);
                });
            }

            for (int row = 1, count = md.GetTableRowCount(TableIndex.StandAloneSig); row <= count; row++)
            {
                var signature = md.GetStandaloneSignature(MetadataTokens.StandaloneSignatureHandle(row));
                Try(() =>
                {
                    if (signature.GetKind() == StandaloneSignatureKind.LocalVariables)
                        signature.DecodeLocalSignature(scan, null);
                    else
                        signature.DecodeMethodSignature(scan, null);
                });
            }

            return scan._found;
        }

        // A signature naming a type this context cannot load is skipped: it names no type nino-gen writes.
        private static void Try(Action decode)
        {
            try
            {
                decode();
            }
            catch (Exception e) when (e is FileNotFoundException or TypeLoadException or ArgumentException
                                          or BadImageFormatException or NotSupportedException
                                          or InvalidOperationException)
            {
            }
        }

        public Type? GetPrimitiveType(PrimitiveTypeCode typeCode) => typeCode switch
        {
            PrimitiveTypeCode.Void => null,
            PrimitiveTypeCode.TypedReference => null,
            PrimitiveTypeCode.IntPtr => Core("System.IntPtr"),
            PrimitiveTypeCode.UIntPtr => Core("System.UIntPtr"),
            PrimitiveTypeCode.Object => Core("System.Object"),
            PrimitiveTypeCode.String => Core("System.String"),
            _ => Core("System." + typeCode),
        };

        private Type? Core(string name) => CoreAssembly?.GetType(name);

        private Assembly? _core;
        private bool _coreSearched;

        // The assembly System.Object comes from: the scanned one itself (a core library), else one it references.
        private Assembly? CoreAssembly
        {
            get
            {
                if (_coreSearched)
                    return _core;
                _coreSearched = true;
                if (_assembly.GetType("System.Object") != null)
                    return _core = _assembly;
                foreach (var reference in _assembly.GetReferencedAssemblies())
                {
                    try
                    {
                        if (_load(reference) is { } loaded && loaded.GetType("System.Object") != null)
                            return _core = loaded;
                    }
                    catch (FileNotFoundException)
                    {
                    }
                }

                return null;
            }
        }

        public Type? GetTypeFromDefinition(MetadataReader reader, TypeDefinitionHandle handle, byte rawTypeKind) =>
            _assembly.GetType(DefinitionName(reader, handle));

        private static string DefinitionName(MetadataReader reader, TypeDefinitionHandle handle)
        {
            var definition = reader.GetTypeDefinition(handle);
            string name = reader.GetString(definition.Name);
            var declaring = definition.GetDeclaringType();
            if (!declaring.IsNil)
                return DefinitionName(reader, declaring) + "+" + name;
            string ns = reader.GetString(definition.Namespace);
            return ns.Length == 0 ? name : ns + "." + name;
        }

        public Type? GetTypeFromReference(MetadataReader reader, TypeReferenceHandle handle, byte rawTypeKind)
        {
            var reference = reader.GetTypeReference(handle);
            string name = reader.GetString(reference.Name);
            var scope = reference.ResolutionScope;
            switch (scope.Kind)
            {
                case HandleKind.TypeReference:
                    return GetTypeFromReference(reader, (TypeReferenceHandle)scope, rawTypeKind)?.GetNestedType(name,
                        BindingFlags.Public | BindingFlags.NonPublic);
                case HandleKind.AssemblyReference:
                {
                    var assembly = _load(reader.GetAssemblyReference((AssemblyReferenceHandle)scope).GetAssemblyName());
                    string ns = reader.GetString(reference.Namespace);
                    return assembly?.GetType(ns.Length == 0 ? name : ns + "." + name);
                }
                default:
                {
                    string ns = reader.GetString(reference.Namespace);
                    return _assembly.GetType(ns.Length == 0 ? name : ns + "." + name);
                }
            }
        }

        public Type? GetTypeFromSpecification(MetadataReader reader, object? genericContext,
            TypeSpecificationHandle handle, byte rawTypeKind) =>
            reader.GetTypeSpecification(handle).DecodeSignature(this, genericContext);

        public Type? GetGenericInstantiation(Type? genericType, ImmutableArray<Type?> typeArguments)
        {
            if (genericType == null)
                return null;
            var args = new Type[typeArguments.Length];
            for (int i = 0; i < args.Length; i++)
            {
                if (typeArguments[i] is not { } arg)
                    return null;
                args[i] = arg;
            }

            var closed = genericType.MakeGenericType(args);
            _found.Add(closed);
            return closed;
        }

        public Type? GetSZArrayType(Type? elementType) => elementType?.MakeArrayType();

        public Type? GetArrayType(Type? elementType, ArrayShape shape) => elementType?.MakeArrayType(shape.Rank);

        public Type? GetByReferenceType(Type? elementType) => elementType;

        public Type? GetPointerType(Type? elementType) => null;

        public Type? GetPinnedType(Type? elementType) => elementType;

        public Type? GetModifiedType(Type? modifier, Type? unmodifiedType, bool isRequired) => unmodifiedType;

        public Type? GetFunctionPointerType(MethodSignature<Type?> signature) => null;

        public Type? GetGenericMethodParameter(object? genericContext, int index) => null;

        public Type? GetGenericTypeParameter(object? genericContext, int index) => null;
    }
}
