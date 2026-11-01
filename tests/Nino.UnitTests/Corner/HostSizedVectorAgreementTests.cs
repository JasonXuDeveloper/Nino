#if NET8_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;
using Nino.Core.Internal;

#nullable disable
namespace Nino.UnitTests
{
    /// <summary>
    /// The generator (RawRules' search for a <c>Vector&lt;T&gt;</c>, behind NINO029) and Nino.Core
    /// (<see cref="NinoReflection.HostSizedVectorIn"/>, behind the refusal at run time) decide each on its own which
    /// types hold a <c>Vector&lt;T&gt;</c>: one from symbols, the other by reflection. Both are asked about the same
    /// shapes. They differ by design on <c>[NinoType]</c>s only: the generator names the vector a generic one's type
    /// argument holds, Nino.Core leaves the type to its generated formatter, which refuses the argument when it first
    /// resolves its formatter; so for those, a write of an instance must throw <see cref="NotSupportedException"/>.
    /// Nino.Core walks base types of classes only (a struct's is <c>ValueType</c>) and skips the fields of
    /// <c>Nullable</c> (its type argument is searched instead), which loses nothing.
    /// </summary>
    [TestClass]
    public class HostSizedVectorAgreementTests
    {
        private const string Source = @"
using System.Collections;
using System.Collections.Generic;
using System.Numerics;
using Nino.Core;

namespace Vectors
{
    public struct Raw { public int A; public Vector<int> V; }
    public struct Wrap { public Raw R; }
    public struct HoldsNullable { public Vector<int>? V; }
    public struct Chain { public int A; public List<Chain> Rest; public Vector<int> V; }
    public struct Clean { public int A; public Vector4 V; }
    public class VectorList : List<Vector<int>> { }
    public class Derived : VectorList { }
    public class Bag : IEnumerable<Vector<long>>
    {
        public void Add(Vector<long> item) { }
        public IEnumerator<Vector<long>> GetEnumerator() { yield break; }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
    public struct StructBag : IEnumerable<Vector<int>>
    {
        public IEnumerator<Vector<int>> GetEnumerator() { yield break; }
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
    public class PlainClass { public Vector<int> V; }
    [NinoType] public class Holder { public int A; public Vector<int> V; }
    [NinoType] public struct NinoStruct { public int A; public Vector<int> V; }
    [NinoType] public class Box<T> { public T V; }

    public class Probe
    {
        public Vector<int> F0; public Vector<int>[] F1; public Vector<int>? F2; public List<Vector<int>> F3;
        public Dictionary<int, Vector<float>> F4; public (int, Vector<int>) F5; public KeyValuePair<int, Vector<int>> F6;
        public Raw F7; public Wrap F8; public HoldsNullable F9; public Chain F10; public Clean F11; public VectorList F12;
        public Derived F13; public Bag F14; public StructBag F15; public PlainClass F16; public Holder F17;
        public NinoStruct F18; public Box<Vector<int>> F19; public Box<int> F20; public Box<List<Vector<int>>> F21;
        public System.Runtime.Intrinsics.Vector128<int> F22; public int[] F23; public List<int> F24; public Raw[] F25;
        public Vector<int>[,] F26; public List<Clean> F27; public Box<Clean> F28;
    }
}";

        [TestMethod]
        public void GeneratorAndRuntimeAgreeOnHostSizedVectors()
        {
            var result = CornerCompiler.Compile(Source);
            Assert.AreEqual(0, result.Errors.Length, result.ErrorText);
            var compilation = result.Compilation;
            var probe = compilation.GetTypeByMetadataName("Vectors.Probe");
            Assert.IsNotNull(probe);

            // TypeFacts.For(compilation, options).Raw: the generator's rules for this compilation.
            var generator = typeof(Generator.Generation.NinoGenerator).Assembly;
            var factsType = generator.GetType("Nino.Generator.Generation.TypeFacts", true);
            var facts = factsType.GetMethod("For")!.Invoke(null,
                new object[] { compilation, compilation.SyntaxTrees.First().Options });
            var raw = factsType.GetProperty("Raw")!.GetValue(facts);
            var search = raw.GetType().GetMethod("HostSizedVectorIn", new[] { typeof(ITypeSymbol) }) ??
                         raw.GetType().GetMethod("VariableVectorIn", new[] { typeof(ITypeSymbol) });
            Assert.IsNotNull(search, "RawRules' search for a Vector<T>");

            var runtimeProbe = result.Assembly.GetType("Vectors.Probe", true);
            var disagreements = new List<string>();
            int held = 0, fields = 0;
            foreach (var field in probe.GetMembers().OfType<IFieldSymbol>())
            {
                bool generated = search.Invoke(raw, new object[] { field.Type }) != null;
                var type = runtimeProbe.GetField(field.Name)!.FieldType;
                bool runtime = NinoReflection.HostSizedVectorIn(type) != null;
                // A [NinoType] is refused when its generated formatter resolves the vector's.
                if (!runtime && IsNinoType(type))
                    runtime = WriteThrows(type);
                fields++;
                if (generated)
                    held++;
                if (generated != runtime)
                    disagreements.Add($"{field.Name} {field.Type.ToDisplayString()}: generator {generated}, runtime {runtime}");
            }

            Assert.AreEqual(0, disagreements.Count, string.Join("\n", disagreements));
            Assert.IsTrue(held > 10 && fields - held > 5, $"{held} of {fields} hold a Vector<T>");
        }

        private static bool IsNinoType(Type type) =>
            (type.IsGenericType ? type.GetGenericTypeDefinition() : type).IsDefined(typeof(NinoTypeAttribute), false);

        private static bool WriteThrows(Type type)
        {
            var write = typeof(HostSizedVectorAgreementTests).GetMethod(nameof(Write), BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(type);
            try
            {
                write.Invoke(null, new[] { Activator.CreateInstance(type) });
                return false;
            }
            catch (TargetInvocationException e) when (e.InnerException is NotSupportedException)
            {
                return true;
            }
        }

        private static byte[] Write<T>(object value) => NinoSerializer.Serialize((T)value);
    }
}
#endif
