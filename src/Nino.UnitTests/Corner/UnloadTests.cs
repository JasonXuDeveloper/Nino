#if NET8_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

#nullable disable
namespace Nino.UnitTests.Corner
{
    /// <summary>
    /// An assembly with [NinoType]s loaded into a collectible AssemblyLoadContext unloads once it is done with
    /// (issue #199): Nino.Core keeps no reference into it after NinoRegistry.Remove, which runs when the context
    /// starts unloading. Both tests load the same types: the second one is a reload, as a hot-reloading server does
    /// (without the removal, its types would collide with the first context's ids).
    /// </summary>
    [TestClass]
    public class UnloadTests
    {
        private const string Source = @"
using System.Collections.Generic;
using Nino.Core;

namespace Unloadable
{
    [NinoType]
    public abstract class Shape { public int Id; }

    [NinoType]
    public class Circle : Shape { public float Radius; public List<Shape> Children = new List<Shape>(); }

    [NinoType]
    public struct Move { public float X; public float Y; }

    public static class Use
    {
        public static bool RoundTrip()
        {
            var circle = new Circle { Id = 1, Radius = 2.5f };
            circle.Children.Add(new Circle { Id = 2, Radius = 1 });
            var shape = NinoDeserializer.Deserialize<Shape>(NinoSerializer.Serialize<Shape>(circle)) as Circle;
            var boxed = (Circle)NinoDeserializer.Deserialize<object>(NinoSerializer.Serialize<object>(circle));
            var moves = NinoDeserializer.Deserialize<Move[]>(NinoSerializer.Serialize(new[] { new Move { X = 3 } }));
            return shape != null && shape.Radius == 2.5f && ((Circle)shape.Children[0]).Id == 2 &&
                   boxed.Children.Count == 1 && moves[0].X == 3 && NinoSerializer.GetFixedSize<Move>() == 8;
        }
    }
}";

#if !NINO_CORE_NETSTANDARD // The Unloading hook is in the .NET 8+ builds of Nino.Core only; the others need Remove.
        [TestMethod]
        public void CollectibleContextUnloads() => AssertUnloads(removeFirst: false);
#endif

        [TestMethod]
        public void CollectibleContextUnloadsAfterRemove() => AssertUnloads(removeFirst: true);

        private static void AssertUnloads(bool removeFirst)
        {
            var compiled = CornerCompiler.Compile(Source, load: false);
            Assert.AreEqual(0, compiled.Errors.Length, compiled.ErrorText);

            var context = LoadUseAndUnload(compiled.Image, removeFirst);
            for (int i = 0; i < 20 && context.IsAlive; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }

            Assert.IsFalse(context.IsAlive, "the collectible context was not collected");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference LoadUseAndUnload(byte[] image, bool removeFirst)
        {
            var context = new AssemblyLoadContext("nino-unload", isCollectible: true);
            var assembly = context.LoadFromStream(new MemoryStream(image));
            var use = assembly.GetType("Unloadable.Use")!;
            Assert.IsTrue((bool)use.GetMethod("RoundTrip")!.Invoke(null, null)!);

            // Through Nino's runtime-type APIs too: the boxed formatters, the object registry and the type ids.
            var circleType = assembly.GetType("Unloadable.Circle")!;
            var circle = Activator.CreateInstance(circleType);
            var bytes = NinoSerializer.SerializeBoxed(circle, circleType);
            Assert.IsInstanceOfType(NinoDeserializer.DeserializeBoxed(bytes, circleType), circleType);
            NinoTypeId.Of(circleType);

            if (removeFirst)
                NinoRegistry.Remove(assembly);
            context.Unload();
            return new WeakReference(context);
        }
    }
}
#endif
