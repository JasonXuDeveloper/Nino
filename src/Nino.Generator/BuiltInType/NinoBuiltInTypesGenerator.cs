// NinoBuiltInTypesGenerator.cs
//
//  Author:
//        JasonXuDeveloper <jason@xgamedev.net>
//
//  Copyright (c) 2025 JEngine
//
//  Permission is hereby granted, free of charge, to any person obtaining a copy
//  of this software and associated documentation files (the "Software"), to deal
//  in the Software without restriction, including without limitation the rights
//  to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
//  copies of the Software, and to permit persons to whom the Software is
//  furnished to do so, subject to the following conditions:
//
//  The above copyright notice and this permission notice shall be included in
//  all copies or substantial portions of the Software.
//
//  THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
//  IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
//  FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
//  AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
//  LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
//  OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
//  THE SOFTWARE.

using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Nino.Generator.Metadata;
using Nino.Generator.Template;

namespace Nino.Generator.BuiltInType;

/// <summary>
/// Unified generator that consolidates all built-in type generators into a single set of output files.
/// This reduces file count from 3*N to 3 files total (Serializer, Deserializer, Registration).
/// </summary>
public class NinoBuiltInTypesGenerator(
    NinoGraph ninoGraph,
    HashSet<ITypeSymbol> potentialTypes,
    HashSet<ITypeSymbol> generatedTypes,
    Compilation compilation) : NinoGenerator(compilation)
{
    private readonly NinoBuiltInTypeGenerator[] _generators =
    {
        new NullableGenerator(ninoGraph, potentialTypes, generatedTypes, compilation),
        new KeyValuePairGenerator(ninoGraph, potentialTypes, generatedTypes, compilation),
        new TupleGenerator(ninoGraph, potentialTypes, generatedTypes, compilation),
        new ArrayGenerator(ninoGraph, potentialTypes, generatedTypes, compilation),
        new DictionaryGenerator(ninoGraph, potentialTypes, generatedTypes, compilation),
        new ListGenerator(ninoGraph, potentialTypes, generatedTypes, compilation),
        new ArraySegmentGenerator(ninoGraph, potentialTypes, generatedTypes, compilation),
        new QueueGenerator(ninoGraph, potentialTypes, generatedTypes, compilation),
        new StackGenerator(ninoGraph, potentialTypes, generatedTypes, compilation),
        new HashSetGenerator(ninoGraph, potentialTypes, generatedTypes, compilation),
        new LinkedListGenerator(ninoGraph, potentialTypes, generatedTypes, compilation),
        new ImmutableArrayGenerator(ninoGraph, potentialTypes, generatedTypes, compilation),
        new ImmutableListGenerator(ninoGraph, potentialTypes, generatedTypes, compilation),
        new PriorityQueueGenerator(ninoGraph, potentialTypes, generatedTypes, compilation),
        new SortedSetGenerator(ninoGraph, potentialTypes, generatedTypes, compilation),
    };

    protected override void Generate(SourceProductionContext spc)
    {
        // Clear and pre-filter to identify which types are handled by built-in generators
        generatedTypes.Clear();
        var filterTypes = potentialTypes.ToList().OrderBy(static t => t.GetTypeHierarchyLevel()).ToList();
        foreach (var type in filterTypes)
        {
            foreach (var generator in _generators)
            {
                if (generator.Filter(type))
                {
                    generatedTypes.Add(type);
                    break;
                }
            }
        }

        NinoBuiltInTypeGenerator.Writer serializerWriter = new("        ");
        serializerWriter.AppendLine();
        NinoBuiltInTypeGenerator.Writer deserializerWriter = new("        ");
        deserializerWriter.AppendLine();
        HashSet<ITypeSymbol> registeredTypes = new(TupleSanitizedEqualityComparer.Default);

        // Process each generator and collect their outputs
        foreach (var generator in _generators)
        {
            var (serializerCode, deserializerCode, types) = generator.GenerateCode(potentialTypes);

            if (types.Count > 0)
            {
                serializerWriter.Append(serializerCode);
                serializerWriter.AppendLine();
                deserializerWriter.Append(deserializerCode);
                deserializerWriter.AppendLine();

                foreach (var type in types)
                {
                    registeredTypes.Add(type);
                }
            }
        }

        // Generate registration code
        StringBuilder registrationCode = new();
        foreach (var registeredType in registeredTypes)
        {
            var typeName = registeredType.GetDisplayString();
            registrationCode.AppendLine(
                $"                NinoTypeMetadata.RegisterSerializer<{typeName}>(Serializer.Serialize, false);");
            registrationCode.AppendLine(
                $"                NinoTypeMetadata.RegisterDeserializer<{typeName}>(-1, Deserializer.Deserialize, Deserializer.DeserializeRef, false);");
        }

        var curNamespace = Compilation.AssemblyName!.GetNamespace();

        // Generate serializer file
        var code = $$"""
                     // <auto-generated/>
                     #pragma warning disable CS8669

                     using System;
                     using global::Nino.Core;
                     using System.Buffers;
                     using System.Collections.Generic;
                     using System.Collections.Concurrent;
                     using System.Runtime.InteropServices;
                     using System.Runtime.CompilerServices;

                     namespace {{curNamespace}}
                     {
                         public static partial class Serializer
                         {
                     {{serializerWriter}}    }
                     }
                     """;

        spc.AddSource($"{curNamespace}.NinoBuiltInTypes.Serializer.g.cs", code);

        // Generate deserializer file
        code = $$"""
                 // <auto-generated/>
                 #pragma warning disable CS8669

                 using System;
                 using global::Nino.Core;
                 using System.Buffers;
                 using System.Collections.Generic;
                 using System.Collections.Concurrent;
                 using System.Runtime.InteropServices;
                 using System.Runtime.CompilerServices;

                 namespace {{curNamespace}}
                 {
                     public static partial class Deserializer
                     {
                 {{deserializerWriter}}    }
                 }
                 """;
        spc.AddSource($"{curNamespace}.NinoBuiltInTypes.Deserializer.g.cs", code);

        // Generate registration file
        if (registrationCode.Length > 0)
        {
            code = $$"""
                     // <auto-generated/>
                     #pragma warning disable CS8669

                     using System;
                     using global::Nino.Core;
                     using System.Collections.Concurrent;
                     using System.Runtime.InteropServices;
                     using System.Runtime.CompilerServices;

                     namespace {{curNamespace}}
                     {
                         public static class NinoBuiltInTypesRegistration
                         {
                             private static bool _initialized;
                             private static object _lock = new object();

                             static NinoBuiltInTypesRegistration()
                             {
                                 Init();
                             }

                             #if NET5_0_OR_GREATER
                             [ModuleInitializer]
                             #endif
                             public static void Init()
                             {
                                 lock (_lock)
                                 {
                                     if (_initialized)
                                         return;

                     {{registrationCode}}
                                     _initialized = true;
                                 }
                             }

                     #if UNITY_2020_2_OR_NEWER
                     #if UNITY_EDITOR
                             [UnityEditor.InitializeOnLoadMethod]
                             private static void InitEditor() => Init();
                     #endif

                             [UnityEngine.RuntimeInitializeOnLoadMethod(UnityEngine.RuntimeInitializeLoadType.BeforeSceneLoad)]
                             private static void InitRuntime() => Init();
                     #endif
                         }
                     }
                     """;
            spc.AddSource($"{curNamespace}.NinoBuiltInTypes.Registration.g.cs", code);
        }
    }
}