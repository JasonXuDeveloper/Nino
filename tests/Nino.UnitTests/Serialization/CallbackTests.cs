using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;
using Nino.UnitTests.Subset.Callbacks;

#nullable disable
namespace Nino.UnitTests;

/// <summary>What the callbacks below were called with, in order, per thread (test classes run side by side).</summary>
internal static class CallbackLog
{
    [ThreadStatic] private static List<string> t_events;

    public static List<string> Events => t_events ??= new List<string>();

    public static void Add(string e) => Events.Add(e);

    public static string[] Take()
    {
        var events = Events.ToArray();
        Events.Clear();
        return events;
    }
}

[NinoType(format: NinoFormat.VersionTolerant)]
public class CallbackBase
{
    public int A;

    [NinoOnSerializing]
    internal void BaseSerializing() => CallbackLog.Add("Base.Serializing");

    [NinoOnSerialized]
    internal void BaseSerialized() => CallbackLog.Add("Base.Serialized");

    [NinoOnDeserializing]
    internal static void BaseDeserializing() => CallbackLog.Add("Base.Deserializing(static)");

    [NinoOnDeserialized]
    internal void BaseDeserialized() => CallbackLog.Add($"Base.Deserialized A={A}");
}

// Takes [NinoType] from its base.
public class CallbackDerived : CallbackBase
{
    public string B;

    [NinoOnSerializing]
    public void DerivedSerializing() => CallbackLog.Add("Derived.Serializing");

    [NinoOnDeserializing]
    public void DerivedDeserializing() => CallbackLog.Add($"Derived.Deserializing B={B ?? "null"}");

    [NinoOnDeserialized]
    public void DerivedDeserialized() => CallbackLog.Add($"Derived.Deserialized B={B}");
}

// A virtual callback: the override is what runs, once.
[NinoType(format: NinoFormat.VersionTolerant)]
public abstract class CallbackShape
{
    public int Id;

    [NinoOnDeserialized]
    protected internal virtual void Loaded() => CallbackLog.Add("Shape.Loaded");
}

public sealed class CallbackCircle : CallbackShape
{
    public float Radius;

    protected internal override void Loaded() => CallbackLog.Add($"Circle.Loaded {Radius}");
}

// A derived callback hiding its base's name: both run, the base's through a cast.
[NinoType(format: NinoFormat.VersionTolerant)]
public class CallbackHiddenBase
{
    public int A;

    [NinoOnSerializing]
    internal void Prepare() => CallbackLog.Add("HiddenBase.Prepare");
}

public class CallbackHidden : CallbackHiddenBase
{
    [NinoOnSerializing]
    internal new void Prepare() => CallbackLog.Add("Hidden.Prepare");
}

[NinoType(format: NinoFormat.VersionTolerant)]
public struct CallbackStruct
{
    public int X;

    // Not serialized: set by the callbacks.
    [NinoIgnore] public int Doubled;

    [NinoOnSerializing]
    internal void Serializing() => CallbackLog.Add($"Struct.Serializing {X}");

    [NinoOnDeserialized]
    internal void Deserialized()
    {
        Doubled = X * 2;
        CallbackLog.Add($"Struct.Deserialized {X}");
    }
}

[NinoType(format: NinoFormat.VersionTolerant)]
public class CallbackHolder
{
    public CallbackBase Item;
    public List<CallbackBase> Items;
    public CallbackStruct[] Structs;
    public Dictionary<int, CallbackStruct> ById;
}

// Created from the members it reads: only a static [NinoOnDeserializing] can run.
[NinoType(format: NinoFormat.VersionTolerant)]
public record CallbackRecord(int A, string B)
{
    [NinoOnDeserializing]
    internal static void Deserializing() => CallbackLog.Add("Record.Deserializing(static)");

    [NinoOnDeserialized]
    internal void Deserialized() => CallbackLog.Add($"Record.Deserialized {A} {B}");
}

[NinoType(format: NinoFormat.Keyed)]
public sealed class CallbackKeyed
{
    public int A;
    public string B;

    [NinoOnSerialized]
    internal void Serialized() => CallbackLog.Add("Keyed.Serialized");

    [NinoOnDeserialized]
    internal void Deserialized() => CallbackLog.Add($"Keyed.Deserialized {A} {B}");
}

[NinoType(format: NinoFormat.VersionTolerant)]
public class CallbackBox<T>
{
    public T Value;

    [NinoOnDeserializing]
    internal static void Deserializing() => CallbackLog.Add($"Box<{typeof(T).Name}>.Deserializing(static)");

    [NinoOnDeserialized]
    internal void Deserialized() => CallbackLog.Add($"Box<{typeof(T).Name}>.Deserialized {Value}");
}

// A sealed class read by its __Top twin at the top level, its new instances by a body of their own (FreshRead).
[NinoType(format: NinoFormat.VersionTolerant)]
public sealed class CallbackSealed
{
    public int A;
    public int[] Values;

    [NinoOnSerializing]
    internal void Serializing() => CallbackLog.Add("Sealed.Serializing");

    [NinoOnDeserializing]
    internal void Deserializing() => CallbackLog.Add($"Sealed.Deserializing {Values?.Length ?? -1}");

    [NinoOnDeserialized]
    internal void Deserialized() => CallbackLog.Add($"Sealed.Deserialized {Values.Length}");
}

// Holds the sealed class: read through the __Shallow twins when the depth leaves room.
[NinoType(format: NinoFormat.VersionTolerant)]
public sealed class CallbackSealedHolder
{
    public CallbackSealed Item;

    [NinoOnSerialized]
    internal void Serialized() => CallbackLog.Add("SealedHolder.Serialized");

    [NinoOnDeserialized]
    internal void Deserialized() => CallbackLog.Add("SealedHolder.Deserialized");
}

[NinoType(format: NinoFormat.VersionTolerant, trackReferences: true)]
public class CallbackNode
{
    public string Name;
    public CallbackNode Next;

    [NinoOnSerializing]
    internal void Serializing() => CallbackLog.Add($"Node.Serializing {Name}");

    [NinoOnDeserialized]
    internal void Deserialized() => CallbackLog.Add($"Node.Deserialized {Name}");
}

// Nested deeper than the bodies read inline (NinoDepth.Inline): the deep ones are read later.
[NinoType(format: NinoFormat.VersionTolerant)]
public sealed class CallbackLink
{
    public int Index;
    public CallbackLink Next;

    [NinoOnDeserialized]
    internal void Deserialized() => CallbackLog.Add(Index.ToString(System.Globalization.CultureInfo.InvariantCulture));
}

[NinoType(format: NinoFormat.VersionTolerant)]
public class CallbackThrows
{
    public int A;

    [NinoIgnore] public bool ThrowOnSerializing, ThrowOnDeserialized;

    [NinoOnSerializing]
    internal void Serializing()
    {
        if (ThrowOnSerializing)
            throw new InvalidOperationException("thrown by a callback");
    }

    [NinoOnSerialized]
    internal void Serialized() => CallbackLog.Add("Throws.Serialized");

    [NinoOnDeserialized]
    internal void Deserialized()
    {
        if (A < 0)
            throw new ArgumentException("thrown by a callback");
        CallbackLog.Add("Throws.Deserialized");
    }
}

[TestClass]
public class CallbackTests
{
    [TestInitialize]
    public void Reset()
    {
        CallbackLog.Take();
        CallbackCounts.Reset();
    }

    [TestMethod]
    public void HierarchyCallsBaseFirstAndStaticBeforeTheInstance()
    {
        var bytes = NinoSerializer.Serialize(new CallbackDerived { A = 1, B = "b" });
        CollectionAssert.AreEqual(new[] { "Base.Serializing", "Derived.Serializing", "Base.Serialized" },
            CallbackLog.Take());

        var read = NinoDeserializer.Deserialize<CallbackDerived>(bytes);
        Assert.AreEqual("b", read.B);
        CollectionAssert.AreEqual(new[]
        {
            "Base.Deserializing(static)", "Derived.Deserializing B=null", "Base.Deserialized A=1",
            "Derived.Deserialized B=b",
        }, CallbackLog.Take());
    }

    [TestMethod]
    public void BaseSlotsCallTheSubtypesCallbacks()
    {
        var holder = new CallbackHolder
        {
            Item = new CallbackDerived { A = 1, B = "x" },
            Items = new List<CallbackBase> { new CallbackBase { A = 2 }, new CallbackDerived { A = 3, B = "y" }, null },
        };
        var bytes = NinoSerializer.Serialize(holder);
        CollectionAssert.AreEqual(new[]
        {
            "Base.Serializing", "Derived.Serializing", "Base.Serialized",
            "Base.Serializing", "Base.Serialized",
            "Base.Serializing", "Derived.Serializing", "Base.Serialized",
        }, CallbackLog.Take());

        var read = NinoDeserializer.Deserialize<CallbackHolder>(bytes);
        Assert.IsInstanceOfType(read.Items[1], typeof(CallbackDerived));
        CollectionAssert.AreEqual(new[]
        {
            "Base.Deserializing(static)", "Derived.Deserializing B=null", "Base.Deserialized A=1", "Derived.Deserialized B=x",
            "Base.Deserializing(static)", "Base.Deserialized A=2",
            "Base.Deserializing(static)", "Derived.Deserializing B=null", "Base.Deserialized A=3", "Derived.Deserialized B=y",
        }, CallbackLog.Take());

        // Boxed through the base, by its runtime type.
        CallbackBase item = new CallbackDerived { A = 4, B = "z" };
        var boxed = NinoDeserializer.Deserialize<CallbackBase>(NinoSerializer.Serialize(item));
        Assert.AreEqual("z", ((CallbackDerived)boxed).B);
        Assert.AreEqual(7, CallbackLog.Take().Length);
    }

    [TestMethod]
    public void VirtualCallbackRunsTheOverrideOnce()
    {
        CallbackShape shape = new CallbackCircle { Id = 1, Radius = 2.5f };
        var read = NinoDeserializer.Deserialize<CallbackShape>(NinoSerializer.Serialize(shape));
        Assert.AreEqual(2.5f, ((CallbackCircle)read).Radius);
        CollectionAssert.AreEqual(new[] { "Circle.Loaded 2.5" }, CallbackLog.Take());
    }

    [TestMethod]
    public void HiddenBaseCallbackStillRuns()
    {
        NinoSerializer.Serialize(new CallbackHidden { A = 1 });
        CollectionAssert.AreEqual(new[] { "HiddenBase.Prepare", "Hidden.Prepare" }, CallbackLog.Take());
    }

    [TestMethod]
    public void StructCallbacksRunOnTheValueInItsSlot()
    {
        var holder = new CallbackHolder
        {
            Structs = new[] { new CallbackStruct { X = 1 }, new CallbackStruct { X = 2 } },
            ById = new Dictionary<int, CallbackStruct> { [7] = new CallbackStruct { X = 3 } },
        };
        var bytes = NinoSerializer.Serialize(holder);
        CollectionAssert.AreEqual(new[] { "Struct.Serializing 1", "Struct.Serializing 2", "Struct.Serializing 3" },
            CallbackLog.Take());

        var read = NinoDeserializer.Deserialize<CallbackHolder>(bytes);
        CollectionAssert.AreEqual(new[] { 2, 4 }, read.Structs.Select(s => s.Doubled).ToArray());
        Assert.AreEqual(6, read.ById[7].Doubled);
        Assert.AreEqual(3, CallbackLog.Take().Length);

        // Top level, and a list of them.
        var one = NinoDeserializer.Deserialize<CallbackStruct>(NinoSerializer.Serialize(new CallbackStruct { X = 5 }));
        Assert.AreEqual(10, one.Doubled);
        var list = NinoDeserializer.Deserialize<List<CallbackStruct>>(NinoSerializer.Serialize(
            new List<CallbackStruct> { new() { X = 6 }, new() { X = 7 } }));
        CollectionAssert.AreEqual(new[] { 12, 14 }, list.Select(s => s.Doubled).ToArray());
    }

    [TestMethod]
    public void DeserializingIntoAnExistingInstanceCallsItOnThatInstance()
    {
        var bytes = NinoSerializer.Serialize(new CallbackDerived { A = 1, B = "new" });
        CallbackLog.Take();
        var existing = new CallbackDerived { A = 9, B = "old" };
        var target = existing;
        NinoDeserializer.Deserialize(bytes, ref target);
        Assert.AreSame(existing, target);
        Assert.AreEqual("new", target.B);
        CollectionAssert.AreEqual(new[]
        {
            "Base.Deserializing(static)", "Derived.Deserializing B=old", "Base.Deserialized A=1",
            "Derived.Deserialized B=new",
        }, CallbackLog.Take());
    }

    [TestMethod]
    public void TypesCreatedFromTheirMembersCallOnlyTheStaticBeforeReading()
    {
        var read = NinoDeserializer.Deserialize<CallbackRecord>(NinoSerializer.Serialize(new CallbackRecord(1, "r")));
        Assert.AreEqual(new CallbackRecord(1, "r"), read);
        CollectionAssert.AreEqual(new[] { "Record.Deserializing(static)", "Record.Deserialized 1 r" },
            CallbackLog.Take());
    }

    [TestMethod]
    public void KeyedAndGenericTypesCallTheirCallbacks()
    {
        var keyed = NinoDeserializer.Deserialize<CallbackKeyed>(NinoSerializer.Serialize(new CallbackKeyed { A = 1, B = "k" }));
        Assert.AreEqual("k", keyed.B);
        CollectionAssert.AreEqual(new[] { "Keyed.Serialized", "Keyed.Deserialized 1 k" }, CallbackLog.Take());

        var box = NinoDeserializer.Deserialize<CallbackBox<string>>(
            NinoSerializer.Serialize(new CallbackBox<string> { Value = "v" }));
        Assert.AreEqual("v", box.Value);
        CollectionAssert.AreEqual(new[] { "Box<String>.Deserializing(static)", "Box<String>.Deserialized v" },
            CallbackLog.Take());
    }

    [TestMethod]
    public void SealedClassesCallThemOnEveryPath()
    {
        // Top level (the __Top twin and its new-instance read), into an existing instance, and held by another
        // sealed class (its __Shallow twin), in a list.
        var bytes = NinoSerializer.Serialize(new CallbackSealed { A = 1, Values = new[] { 1, 2 } });
        var read = NinoDeserializer.Deserialize<CallbackSealed>(bytes);
        Assert.AreEqual(2, read.Values.Length);
        CollectionAssert.AreEqual(new[] { "Sealed.Serializing", "Sealed.Deserializing -1", "Sealed.Deserialized 2" },
            CallbackLog.Take());

        NinoDeserializer.Deserialize(bytes, ref read);
        CollectionAssert.AreEqual(new[] { "Sealed.Deserializing 2", "Sealed.Deserialized 2" }, CallbackLog.Take());

        var holder = NinoDeserializer.Deserialize<CallbackSealedHolder>(NinoSerializer.Serialize(
            new CallbackSealedHolder { Item = new CallbackSealed { Values = new[] { 3 } } }));
        Assert.AreEqual(3, holder.Item.Values[0]);
        CollectionAssert.AreEqual(new[]
        {
            "Sealed.Serializing", "SealedHolder.Serialized",
            "Sealed.Deserializing -1", "Sealed.Deserialized 1", "SealedHolder.Deserialized",
        }, CallbackLog.Take());

        var list = NinoDeserializer.Deserialize<List<CallbackSealed>>(NinoSerializer.Serialize(new List<CallbackSealed>
            { new() { Values = new int[0] }, new() { Values = new int[1] } }));
        Assert.AreEqual(2, list.Count);
        Assert.AreEqual(6, CallbackLog.Take().Length);
    }

    [TestMethod]
    public void TrackedObjectsCallThemOncePerObject()
    {
        var a = new CallbackNode { Name = "a" };
        var b = new CallbackNode { Name = "b", Next = a };
        a.Next = b;
        var bytes = NinoSerializer.Serialize(a);
        CollectionAssert.AreEqual(new[] { "Node.Serializing a", "Node.Serializing b" }, CallbackLog.Take());

        var read = NinoDeserializer.Deserialize<CallbackNode>(bytes);
        Assert.AreSame(read, read.Next.Next);
        // b's body ends first; a's back reference reads no body.
        CollectionAssert.AreEqual(new[] { "Node.Deserialized b", "Node.Deserialized a" }, CallbackLog.Take());
    }

    [TestMethod]
    public void DeferredBodiesCallThemWhenRead()
    {
        const int count = 300;
        CallbackLink head = null;
        for (int i = count - 1; i >= 0; i--)
            head = new CallbackLink { Index = i, Next = head };
        var read = NinoDeserializer.Deserialize<CallbackLink>(NinoSerializer.Serialize(head));
        int length = 0;
        for (var link = read; link != null; link = link.Next)
            Assert.AreEqual(length++, link.Index);
        Assert.AreEqual(count, length);
        var events = CallbackLog.Take();
        // Once per object, each after its own body.
        CollectionAssert.AreEquivalent(Enumerable.Range(0, count).Select(i => i.ToString()).ToArray(), events);
    }

    [TestMethod]
    public void ExceptionsFromCallbacksEndTheCall()
    {
        var thrown = Assert.ThrowsExactly<InvalidOperationException>(() =>
            NinoSerializer.Serialize(new CallbackThrows { ThrowOnSerializing = true }));
        Assert.AreEqual("thrown by a callback", thrown.Message);
        Assert.AreEqual(0, CallbackLog.Take().Length, "OnSerialized does not run after a throw");

        var bad = NinoSerializer.Serialize(new CallbackThrows { A = -1 });
        CallbackLog.Take();
        Assert.ThrowsExactly<ArgumentException>(() => NinoDeserializer.Deserialize<CallbackThrows>(bad));

        // The next calls of the thread start clean.
        var read = NinoDeserializer.Deserialize<CallbackThrows>(NinoSerializer.Serialize(new CallbackThrows { A = 1 }));
        Assert.AreEqual(1, read.A);
        CollectionAssert.AreEqual(new[] { "Throws.Serialized", "Throws.Deserialized" }, CallbackLog.Take());
    }

    [TestMethod]
    public void CallbacksKeepTheBytesAndLeaveThePathsThatRunNoCode()
    {
        // A struct of fixed size: its arrays are written element by element, not as blocks, with the same bytes.
        var blocks = new[] { new CallbackBlock { A = 1, T = (2, 3) }, new CallbackBlock { A = 4, T = (5, 6) } };
        var plain = blocks.Select(b => new PlainBlock { A = b.A, T = b.T }).ToArray();
        var bytes = NinoSerializer.Serialize(blocks);
        CollectionAssert.AreEqual(NinoSerializer.Serialize(plain), bytes);
        Assert.AreEqual(2, CallbackCounts.Serializing);
        Assert.AreEqual(2, CallbackCounts.Serialized);
        var read = NinoDeserializer.Deserialize<CallbackBlock[]>(bytes);
        CollectionAssert.AreEqual(new[] { 2, 8 }, read.Select(b => b.Doubled).ToArray());
        Assert.AreEqual(2, CallbackCounts.Deserializing);
        Assert.AreEqual(2, CallbackCounts.Deserialized);
        Assert.AreEqual(-1, NinoSerializer.GetFixedSize<CallbackBlock>());
        Assert.AreNotEqual(-1, NinoSerializer.GetFixedSize<PlainBlock>());

        // A sealed class of raw members, written without its raw body.
        CallbackCounts.Reset();
        CollectionAssert.AreEqual(NinoSerializer.Serialize(new PlainRawBody { A = 1, B = 2 }),
            NinoSerializer.Serialize(new CallbackRawBody { A = 1, B = 2 }));
        var raw = NinoDeserializer.Deserialize<List<CallbackRawBody>>(NinoSerializer.Serialize(
            new List<CallbackRawBody> { new() { A = 3 }, new() { A = 4 } }));
        Assert.AreEqual(4, raw[1].A);
        Assert.AreEqual(3, CallbackCounts.Serializing);
        Assert.AreEqual(2, CallbackCounts.Deserialized);

        // A small sealed class whose body is inlined, at the top level and in an array.
        CallbackCounts.Reset();
        CollectionAssert.AreEqual(NinoSerializer.Serialize(new PlainInline { A = 1, Name = "n" }),
            NinoSerializer.Serialize(new CallbackInline { A = 1, Name = "n" }));
        var inline = NinoDeserializer.Deserialize<CallbackInline>(NinoSerializer.Serialize(new CallbackInline { Name = "m" }));
        Assert.AreEqual("m", inline.Name);
        var inlines = NinoDeserializer.Deserialize<CallbackInline[]>(NinoSerializer.Serialize(
            new[] { new CallbackInline(), new CallbackInline() }));
        Assert.AreEqual(2, inlines.Length);
        Assert.AreEqual(4, CallbackCounts.Serialized);
        Assert.AreEqual(3, CallbackCounts.Deserializing);
        Assert.AreEqual(3, CallbackCounts.Deserialized);
    }

    [TestMethod]
    public void CallbacksLeaveThePublishedLayoutAlone()
    {
        // What nino-gen and the layout baseline read: the same with and without callbacks.
        var layouts = typeof(CallbackBlock).Assembly.GetCustomAttributes(typeof(NinoTypeLayoutAttribute), false)
            .Cast<NinoTypeLayoutAttribute>().ToDictionary(l => l.Type);
        foreach (var (with, without) in new[]
                 {
                     (typeof(CallbackBlock), typeof(PlainBlock)), (typeof(CallbackRawBody), typeof(PlainRawBody)),
                     (typeof(CallbackInline), typeof(PlainInline)),
                 })
        {
            NinoTypeLayoutAttribute a = layouts[with], b = layouts[without];
            Assert.AreEqual((b.Format, b.Header, b.TrackReferences, b.Raw, b.Members, b.Deferrable),
                (a.Format, a.Header, a.TrackReferences, a.Raw, a.Members, a.Deferrable), with.Name);
        }
    }
}

[NinoType(format: NinoFormat.VersionTolerant), NinoPooled]
public sealed partial class CallbackPooled
{
    public int Id;
    public List<int> Values;

    private static partial IEnumerable<CallbackPooled> OnNinoPoolWarmup() => null;

    private partial void OnNinoPoolReturn() => CallbackLog.Add($"Pooled.Return {Id}");

    [NinoOnDeserializing]
    internal void Deserializing() => CallbackLog.Add($"Pooled.Deserializing {Id}");

    [NinoOnDeserialized]
    internal void Deserialized() => CallbackLog.Add($"Pooled.Deserialized {Id}");
}

/// <summary>Pooled reads, which share the process-wide pool.</summary>
[TestClass]
[DoNotParallelize]
public class CallbackPoolTests
{
    [TestMethod]
    public void PooledInstancesAreReadLikeExistingOnes()
    {
        NinoPool<CallbackPooled>.Shared.Clear();
        CallbackLog.Take();
        var bytes = NinoSerializer.Serialize(new CallbackPooled { Id = 2, Values = new List<int> { 1 } });
        var first = NinoDeserializer.Deserialize<CallbackPooled>(NinoSerializer.Serialize(new CallbackPooled { Id = 1 }));
        first.ReturnToPool();
        var second = NinoDeserializer.Deserialize<CallbackPooled>(bytes);
        Assert.AreSame(first, second);
        // The pool's return hook resets; the callbacks run on the pooled instance as on any existing one.
        CollectionAssert.AreEqual(new[]
        {
            "Pooled.Deserializing 0", "Pooled.Deserialized 1", "Pooled.Return 1",
            "Pooled.Deserializing 1", "Pooled.Deserialized 2",
        }, CallbackLog.Take());
        NinoPool<CallbackPooled>.Shared.Clear();
    }
}
