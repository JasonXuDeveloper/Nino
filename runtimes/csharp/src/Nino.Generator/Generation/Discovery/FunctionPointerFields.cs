using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Nino.Generator.Common;

namespace Nino.Generator.Generation;

/// <summary>
/// The function pointer (<c>delegate*</c>) fields of structs, as the arguments of the registry's
/// <c>NinoFunctionPointerFields.Register</c> calls: the assembly's name, the definition's runtime name
/// (<see cref="TypeNames.RuntimeName"/>, as <c>NinoTypeId.GetRuntimeName</c> reads it) and the fields' names, as C#
/// literals. Nino.Core lays these fields out as pointers, zero-extended to 8 bytes, where reflection cannot tell them
/// from an <c>IntPtr</c> (Mono, IL2CPP, .NET Framework) or reports them as a function pointer type (.NET 8 and later).
/// Names, not types: the struct need not be nameable (a private one inside a public one), and registering loads no
/// type. Every struct a compilation can see is registered, whether or not anything here names it, since generic code
/// (<c>Save&lt;T&gt;</c> calling <c>Serialize&lt;T&gt;</c>) reaches structs no member or call names: those declared
/// here (<see cref="Of"/>, from syntax), and those of every reference (<see cref="In(MetadataReference, string, CancellationToken)"/>),
/// read from their metadata, where private fields are (Roslyn's symbols of a metadata reference leave them out).
/// </summary>
internal static class FunctionPointerFields
{
    /// <summary>
    /// Whether <paramref name="node"/> declares a struct (or record struct) with a function pointer field, property
    /// or parameter of its primary constructor, from syntax alone (an alias of a function pointer type is
    /// <see cref="IsAlias"/>).
    /// </summary>
    public static bool IsDeclaredIn(SyntaxNode node)
    {
        if (node is not (StructDeclarationSyntax or RecordDeclarationSyntax) || node.IsKind(SyntaxKind.RecordDeclaration))
            return false;
        var type = (TypeDeclarationSyntax)node;
        foreach (var member in type.Members)
        {
            if (member is FieldDeclarationSyntax { Declaration.Type: FunctionPointerTypeSyntax } or
                PropertyDeclarationSyntax { Type: FunctionPointerTypeSyntax })
                return true;
        }

        return ParameterList(type) is { } parameters &&
               parameters.Parameters.Any(static p => p.Type is FunctionPointerTypeSyntax);
    }

    private static ParameterListSyntax? ParameterList(TypeDeclarationSyntax type) =>
#if ROSLYN4_8
        type.ParameterList;
#else
        // Roslyn 4.3 parses no primary constructor of a plain struct (C# 12).
        (type as RecordDeclarationSyntax)?.ParameterList;
#endif

#if ROSLYN4_8
    /// <summary>
    /// Whether <paramref name="node"/> is an alias of a function pointer type (C# 12: <c>using unsafe F =
    /// delegate*&lt;int&gt;;</c>), whose fields syntax cannot tell from any other type's.
    /// </summary>
    public static bool IsAlias(SyntaxNode node) =>
        node is UsingDirectiveSyntax { Alias: not null, NamespaceOrType: FunctionPointerTypeSyntax };
#endif

    /// <summary>
    /// The registration of <paramref name="type"/>'s function pointer fields: instance fields (auto-properties'
    /// backing fields among them) and the fields a plain struct's primary constructor captures (<c>&lt;p&gt;P</c>,
    /// registered whether captured or not); null without one.
    /// </summary>
    public static string? Of(INamedTypeSymbol type)
    {
        if (type.TypeKind != TypeKind.Struct || type.ContainingAssembly is not { } assembly)
            return null;
        List<string>? fields = null;
        foreach (var member in type.GetMembers())
        {
            if (member is IFieldSymbol { IsStatic: false, Type: IFunctionPointerTypeSymbol } field)
                (fields ??= new List<string>()).Add(field.MetadataName);
        }

        if (!type.IsRecord)
        {
            foreach (var constructor in type.InstanceConstructors)
            {
                // The primary constructor is declared by the type declaration itself.
                if (!constructor.DeclaringSyntaxReferences.Any(static r => r.GetSyntax() is TypeDeclarationSyntax))
                    continue;
                foreach (var parameter in constructor.Parameters)
                {
                    // Once the compiler has decided the capture, the field is among the members too (Registration
                    // drops the second).
                    if (parameter.Type is IFunctionPointerTypeSymbol)
                        (fields ??= new List<string>()).Add("<" + parameter.MetadataName + ">P");
                }
            }
        }

        return fields == null ? null : Registration(assembly.Name, TypeNames.RuntimeName(type.OriginalDefinition), fields);
    }

    /// <summary>The registrations of every struct <paramref name="assembly"/> declares, nested ones included.</summary>
    public static IEnumerable<string> In(IAssemblySymbol assembly, CancellationToken ct)
    {
        foreach (var type in AssemblyTypes.Of(assembly, ct))
        {
            if (Of(type) is { } registration)
                yield return registration;
        }
    }

    /// <summary>
    /// The registrations of every struct <paramref name="reference"/> declares (any accessibility), sorted: from its
    /// metadata, or from the referenced compilation's symbols (a project reference in the IDE). A module added to
    /// this assembly (<c>/addmodule</c>) declares its structs in <paramref name="assembly"/>. Unreadable metadata
    /// registers nothing (the compiler reports it).
    /// </summary>
    public static EquatableArray<string> In(MetadataReference reference, string assembly, CancellationToken ct)
    {
        if (reference is CompilationReference compilation)
            return LayoutTypes.ToArray(In(compilation.Compilation.Assembly, ct));
        if (reference is not PortableExecutableReference file)
            return EquatableArray<string>.Empty;
        List<string>? result = null;
        try
        {
            switch (file.GetMetadata())
            {
                case AssemblyMetadata metadata:
                    var modules = metadata.GetModules();
                    var manifest = modules[0].GetMetadataReader();
                    if (!manifest.IsAssembly)
                        break;
                    string name = manifest.GetString(manifest.GetAssemblyDefinition().Name);
                    foreach (var module in modules)
                        Scan(module.GetMetadataReader(), name, ref result, ct);
                    break;
                case ModuleMetadata module:
                    Scan(module.GetMetadataReader(), assembly, ref result, ct);
                    break;
            }
        }
        catch (Exception e) when (e is IOException or BadImageFormatException)
        {
            return EquatableArray<string>.Empty;
        }

        return result == null ? EquatableArray<string>.Empty : LayoutTypes.ToArray(result);
    }

    /// <summary>Adds the registration of every value type <paramref name="reader"/> defines with a function pointer field.</summary>
    private static void Scan(MetadataReader reader, string assembly, ref List<string>? into, CancellationToken ct)
    {
        foreach (var handle in reader.TypeDefinitions)
        {
            ct.ThrowIfCancellationRequested();
            var type = reader.GetTypeDefinition(handle);
            List<string>? fields = null;
            foreach (var fieldHandle in type.GetFields())
            {
                var field = reader.GetFieldDefinition(fieldHandle);
                if ((field.Attributes & FieldAttributes.Static) == 0 && IsFunctionPointer(reader.GetBlobReader(field.Signature)))
                    (fields ??= new List<string>()).Add(reader.GetString(field.Name));
            }

            if (fields != null && IsValueType(reader, type))
                (into ??= new List<string>()).Add(Registration(assembly, RuntimeName(reader, type), fields));
        }
    }

    /// <summary>Whether a field signature's type is a function pointer (after its custom modifiers: <c>volatile</c>).</summary>
    private static bool IsFunctionPointer(BlobReader signature)
    {
        if (signature.ReadSignatureHeader().Kind != SignatureKind.Field)
            return false;
        var code = signature.ReadSignatureTypeCode();
        while (code is SignatureTypeCode.RequiredModifier or SignatureTypeCode.OptionalModifier)
        {
            signature.ReadTypeHandle();
            code = signature.ReadSignatureTypeCode();
        }

        return code == SignatureTypeCode.FunctionPointer;
    }

    /// <summary>Whether <paramref name="type"/> derives from <c>System.ValueType</c> (a struct; an enum has no such field).</summary>
    private static bool IsValueType(MetadataReader reader, TypeDefinition type)
    {
        StringHandle ns, name;
        switch (type.BaseType.Kind)
        {
            case HandleKind.TypeReference:
                var reference = reader.GetTypeReference((TypeReferenceHandle)type.BaseType);
                (ns, name) = (reference.Namespace, reference.Name);
                break;
            case HandleKind.TypeDefinition:
                var definition = reader.GetTypeDefinition((TypeDefinitionHandle)type.BaseType);
                (ns, name) = (definition.Namespace, definition.Name);
                break;
            default:
                return false;
        }

        return reader.StringComparer.Equals(name, "ValueType") && reader.StringComparer.Equals(ns, "System");
    }

    /// <summary>
    /// The runtime name of a definition (<c>Ns.Outer+Inner&lt;T,U&gt;</c>), as <see cref="TypeNames.RuntimeName"/>
    /// spells a symbol's: a nested type's generic parameters are its containing types' and its own.
    /// </summary>
    private static string RuntimeName(MetadataReader reader, TypeDefinition type)
    {
        var sb = new StringBuilder();
        AppendDefinitionName(sb, reader, type);
        var parameters = type.GetGenericParameters();
        if (parameters.Count == 0)
            return sb.ToString();
        sb.Append('<');
        for (int i = 0; i < parameters.Count; i++)
        {
            if (i != 0)
                sb.Append(',');
            sb.Append(reader.GetString(reader.GetGenericParameter(parameters[i]).Name));
        }

        return sb.Append('>').ToString();
    }

    private static void AppendDefinitionName(StringBuilder sb, MetadataReader reader, TypeDefinition type)
    {
        var declaring = type.GetDeclaringType();
        if (!declaring.IsNil)
        {
            AppendDefinitionName(sb, reader, reader.GetTypeDefinition(declaring));
            sb.Append('+');
        }
        else if (reader.GetString(type.Namespace) is { Length: > 0 } ns)
        {
            sb.Append(ns).Append('.');
        }

        string name = reader.GetString(type.Name);
        int tick = name.IndexOf('`');
        sb.Append(name, 0, tick < 0 ? name.Length : tick);
    }

    /// <summary>
    /// The arguments: the fields once each and sorted, so that whether the compiler has decided a primary
    /// constructor's captures yet, or which view (source or metadata) found them, changes no generated text.
    /// </summary>
    private static string Registration(string assembly, string type, IEnumerable<string> fields) =>
        string.Join(", ", new[] { assembly, type }
            .Concat(fields.Distinct().OrderBy(static f => f, StringComparer.Ordinal)).Select(Literal));

    private static string Literal(string value) =>
        SymbolDisplay.FormatLiteral(value, quote: true);
}
