using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Nino.Generator.Metadata;
using Nino.Generator.Template;

namespace Nino.Generator.Common;

public class UnsafeAccessorGenerator : NinoCommonGenerator
{
    public UnsafeAccessorGenerator(Compilation compilation, NinoGraph ninoGraph, List<NinoType> ninoTypes)
        : base(compilation, ninoGraph, ninoTypes)
    {
    }

    protected override void Generate(SourceProductionContext spc)
    {
        var compilation = Compilation;

        var sb = new StringBuilder();
        var generatedTypes = new HashSet<NinoType>();
        var generatedMembers = new HashSet<(string, string)>();

        foreach (var ninoType in NinoTypes)
        {
            try
            {
                bool isPolymorphicType = ninoType.IsPolymorphic();

                // check if struct is unmanaged
                if (ninoType.TypeSymbol.IsUnmanagedType && !isPolymorphicType)
                {
                    continue;
                }

                void WriteMembers(NinoType type)
                {
                    if (!generatedTypes.Add(type))
                    {
                        return;
                    }

                    foreach (var member in type.Members)
                    {
                        if (!member.IsPrivate)
                        {
                            continue;
                        }

                        var declaringType = type.TypeSymbol;
                        while (declaringType != null && declaringType.IsNinoType())
                        {
                            var m = declaringType.GetMembers().FirstOrDefault(m => m.Name == member.Name);
                            if (m != null)
                            {
                                declaringType = m.ContainingType;
                                break;
                            }

                            declaringType = declaringType.BaseType;
                        }

                        if (declaringType == null)
                        {
                            continue;
                        }

                        string typeName = declaringType.ToDisplayString();

                        if (!generatedMembers.Add((typeName, member.Name)))
                        {
                            continue;
                        }

                        if (declaringType.IsValueType)
                        {
                            typeName = $"ref {typeName}";
                        }

                        var name = member.Name;
                        var declaredType = member.Type;

                        if (member.IsProperty)
                        {
                            sb.AppendLine(
                                $"        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = \"get_{name}\")]");
                            sb.AppendLine(
                                $"        internal extern static {declaredType.ToDisplayString()} __get__{name}__({typeName} @this);");

                            sb.AppendLine(
                                $"        [UnsafeAccessor(UnsafeAccessorKind.Method, Name = \"set_{name}\")]");
                            sb.AppendLine(
                                $"        internal extern static void __set__{name}__({typeName} @this, {declaredType.ToDisplayString()} value);");
                        }
                        else
                        {
                            sb.AppendLine(
                                $"        [UnsafeAccessor(UnsafeAccessorKind.Field, Name = \"{name}\")]");
                            sb.AppendLine(
                                $"        internal extern static ref {declaredType.ToDisplayString()} __{name}__({typeName} @this);");
                        }
                    }
                }

                if (NinoGraph.SubTypes.TryGetValue(ninoType, out var lst))
                {
                    //sort lst by how deep the inheritance is (i.e. how many levels of inheritance), the deepest first
                    lst.Sort((a, b) =>
                    {
                        int aCount = NinoGraph.BaseTypes[a].Count;
                        int bCount = NinoGraph.BaseTypes[b].Count;
                        return bCount.CompareTo(aCount);
                    });

                    foreach (var subType in lst)
                    {
                        var subTypeSymbol = subType.TypeSymbol;
                        if (subTypeSymbol.IsInstanceType())
                        {
                            if (subTypeSymbol.IsUnmanagedType)
                            {
                                continue;
                            }

                            WriteMembers(subType);
                        }
                    }
                }

                if (ninoType.TypeSymbol.IsInstanceType())
                {
                    if (ninoType.TypeSymbol.IsUnmanagedType)
                    {
                        continue;
                    }

                    WriteMembers(ninoType);
                }
            }
            catch (Exception e)
            {
                sb.AppendLine($"/* Error: {e.Message} for type {ninoType.TypeSymbol.GetTypeFullName()}");
                //add stacktrace
                foreach (var line in e.StackTrace.Split('\n'))
                {
                    sb.AppendLine($" * {line}");
                }

                //end error
                sb.AppendLine(" */");
            }
        }

        var curNamespace = compilation.AssemblyName!.GetNamespace();

        // generate code
        var code = $$"""
                     // <auto-generated/>

                     using System;
                     using System.Runtime.CompilerServices;
                     
                     #nullable disable
                     #if NET8_0_OR_GREATER
                     namespace {{curNamespace}}
                     {
                         internal static partial class PrivateAccessor
                         {
                     {{sb}}    }
                     }
                     #endif
                     """;

        spc.AddSource("NinoPrivateAccessor.g.cs", code);
    }
}