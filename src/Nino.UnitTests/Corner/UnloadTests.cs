#if NET8_0_OR_GREATER
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
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

        /// <summary>
        /// A removed assembly stays in AppDomain.GetAssemblies until it is collected: a scan of every assembly in
        /// between (a read of an unknown type id) must not register it again, which would pin it.
        /// </summary>
        [TestMethod]
        public void RemovedAssemblyIsNotRegisteredAgain() => AssertUnloads(removeFirst: true, scanAfterRemove: true);

        /// <summary>
        /// An unload followed by a load leaves the number of assemblies unchanged: the scan of every assembly must
        /// still see the new one (here, to read a type id only its registry knows).
        /// </summary>
        [TestMethod]
        [DoNotParallelize]
        public void ScanSeesAssemblyLoadedAfterAnUnload()
        {
            var compiled = CornerCompiler.Compile(Source, load: false);
            Assert.AreEqual(0, compiled.Errors.Length, compiled.ErrorText);

            // A Move written through object: its type id, then X and Y.
            var bytes = new byte[12];
            BitConverter.TryWriteBytes(bytes.AsSpan(0), NinoTypeId.Compute("Unloadable.Move"));
            BitConverter.TryWriteBytes(bytes.AsSpan(4), 3f);

            // Scan with a collectible dynamic assembly loaded (which the scan skips, so nothing holds or watches
            // it), let it be collected, then load the reading assembly: the count is the same as at the scan.
            Collect(DefineAndScan(scan: false)); // first loads what defining and collecting need
            var filler = DefineAndScan(scan: true);
            Collect(filler);
            Assert.IsFalse(filler.IsAlive, "the dynamic assembly was not collected");

            var context = new AssemblyLoadContext("nino-reload", isCollectible: true);
            try
            {
                var assembly = context.LoadFromStream(new MemoryStream(compiled.Image));
                var value = NinoDeserializer.Deserialize<object>(bytes);
                Assert.AreEqual("Unloadable.Move", value.GetType().FullName);
                Assert.AreEqual(3f, value.GetType().GetField("X")!.GetValue(value));
                Assert.AreSame(assembly, value.GetType().Assembly);
                NinoRegistry.Remove(assembly);
            }
            finally
            {
                context.Unload();
            }
        }

        /// <summary>
        /// A type id no loaded assembly declares is not searched for again while nothing changes (bad or stale data
        /// read repeatedly), and is found once an assembly declaring it loads.
        /// </summary>
        [TestMethod]
        [DoNotParallelize]
        public void UnknownIdIsFoundOnceItsAssemblyLoads()
        {
            var compiled = CornerCompiler.Compile(Source.Replace("Unloadable", "LoadedLater"), load: false);
            Assert.AreEqual(0, compiled.Errors.Length, compiled.ErrorText);

            int id = NinoTypeId.Compute("LoadedLater.Move");
            var bytes = new byte[12];
            BitConverter.TryWriteBytes(bytes.AsSpan(0), id);
            BitConverter.TryWriteBytes(bytes.AsSpan(4), 3f);

            Assert.ThrowsExactly<InvalidOperationException>(() => NinoDeserializer.Deserialize<object>(bytes));
            Assert.ThrowsExactly<InvalidOperationException>(() => NinoDeserializer.Deserialize<object>(bytes));
            Assert.IsTrue(NinoUnknownIds.Contains(id, NinoUnknownIds.Now()), "the miss is remembered");

            var context = new AssemblyLoadContext("nino-later", isCollectible: true);
            try
            {
                var assembly = context.LoadFromStream(new MemoryStream(compiled.Image));
                Assert.IsFalse(NinoUnknownIds.Contains(id, NinoUnknownIds.Now()), "a load forgets the misses");
                var value = NinoDeserializer.Deserialize<object>(bytes);
                Assert.AreEqual("LoadedLater.Move", value.GetType().FullName);
                Assert.AreSame(assembly, value.GetType().Assembly);
                NinoRegistry.Remove(assembly);
            }
            finally
            {
                context.Unload();
            }

            // A registration forgets them too.
            int other = NinoTypeId.Compute("LoadedLater.Nowhere");
            BitConverter.TryWriteBytes(bytes.AsSpan(0), other);
            Assert.ThrowsExactly<InvalidOperationException>(() => NinoDeserializer.Deserialize<object>(bytes));
            Assert.IsTrue(NinoUnknownIds.Contains(other, NinoUnknownIds.Now()));
            NinoRegistry.Changed();
            Assert.IsFalse(NinoUnknownIds.Contains(other, NinoUnknownIds.Now()));
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference DefineAndScan(bool scan)
        {
            var dynamic = AssemblyBuilder.DefineDynamicAssembly(new AssemblyName("NinoFiller" + Guid.NewGuid().ToString("N")),
                AssemblyBuilderAccess.RunAndCollect);
            dynamic.DefineDynamicModule("Filler").DefineType("Filler.Nothing").CreateType();
            if (scan)
                NinoRegistry.EnsureAllInitialized();
            return new WeakReference(dynamic);
        }

        private static void Collect(WeakReference context)
        {
            for (int i = 0; i < 20 && context.IsAlive; i++)
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        }

        private static void AssertUnloads(bool removeFirst, bool scanAfterRemove = false)
        {
            var compiled = CornerCompiler.Compile(Source, load: false);
            Assert.AreEqual(0, compiled.Errors.Length, compiled.ErrorText);

            var context = LoadUseAndUnload(compiled.Image, removeFirst, scanAfterRemove);
            Collect(context);

            Assert.IsFalse(context.IsAlive, "the collectible context was not collected");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference LoadUseAndUnload(byte[] image, bool removeFirst, bool scanAfterRemove)
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
            if (scanAfterRemove)
            {
                // A miss that scans every loaded assembly, the unloading one included.
                Assert.ThrowsExactly<InvalidOperationException>(() =>
                    NinoDeserializer.Deserialize<object>(new byte[] { 1, 2, 3, 4 }));
                NinoRegistry.EnsureAllInitialized();
            }

            return new WeakReference(context);
        }
    }
}
#endif
