using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

#nullable disable
namespace Nino.UnitTests.Corner
{
    // Only used by the cold start test, so their first use (registration, lazy formatters, pools) happens there.
    [NinoType]
    public abstract class ColdBase
    {
        public int Id;
    }

    [NinoType]
    public class ColdA : ColdBase
    {
        public string Name;
        public List<int> Values;
    }

    [NinoType]
    public sealed class ColdB : ColdBase
    {
        public double D;
        public ColdBase Child;
    }

    [NinoType]
    public interface IColdThing
    {
        int Key { get; set; }
    }

    [NinoType]
    public class ColdThing : IColdThing
    {
        public int Key { get; set; }
        public ColdGeneric<string> Gen;
    }

    [NinoType]
    public class ColdGeneric<T>
    {
        public T Value;
        public T[] Values;
    }

    [NinoType(trackReferences: true)]
    public class ColdTracked
    {
        public int Id;
        public ColdTracked Next;
    }

    [NinoType, NinoPooled]
    public sealed partial class ColdPooled
    {
        public static int Returns;
        public int Id;
        public List<int> Values;

        private static partial IEnumerable<ColdPooled> OnNinoPoolWarmup() => null;

        private partial void OnNinoPoolReturn() => Interlocked.Increment(ref Returns);
    }

    [NinoType]
    public class ColdPooledOwner
    {
        public List<ColdPooled> Items;
    }

    [TestClass]
    public class CornerConcurrencyTests
    {
        private static void RunOnThreads(int threads, Action<int, Barrier> body)
        {
            using var barrier = new Barrier(threads);
            var errors = new ConcurrentQueue<Exception>();
            var workers = Enumerable.Range(0, threads).Select(t => new Thread(() =>
            {
                try
                {
                    body(t, barrier);
                }
                catch (Exception e)
                {
                    errors.Enqueue(e);
                    barrier.RemoveParticipant();
                }
            })).ToList();
            workers.ForEach(w => w.Start());
            foreach (var w in workers)
                Assert.IsTrue(w.Join(TimeSpan.FromSeconds(30)), "worker hung");
            if (!errors.IsEmpty)
                throw new AggregateException(errors);
        }

        [TestMethod]
        public void ColdStartFromManyThreadsAtOnce()
        {
            RunOnThreads(Math.Max(8, Environment.ProcessorCount), (t, barrier) =>
            {
                // Everyone reaches first use together.
                barrier.SignalAndWait();
                for (int i = 0; i < 200; i++)
                {
                    int n = t * 1000 + i;
                    var shapes = new List<ColdBase>
                    {
                        new ColdA { Id = n, Name = "a" + n, Values = new List<int> { n } },
                        new ColdB { Id = -n, D = n, Child = new ColdA { Id = 1 } },
                    };
                    var back = NinoDeserializer.Deserialize<List<ColdBase>>(NinoSerializer.Serialize(shapes));
                    Assert.AreEqual("a" + n, ((ColdA)back[0]).Name);
                    Assert.AreEqual(1, ((ColdB)back[1]).Child.Id);

                    // Through object, the interface and a generic closed per thread.
                    var thing = (ColdThing)NinoDeserializer.Deserialize<object>(NinoSerializer.Serialize<object>(
                        new ColdThing { Key = n, Gen = new ColdGeneric<string> { Value = "g" + n } }));
                    Assert.AreEqual("g" + n, thing.Gen.Value);
                    IColdThing iface = thing;
                    Assert.AreEqual(n, NinoDeserializer.Deserialize<IColdThing>(NinoSerializer.Serialize(iface)).Key);
                    var gen = NinoDeserializer.Deserialize<ColdGeneric<int>>(NinoSerializer.Serialize(
                        new ColdGeneric<int> { Value = n, Values = new[] { t } }));
                    Assert.AreEqual(n, gen.Value);

                    var tracked = new ColdTracked { Id = n };
                    tracked.Next = tracked;
                    var trackedBack = NinoDeserializer.Deserialize<ColdTracked>(NinoSerializer.Serialize(tracked));
                    Assert.AreSame(trackedBack, trackedBack.Next);

                    // Non-generic entry points.
                    var boxed = NinoDeserializer.Deserialize(NinoSerializer.Serialize((object)new ColdA { Id = n }), typeof(ColdA));
                    Assert.AreEqual(n, ((ColdA)boxed).Id);
                }
            });
        }

        [TestMethod]
        public void PooledTypesUnderConcurrentRefDeserialization()
        {
            int returnsBefore = ColdPooled.Returns;
            long expectedReturns = 0;
            RunOnThreads(8, (t, barrier) =>
            {
                var big = NinoSerializer.Serialize(new ColdPooledOwner
                {
                    Items = Enumerable.Range(0, 6).Select(i => new ColdPooled { Id = i, Values = new List<int> { t, i } }).ToList(),
                });
                var small = NinoSerializer.Serialize(new ColdPooledOwner
                    { Items = new List<ColdPooled> { new ColdPooled { Id = 100 + t } } });
                barrier.SignalAndWait();

                var owner = new ColdPooledOwner();
                for (int i = 0; i < 300; i++)
                {
                    NinoDeserializer.DeserializeRef(ref owner, big);
                    Assert.AreEqual(6, owner.Items.Count);
                    Assert.AreEqual(t, owner.Items[5].Values[0]);
                    Assert.AreEqual(6, owner.Items.Select(x => x).Distinct().Count());
                    // Shrinking returns five instances to the pool, which other threads then rent.
                    NinoDeserializer.DeserializeRef(ref owner, small);
                    Assert.AreEqual(100 + t, owner.Items.Single().Id);
                    Interlocked.Add(ref expectedReturns, 5);
                }

                var single = NinoDeserializer.Deserialize<ColdPooled>(NinoSerializer.Serialize(new ColdPooled { Id = t }));
                Assert.AreEqual(t, single.Id);
                single.ReturnToPool();
                Interlocked.Increment(ref expectedReturns);
            });
            Assert.AreEqual(expectedReturns, ColdPooled.Returns - returnsBefore);
        }

        [TestMethod]
        public void SameInstanceSerializedFromManyThreads()
        {
            var shared = new ColdA { Id = 5, Name = "shared", Values = Enumerable.Range(0, 1000).ToList() };
            var expected = NinoSerializer.Serialize<ColdBase>(shared);
            Parallel.For(0, 200, _ =>
            {
                CollectionAssert.AreEqual(expected, NinoSerializer.Serialize<ColdBase>(shared));
                var back = (ColdA)NinoDeserializer.Deserialize<ColdBase>(expected);
                Assert.AreEqual(1000, back.Values.Count);
            });
        }
    }
}
