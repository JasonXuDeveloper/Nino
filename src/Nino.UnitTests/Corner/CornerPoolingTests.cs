using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

#nullable disable
namespace Nino.UnitTests.Corner
{
    internal sealed class RefComparer : IEqualityComparer<object>
    {
        public static readonly RefComparer Instance = new RefComparer();
        public new bool Equals(object x, object y) => ReferenceEquals(x, y);
        public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
    }

    /// <summary>A user-side object pool (like ET's ObjectPool) that detects double returns and use after return.</summary>
    internal static class UserPool
    {
        private static readonly Dictionary<Type, Stack<object>> Free = new Dictionary<Type, Stack<object>>();
        private static readonly HashSet<object> InPool = new HashSet<object>(RefComparer.Instance);
        public static int Returns;

        public static T Rent<T>() where T : class, new()
        {
            if (Free.TryGetValue(typeof(T), out var stack) && stack.Count > 0)
            {
                var item = stack.Pop();
                InPool.Remove(item);
                return (T)item;
            }

            return new T();
        }

        public static void Return(object item)
        {
            if (item == null)
                return;
            if (!InPool.Add(item))
                throw new InvalidOperationException("double return of " + item);
            Returns++;
            // Dispose what the item owns, as ET's Dispose would; keep the stale scalar values.
            switch (item)
            {
                case PComp comp:
                    Return(comp.Tags);
                    comp.Tags = null;
                    break;
                case List<int> ints:
                    ints.Clear();
                    break;
                case List<PComp> comps:
                    foreach (var c in comps)
                        Return(c);
                    comps.Clear();
                    break;
            }

            if (!Free.TryGetValue(item.GetType(), out var stack))
                Free[item.GetType()] = stack = new Stack<object>();
            stack.Push(item);
        }

        public static bool IsInPool(object item) => InPool.Contains(item);

        public static void Reset()
        {
            Free.Clear();
            InPool.Clear();
            Returns = 0;
        }
    }

    [NinoType]
    public class PCompBase
    {
        public static int Created;
        public int Hp;

        [NinoFactory]
        public static PCompBase Create()
        {
            Created++;
            return UserPool.Rent<PCompBase>();
        }
    }

    [NinoType]
    public class PComp : PCompBase
    {
        public new static int Created;
        public List<int> Tags;

        [NinoFactory]
        public new static PComp Create()
        {
            Created++;
            var comp = UserPool.Rent<PComp>();
            comp.Tags = UserPool.Rent<List<int>>();
            return comp;
        }
    }

    [NinoType]
    public class PCompAlt : PCompBase
    {
        public new static int Created;
        public string S;

        [NinoFactory]
        public static PCompAlt Create(bool isFromPool = false)
        {
            Created++;
            return UserPool.Rent<PCompAlt>();
        }
    }

    [NinoType]
    public class PUnit
    {
        public static int Created;

        public long Id;
        public List<int> Buffs;
        public List<PComp> Comps;
        public PComp[] Slots;
        public PComp Main;
        public PCompBase Poly;
        public Dictionary<int, PComp> ById;

        [NinoFactory]
        public static PUnit Create(bool isFromPool = false)
        {
            Created++;
            var unit = UserPool.Rent<PUnit>();
            unit.Buffs = UserPool.Rent<List<int>>();
            unit.Comps = UserPool.Rent<List<PComp>>();
            return unit;
        }
    }

    [TestClass]
    [DoNotParallelize]
    public class CornerPoolingTests
    {
        [TestInitialize]
        public void Init()
        {
            UserPool.Reset();
            NinoSettings.SetRecycler<List<int>>(UserPool.Return);
            NinoSettings.SetRecycler<List<PComp>>(UserPool.Return);
            NinoSettings.SetRecycler<PComp>(UserPool.Return);
            NinoSettings.SetRecycler<PCompBase>(UserPool.Return);
            NinoSettings.SetRecycler<PComp[]>(array =>
            {
                foreach (var c in array)
                    UserPool.Return(c);
            });
            NinoSettings.SetRecycler<Dictionary<int, PComp>>(map =>
            {
                foreach (var c in map.Values)
                    UserPool.Return(c);
            });
        }

        [TestCleanup]
        public void Cleanup()
        {
            NinoSettings.SetRecycler<List<int>>(null);
            NinoSettings.SetRecycler<List<PComp>>(null);
            NinoSettings.SetRecycler<PComp>(null);
            NinoSettings.SetRecycler<PCompBase>(null);
            NinoSettings.SetRecycler<PComp[]>(null);
            NinoSettings.SetRecycler<Dictionary<int, PComp>>(null);
            UserPool.Reset();
        }

        private static PComp Comp(int hp, params int[] tags) =>
            new PComp { Hp = hp, Tags = tags == null ? null : new List<int>(tags) };

        [TestMethod]
        public void NullDataReturnsFactoryRentedListsOnceInNestedGraph()
        {
            var unit = PUnit.Create();
            var buffs = unit.Buffs;
            var comps = unit.Comps;
            // A first payload fills the graph with factory-created components.
            NinoDeserializer.Deserialize(NinoSerializer.Serialize(new PUnit
                { Buffs = new List<int> { 1 }, Comps = new List<PComp> { Comp(1, 1), Comp(2, 2) } }), ref unit);
            Assert.AreSame(buffs, unit.Buffs);
            Assert.AreSame(comps, unit.Comps);
            var c0 = unit.Comps[0];
            var c1 = unit.Comps[1];
            var c0Tags = c0.Tags;
            var c1Tags = c1.Tags;
            Assert.AreEqual(0, UserPool.Returns);

            // Then a payload where the nested list is null and the component list shrinks.
            NinoDeserializer.Deserialize(NinoSerializer.Serialize(new PUnit
                { Buffs = null, Comps = new List<PComp> { Comp(3, null) } }), ref unit);
            Assert.IsNull(unit.Buffs);
            Assert.AreSame(c0, unit.Comps.Single());
            Assert.IsNull(c0.Tags);
            Assert.IsTrue(UserPool.IsInPool(buffs));
            Assert.IsTrue(UserPool.IsInPool(c0Tags));
            Assert.IsTrue(UserPool.IsInPool(c1));
            Assert.IsTrue(UserPool.IsInPool(c1Tags));
            Assert.IsFalse(UserPool.IsInPool(c0));
            Assert.AreEqual(4, UserPool.Returns);

            // Everything null: the component list goes back with its element.
            NinoDeserializer.Deserialize(NinoSerializer.Serialize(new PUnit()), ref unit);
            Assert.IsNull(unit.Comps);
            Assert.IsTrue(UserPool.IsInPool(comps));
            Assert.IsTrue(UserPool.IsInPool(c0));
            Assert.AreEqual(6, UserPool.Returns);
        }

        [TestMethod]
        public void PooledInstancesAreReusedWithStaleStateOverwritten()
        {
            // Put a component with stale state into the pool; the next factory call returns it.
            var stale = new PComp { Hp = 99 };
            UserPool.Return(stale);
            int created = PComp.Created;
            var unit = NinoDeserializer.Deserialize<PUnit>(NinoSerializer.Serialize(new PUnit { Main = Comp(5, 1, 2) }));
            Assert.AreEqual(created + 1, PComp.Created);
            Assert.AreSame(stale, unit.Main);
            Assert.AreEqual(5, unit.Main.Hp);
            CollectionAssert.AreEqual(new[] { 1, 2 }, unit.Main.Tags);
        }

        [TestMethod]
        public void ArraysShrinkAndGrowWithFactoryElements()
        {
            var unit = new PUnit();
            NinoDeserializer.Deserialize(
                NinoSerializer.Serialize(new PUnit { Slots = new[] { Comp(1), Comp(2), Comp(3) } }), ref unit);
            var slots = unit.Slots;
            var a = slots[0];
            var b = slots[1];
            var c = slots[2];

            // Shrink: the kept element moves, the dropped ones are returned once each.
            NinoDeserializer.Deserialize(NinoSerializer.Serialize(new PUnit { Slots = new[] { Comp(7) } }), ref unit);
            Assert.AreSame(a, unit.Slots[0]);
            Assert.AreEqual(7, a.Hp);
            Assert.IsTrue(UserPool.IsInPool(b) && UserPool.IsInPool(c));
            Assert.IsFalse(UserPool.IsInPool(a));
            Assert.IsTrue(slots.All(s => s == null), "the replaced array no longer references its elements");

            // Grow: the kept element stays, the new ones come from the factory (the pool, here).
            int created = PComp.Created;
            NinoDeserializer.Deserialize(
                NinoSerializer.Serialize(new PUnit { Slots = new[] { Comp(1), null, Comp(3), Comp(4) } }), ref unit);
            Assert.AreEqual(created + 2, PComp.Created);
            Assert.AreSame(a, unit.Slots[0]);
            Assert.IsNull(unit.Slots[1]);
            Assert.AreEqual(4, unit.Slots[3].Hp);
            Assert.IsTrue(new[] { b, c }.Contains(unit.Slots[2]) || new[] { b, c }.Contains(unit.Slots[3]));
            Assert.IsFalse(unit.Slots.Any(s => s != null && UserPool.IsInPool(s)));

            // Empty array.
            var before = unit.Slots.Where(s => s != null).ToList();
            NinoDeserializer.Deserialize(NinoSerializer.Serialize(new PUnit { Slots = new PComp[0] }), ref unit);
            Assert.AreEqual(0, unit.Slots.Length);
            Assert.IsTrue(before.All(UserPool.IsInPool));
        }

        [TestMethod]
        public void PolymorphicMemberReplacedByAnotherSubtypeIsRecycledOnce()
        {
            var unit = new PUnit();
            NinoDeserializer.Deserialize(NinoSerializer.Serialize(new PUnit { Poly = Comp(1, 1) }), ref unit);
            var first = (PComp)unit.Poly;
            var tags = first.Tags;
            int returns = UserPool.Returns;

            int alt = PCompAlt.Created;
            NinoDeserializer.Deserialize(NinoSerializer.Serialize(new PUnit { Poly = new PCompAlt { Hp = 2, S = "s" } }), ref unit);
            Assert.AreEqual(alt + 1, PCompAlt.Created);
            Assert.AreEqual("s", ((PCompAlt)unit.Poly).S);
            Assert.IsTrue(UserPool.IsInPool(first));
            Assert.IsTrue(UserPool.IsInPool(tags));
            Assert.AreEqual(returns + 2, UserPool.Returns);

            // Base type, then back to the first subtype, which comes out of the pool.
            NinoDeserializer.Deserialize(NinoSerializer.Serialize(new PUnit { Poly = new PCompBase { Hp = 3 } }), ref unit);
            Assert.AreEqual(typeof(PCompBase), unit.Poly.GetType());
            NinoDeserializer.Deserialize(NinoSerializer.Serialize(new PUnit { Poly = Comp(4, 4) }), ref unit);
            Assert.AreSame(first, unit.Poly);
            Assert.AreEqual(4, first.Hp);
            CollectionAssert.AreEqual(new[] { 4 }, first.Tags);
        }

        [TestMethod]
        public void DictionaryValuesOfRemovedKeysAreRecycledOnce()
        {
            var unit = new PUnit();
            NinoDeserializer.Deserialize(NinoSerializer.Serialize(new PUnit
                { ById = new Dictionary<int, PComp> { [1] = Comp(1), [2] = Comp(2), [3] = null } }), ref unit);
            var one = unit.ById[1];
            var two = unit.ById[2];
            NinoDeserializer.Deserialize(NinoSerializer.Serialize(new PUnit
                { ById = new Dictionary<int, PComp> { [1] = Comp(10), [4] = Comp(4) } }), ref unit);
            Assert.AreSame(one, unit.ById[1]);
            Assert.AreEqual(10, one.Hp);
            Assert.IsTrue(UserPool.IsInPool(two));
            Assert.IsFalse(unit.ById.ContainsKey(3));
        }

        // ---- randomized: nothing reachable is in the pool, nothing dropped is leaked, nothing is returned twice ----

        private static List<object> Reachable(PUnit unit)
        {
            var result = new List<object>();

            void AddComp(PCompBase c)
            {
                if (c == null)
                    return;
                result.Add(c);
                if (c is PComp p && p.Tags != null)
                    result.Add(p.Tags);
            }

            if (unit.Buffs != null)
                result.Add(unit.Buffs);
            if (unit.Comps != null)
            {
                result.Add(unit.Comps);
                unit.Comps.ForEach(AddComp);
            }

            if (unit.Slots != null)
                foreach (var s in unit.Slots)
                    AddComp(s);
            AddComp(unit.Main);
            AddComp(unit.Poly);
            if (unit.ById != null)
                foreach (var v in unit.ById.Values)
                    AddComp(v);
            return result;
        }

        private static PComp RandomComp(Random r) => r.Next(5) == 0
            ? null
            : new PComp { Hp = r.Next(100), Tags = r.Next(3) == 0 ? null : Enumerable.Range(0, r.Next(4)).ToList() };

        private static PUnit RandomUnit(Random r)
        {
            List<T> List<T>(Func<T> make) => r.Next(4) == 0 ? null : Enumerable.Range(0, r.Next(5)).Select(_ => make()).ToList();
            return new PUnit
            {
                Id = r.Next(),
                Buffs = List(() => r.Next(10)),
                Comps = List(() => RandomComp(r)),
                Slots = List(() => RandomComp(r))?.ToArray(),
                Main = RandomComp(r),
                Poly = r.Next(4) switch
                {
                    0 => null,
                    1 => RandomComp(r),
                    2 => new PCompAlt { Hp = r.Next(), S = "s" + r.Next(3) },
                    _ => new PCompBase { Hp = r.Next() },
                },
                ById = r.Next(4) == 0
                    ? null
                    : Enumerable.Range(0, 6).Where(_ => r.Next(2) == 0).ToDictionary(k => k, _ => RandomComp(r)),
            };
        }

        private static void AssertSame(PComp expected, PComp actual)
        {
            Assert.AreEqual(expected == null, actual == null);
            if (expected == null)
                return;
            Assert.AreEqual(expected.Hp, actual.Hp);
            if (expected.Tags == null)
                Assert.IsNull(actual.Tags);
            else
                CollectionAssert.AreEqual(expected.Tags, actual.Tags);
        }

        private static void AssertGraph(PUnit expected, PUnit actual)
        {
            Assert.AreEqual(expected.Id, actual.Id);
            if (expected.Buffs == null) Assert.IsNull(actual.Buffs);
            else CollectionAssert.AreEqual(expected.Buffs, actual.Buffs);
            Assert.AreEqual(expected.Comps?.Count, actual.Comps?.Count);
            for (int i = 0; i < (expected.Comps?.Count ?? 0); i++)
                AssertSame(expected.Comps[i], actual.Comps[i]);
            Assert.AreEqual(expected.Slots?.Length, actual.Slots?.Length);
            for (int i = 0; i < (expected.Slots?.Length ?? 0); i++)
                AssertSame(expected.Slots[i], actual.Slots[i]);
            AssertSame(expected.Main, actual.Main);
            Assert.AreEqual(expected.Poly?.GetType(), actual.Poly?.GetType());
            if (expected.Poly is PComp pc)
                AssertSame(pc, (PComp)actual.Poly);
            else if (expected.Poly != null)
                Assert.AreEqual(expected.Poly.Hp, actual.Poly.Hp);
            if (expected.Poly is PCompAlt alt)
                Assert.AreEqual(alt.S, ((PCompAlt)actual.Poly).S);
            Assert.AreEqual(expected.ById?.Count, actual.ById?.Count);
            if (expected.ById != null)
                foreach (var pair in expected.ById)
                    AssertSame(pair.Value, actual.ById[pair.Key]);
        }

        [TestMethod]
        public void RandomGraphsNeverLeakOrDoubleReturnPooledObjects()
        {
            var random = new Random(1234);
            var unit = PUnit.Create();
            for (int i = 0; i < 300; i++)
            {
                var source = RandomUnit(random);
                var before = Reachable(unit);
                NinoDeserializer.Deserialize(NinoSerializer.Serialize(source), ref unit);
                AssertGraph(source, unit);

                var after = new HashSet<object>(Reachable(unit), RefComparer.Instance);
                foreach (var item in after)
                    Assert.IsFalse(UserPool.IsInPool(item), $"iteration {i}: {item} is in use and in the pool");
                foreach (var item in before)
                {
                    if (!after.Contains(item))
                        Assert.IsTrue(UserPool.IsInPool(item), $"iteration {i}: dropped {item} was leaked");
                }
            }
        }
    }
}
