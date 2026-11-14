using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Nino.Core;

namespace Nino.UnitTests;

/// <summary>
/// The Stream overloads: the bytes of the span and buffer overloads with no framing, read back to the end of the stream
/// from a MemoryStream, an UnmanagedMemoryStream and a memory-mapped view (in place), an UnmanagedMemoryStream over a
/// SafeBuffer, a seekable stream and a non-seekable one that hands over a few bytes per read,
/// synchronously and asynchronously, new values and into existing ones, through the interceptors and the boxed API;
/// truncated, empty and oversized input, a stream that claims more than it holds, cancellation, and a write still
/// pending while the thread serializes something else.
/// </summary>
[TestClass]
public class StreamTests
{
    private static SimpleClass Sample(int id, int length = 9) =>
        new()
        { Id = id, Name = new string((char)('a' + id % 26), length), CreateTime = new DateTime(2026, 11, 8) };

    private static void AssertSame(SimpleClass expected, SimpleClass actual)
    {
        Assert.IsNotNull(actual);
        Assert.AreEqual(expected.Id, actual.Id);
        Assert.AreEqual(expected.Name, actual.Name);
        Assert.AreEqual(expected.CreateTime, actual.CreateTime);
    }

    private static byte[] Written(Action<Stream> write)
    {
        var stream = new MemoryStream();
        write(stream);
        return stream.ToArray();
    }

    /// <summary>
    /// Each stream a payload is read from: an exposed MemoryStream, an UnmanagedMemoryStream subclass, a memory-mapped
    /// view not aligned to a page, an UnmanagedMemoryStream over a SafeBuffer, a hidden buffer, and a trickle.
    /// </summary>
    private static IEnumerable<Func<byte[], Stream>> Sources() => new Func<byte[], Stream>[]
    {
        bytes =>
        {
            var exposed = new MemoryStream();
            exposed.Write(bytes, 0, bytes.Length);
            exposed.Position = 0;
            return exposed;
        },
        NativeStream.Of,
        bytes => MappedView(bytes, accessor: false),
        bytes => MappedView(bytes, accessor: true),
        bytes => new MemoryStream(bytes, writable: false),
        bytes => new TrickleStream(bytes, 3),
        bytes => new TrickleStream(bytes, 1000),
    };

    [TestMethod]
    public void WritesTheBytesOfTheOtherOverloads()
    {
        var sample = Sample(3);
        CollectionAssert.AreEqual(NinoSerializer.Serialize(sample), Written(s => NinoSerializer.Serialize(sample, s)));
        CollectionAssert.AreEqual(NinoSerializer.Serialize((SimpleClass)null),
            Written(s => NinoSerializer.Serialize((SimpleClass)null, s)));
        CollectionAssert.AreEqual(NinoSerializer.Serialize(42), Written(s => NinoSerializer.Serialize(42, s)));
        var ints = Enumerable.Range(0, 100).ToArray();
        CollectionAssert.AreEqual(NinoSerializer.Serialize(ints), Written(s => NinoSerializer.Serialize(ints, s)));
        var list = Enumerable.Range(0, 20).Select(i => Sample(i)).ToList();
        CollectionAssert.AreEqual(NinoSerializer.Serialize(list), Written(s => NinoSerializer.Serialize(list, s)));
        CollectionAssert.AreEqual(NinoSerializer.Serialize("héllo"), Written(s => NinoSerializer.Serialize("héllo", s)));
        HierarchicalBase sub = new HierarchicalSub2 { B = "b", D = 3, F = new List<int> { 1 } };
        CollectionAssert.AreEqual(NinoSerializer.Serialize(sub), Written(s => NinoSerializer.Serialize(sub, s)));
        CollectionAssert.AreEqual(NinoSerializer.Serialize(sample),
            Written(s => NinoSerializer.SerializeBoxed(sample, s, typeof(SimpleClass))));

        // Appended after what the stream holds, with no framing: two values follow one another.
        var both = Written(s =>
        {
            NinoSerializer.Serialize(1, s);
            NinoSerializer.Serialize(sample, s);
        });
        CollectionAssert.AreEqual(NinoSerializer.Serialize(1).Concat(NinoSerializer.Serialize(sample)).ToArray(), both);
    }

    [TestMethod]
    public void ReadsToTheEndOfEveryKindOfStream()
    {
        var sample = Sample(5);
        var large = Sample(6, 300_000); // past the first array of a non-seekable stream: grown several times
        var ints = Enumerable.Range(0, 1000).ToArray();
        HierarchicalBase sub = new HierarchicalSub2 { B = "b", D = 3, F = new List<int> { 1 } };
        foreach (var source in Sources())
        {
            AssertSame(sample, NinoDeserializer.Deserialize<SimpleClass>(source(NinoSerializer.Serialize(sample))));
            AssertSame(large, NinoDeserializer.Deserialize<SimpleClass>(source(NinoSerializer.Serialize(large))));
            Assert.IsNull(NinoDeserializer.Deserialize<SimpleClass>(source(NinoSerializer.Serialize((SimpleClass)null))));
            Assert.AreEqual(42, NinoDeserializer.Deserialize<int>(source(NinoSerializer.Serialize(42))));
            CollectionAssert.AreEqual(ints, NinoDeserializer.Deserialize<int[]>(source(NinoSerializer.Serialize(ints))));
            var back = NinoDeserializer.Deserialize<HierarchicalBase>(source(NinoSerializer.Serialize(sub)));
            Assert.IsInstanceOfType(back, typeof(HierarchicalSub2));
            Assert.AreEqual("b", ((HierarchicalSub2)back).B);
            AssertSame(sample, (SimpleClass)NinoDeserializer.DeserializeBoxed(source(NinoSerializer.Serialize(sample)),
                typeof(SimpleClass)));
        }
    }

    [TestMethod]
    public void ReadsFromTheStreamsPosition()
    {
        var sample = Sample(7);
        byte[] bytes = NinoSerializer.Serialize(sample);
        var stream = new MemoryStream();
        stream.Write(new byte[] { 9, 9, 9 }, 0, 3);
        stream.Write(bytes, 0, bytes.Length);
        stream.Position = 3;
        AssertSame(sample, NinoDeserializer.Deserialize<SimpleClass>(stream));
        Assert.AreEqual(stream.Length, stream.Position); // read to its end
        var readOnly = new MemoryStream(new byte[] { 9, 9 }.Concat(bytes).ToArray(), writable: false) { Position = 2 };
        AssertSame(sample, NinoDeserializer.Deserialize<SimpleClass>(readOnly));
        Assert.AreEqual(readOnly.Length, readOnly.Position);
    }

    [TestMethod]
    public unsafe void ReadsNativeMemoryFromItsPosition()
    {
        var sample = Sample(11);
        byte[] bytes = NinoSerializer.Serialize(sample);
        byte[] prefixed = new byte[] { 9, 9, 9, 9 }.Concat(bytes).ToArray();
        fixed (byte* start = prefixed)
        {
            using var native = new UnmanagedMemoryStream(start, prefixed.Length) { Position = 4 };
            AssertSame(sample, NinoDeserializer.Deserialize<SimpleClass>(native));
            Assert.AreEqual(native.Length, native.Position);
            native.Position = native.Length + 10;
            Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<SimpleClass>(native));
        }

        using var view = MappedView(new byte[] { 9, 9 }.Concat(bytes).ToArray(), accessor: false);
        view.Position = 2;
        AssertSame(sample, NinoDeserializer.Deserialize<SimpleClass>(view));
        Assert.AreEqual(view.Length, view.Position);
    }

    [TestMethod]
    public unsafe void ReadsDerivedStreamsThroughTheirReadMethods()
    {
        byte* bytes = stackalloc byte[sizeof(int)];
        using var stream = new LongerStream(bytes);
        Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<int>(stream));
        Assert.AreEqual(1L, stream.Position);
    }

    [TestMethod]
    public unsafe void ConsumesInPlaceStreamsBeforeRejectingThePayload()
    {
        byte* bytes = stackalloc byte[1];
        using var native = new UnmanagedMemoryStream(bytes, 1);
        using var view = MappedView(new byte[1], accessor: false);
        foreach (var stream in new Stream[] { native, view })
        {
            Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<int>(stream));
            Assert.AreEqual(stream.Length, stream.Position);
        }
    }

#if NET
    [TestMethod]
    public unsafe void SteadyStateCallsAllocateNothing()
    {
        // The owner's question: are the stream calls zero GC? Into a reused instance, after a warm-up, yes, for every
        // kind of stream: in place, or through a pooled array.
        var sample = Sample(12, 40);
        byte[] bytes = NinoSerializer.Serialize(sample);
        var target = new MemoryStream();
        var exposed = new MemoryStream();
        exposed.Write(bytes, 0, bytes.Length);
        byte* nativeBytes = stackalloc byte[bytes.Length];
        bytes.CopyTo(new Span<byte>(nativeBytes, bytes.Length));
        using var native = new UnmanagedMemoryStream(nativeBytes, bytes.Length);
        var copied = new MemoryStream(bytes, writable: false);
        var streams = new Stream[] { exposed, native, copied };
        SimpleClass into = null;

        void Once()
        {
            target.Position = 0;
            NinoSerializer.Serialize(sample, target);
            foreach (var stream in streams)
            {
                stream.Position = 0;
                NinoDeserializer.Deserialize(stream, ref into);
            }

            target.Position = 0;
            NinoSerializer.SerializeAsync(sample, target).GetAwaiter().GetResult();
        }

        for (int i = 0; i < 100; i++)
            Once();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
            Once();
        Assert.AreEqual(0, GC.GetAllocatedBytesForCurrentThread() - before);
        AssertSame(sample, into);
    }
#endif

    [TestMethod]
    public void ReadsIntoExistingInstances()
    {
        var first = Sample(1);
        var second = Sample(2, 50);
        foreach (var source in Sources())
        {
            SimpleClass reused = null;
            NinoDeserializer.Deserialize(source(NinoSerializer.Serialize(first)), ref reused);
            var instance = reused;
            AssertSame(first, reused);
            NinoDeserializer.Deserialize(source(NinoSerializer.Serialize(second)), ref reused);
            Assert.AreSame(instance, reused);
            AssertSame(second, reused);

            object boxed = null;
            NinoDeserializer.DeserializeBoxed(source(NinoSerializer.Serialize(first)), ref boxed, typeof(SimpleClass));
            var boxedInstance = boxed;
            NinoDeserializer.DeserializeBoxed(source(NinoSerializer.Serialize(second)), ref boxed, typeof(SimpleClass));
            Assert.AreSame(boxedInstance, boxed);
            AssertSame(second, (SimpleClass)boxed);
        }
    }

    [TestMethod]
    public void LongArraysAndListsOfPrefetchingClasses()
    {
        // Intercepted with NinoDirect's ...Prefetched Stream forms (this assembly builds with
        // NinoPrefetchLargeCollections): the bytes and the reads are the regular ones.
        var items = Enumerable.Range(0, 5000)
            .Select(i => new PrefetchItem { Id = i, Name = "item " + i, Value = i * 3L }).Reverse().ToArray();
        var list = items.ToList();
        CollectionAssert.AreEqual(NinoSerializer.Serialize(items), Written(s => NinoSerializer.Serialize(items, s)));
        CollectionAssert.AreEqual(NinoSerializer.Serialize(list), Written(s => NinoSerializer.Serialize(list, s)));
        var into = items.Select(i => new PrefetchItem()).ToArray();
        var objects = into.ToArray();
        NinoDeserializer.Deserialize(new TrickleStream(NinoSerializer.Serialize(items), 4096), ref into);
        Assert.AreEqual(items.Length, into.Length);
        for (int i = 0; i < items.Length; i++)
        {
            Assert.AreSame(objects[i], into[i]);
            Assert.AreEqual(items[i].Name, into[i].Name);
        }

        var intoList = new List<PrefetchItem>(objects);
        NinoDeserializer.Deserialize(new MemoryStream(NinoSerializer.Serialize(list)), ref intoList);
        Assert.AreEqual(list[4999].Name, intoList[4999].Name);
        Assert.AreSame(objects[4999], intoList[4999]);
    }

    [TestMethod]
    [DataRow(4095)]
    [DataRow(4096)]
    [DataRow(4097)]
    public void ReadsAcrossPooledBufferBoundaries(int length)
    {
        byte[] expected = Enumerable.Range(0, length - sizeof(int)).Select(i => (byte)(i % 251)).ToArray();
        byte[] bytes = NinoSerializer.Serialize(expected);
        Assert.AreEqual(length, bytes.Length);
        using var stream = new TrickleStream(bytes, 4096);
        CollectionAssert.AreEqual(expected, NinoDeserializer.Deserialize<byte[]>(stream));
    }

    [TestMethod]
    public void TruncatedAndEmptyInputThrows()
    {
        byte[] bytes = NinoSerializer.Serialize(Sample(4, 100));
        foreach (var source in Sources())
        {
            Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<SimpleClass>(source(bytes[..^1])));
            Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<SimpleClass>(source(bytes[..3])));
            Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<SimpleClass>(source(Array.Empty<byte>())));
            Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<int>(source(new byte[] { 1, 2 })));
        }
    }

    [TestMethod]
    public void OversizedLengthsThrowBeforeAllocating()
    {
        // An array claiming int.MaxValue / 2 elements in a few bytes: refused against the bytes the stream held.
        byte[] bytes = NinoSerializer.Serialize(new[] { 1, 2, 3 });
        BitConverter.GetBytes(int.MaxValue / 2).CopyTo(bytes, 0);
        CollectionAssert.AreEqual(BitConverter.GetBytes(int.MaxValue / 2), bytes[..4]);
        foreach (var source in Sources())
            Assert.ThrowsExactly<NinoDataException>(() => NinoDeserializer.Deserialize<int[]>(source(bytes)));
    }

    [TestMethod]
    public void AStreamsLengthIsNotTrustedAhead()
    {
        // A seekable stream that claims a gigabyte and holds a few bytes is read as what it holds, with the first
        // array capped (NinoStreams.MaxFirstRead), not sized by the claim.
        var sample = Sample(8);
        var stream = new LyingStream(NinoSerializer.Serialize(sample), 1L << 30);
#if NET
        long before = GC.GetAllocatedBytesForCurrentThread();
#endif
        AssertSame(sample, NinoDeserializer.Deserialize<SimpleClass>(stream));
#if NET
        Assert.IsTrue(GC.GetAllocatedBytesForCurrentThread() - before < 8 << 20);
#endif
    }

    [TestMethod]
    public void NullArgumentsThrow()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => NinoSerializer.Serialize(Sample(1), (Stream)null));
        Assert.ThrowsExactly<ArgumentNullException>(() => NinoDeserializer.Deserialize<SimpleClass>((Stream)null));
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            NinoSerializer.SerializeBoxed(Sample(1), new MemoryStream(), null));
        Assert.ThrowsExactly<ArgumentNullException>(() => NinoDeserializer.DeserializeBoxed(new MemoryStream(), null));
    }

    [TestMethod]
    public async Task AsyncRoundTrips()
    {
        var sample = Sample(9);
        var large = Sample(10, 200_000);
        foreach (var value in new[] { sample, large, null })
        {
            var stream = new MemoryStream();
            await NinoSerializer.SerializeAsync(value, stream);
            CollectionAssert.AreEqual(NinoSerializer.Serialize(value), stream.ToArray());
            foreach (var source in Sources())
            {
                var back = await NinoDeserializer.DeserializeAsync<SimpleClass>(source(stream.ToArray()));
                if (value == null)
                    Assert.IsNull(back);
                else
                    AssertSame(value, back);
            }
        }

        var boxed = new MemoryStream();
        await NinoSerializer.SerializeBoxedAsync(sample, boxed, typeof(SimpleClass));
        CollectionAssert.AreEqual(NinoSerializer.Serialize(sample), boxed.ToArray());
        boxed.Position = 0;
        AssertSame(sample, (SimpleClass)await NinoDeserializer.DeserializeBoxedAsync(boxed, typeof(SimpleClass)));
        var ints = Enumerable.Range(0, 50).ToArray();
        var raw = new MemoryStream();
        await NinoSerializer.SerializeAsync(ints, raw);
        CollectionAssert.AreEqual(ints, await NinoDeserializer.DeserializeAsync<int[]>(new TrickleStream(raw.ToArray(), 7)));
    }

    [TestMethod]
    public async Task AsyncTruncatedInputThrows()
    {
        byte[] bytes = NinoSerializer.Serialize(Sample(4, 100));
        foreach (var source in Sources())
        {
            await Assert.ThrowsExactlyAsync<NinoDataException>(async () =>
                await NinoDeserializer.DeserializeAsync<SimpleClass>(source(bytes[..^1])));
            await Assert.ThrowsExactlyAsync<NinoDataException>(async () =>
                await NinoDeserializer.DeserializeAsync<SimpleClass>(source(Array.Empty<byte>())));
        }
    }

    [TestMethod]
    public async Task AsyncCallsObserveCancellation()
    {
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        var stream = new MemoryStream();
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await NinoSerializer.SerializeAsync(Sample(1), stream, canceled.Token));
        Assert.AreEqual(0, stream.Length);
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await NinoDeserializer.DeserializeAsync<SimpleClass>(new MemoryStream(NinoSerializer.Serialize(Sample(1))),
                canceled.Token));

        // Canceled between two reads of a stream that hands over a few bytes at a time.
        using var midway = new CancellationTokenSource();
        var trickle = new TrickleStream(NinoSerializer.Serialize(Sample(2, 1000)), 16) { AfterRead = midway.Cancel };
        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await NinoDeserializer.DeserializeAsync<SimpleClass>(trickle, midway.Token));
    }

    [TestMethod]
    public async Task APendingWriteKeepsItsBytes()
    {
        // The stream reads the memory it was given only when the write completes; meanwhile this thread serializes
        // other values through its scratch buffer, which must not reuse the pending write's array.
        var first = Sample(11, 5000);
        var stream = new PendingWriteStream();
        var pending = NinoSerializer.SerializeAsync(first, stream);
        Assert.IsFalse(pending.IsCompleted);
        for (int i = 0; i < 10; i++)
            NinoSerializer.Serialize(Sample(12 + i, 6000));
        stream.Complete();
        await pending;
        CollectionAssert.AreEqual(NinoSerializer.Serialize(first), stream.Written);
    }

    [TestMethod]
    public void TopLevelStreamCallsAreIntercepted()
    {
        // The interceptors of this assembly's calls (the Roslyn 4.8+ generator) call NinoDirect's Stream forms.
        var interceptors = typeof(StreamTests).Assembly.GetTypes().FirstOrDefault(t => t.Name.EndsWith("NinoInterceptors"));
        if (interceptors == null)
            return; // built with -p:NinoRegularCalls=true
        var called = new HashSet<string>();
        foreach (var method in interceptors.GetMethods(System.Reflection.BindingFlags.Public |
                                                       System.Reflection.BindingFlags.NonPublic |
                                                       System.Reflection.BindingFlags.Static))
        {
            var il = method.GetMethodBody()?.GetILAsByteArray();
            for (int i = 0; il != null && i + 4 < il.Length; i++)
            {
                if (il[i] != 0x28 || il[i + 4] != 0x2B)
                    continue;
                try
                {
                    if (method.Module.ResolveMethod(BitConverter.ToInt32(il, i + 1)) is System.Reflection.MethodInfo
                        {
                            IsGenericMethod: true, DeclaringType.Name: "NinoDirect",
                        } target && target.GetParameters().Any(p => p.ParameterType == typeof(Stream)))
                        called.Add(target.Name);
                }
                catch (ArgumentException)
                {
                    // not an instruction boundary
                }
            }
        }

        string all = string.Join(", ", called);
        Assert.IsTrue(called.Contains("Serialize") || called.Contains("SerializeTop"), all);
        Assert.IsTrue(called.Contains("Deserialize") || called.Contains("DeserializeTop"), all);
        Assert.IsTrue(called.Contains("SerializePrefetched"), all);
        Assert.IsTrue(called.Contains("DeserializePrefetched"), all);
        Assert.IsTrue(called.Contains("SerializeAsync"), all);
        Assert.IsTrue(called.Contains("DeserializeAsync"), all);
    }

    /// <summary>
    /// <paramref name="bytes"/> in a memory-mapped view that does not start on a page (its bytes PointerOffset into the
    /// mapping): a MemoryMappedViewStream, or, with <paramref name="accessor"/>, an UnmanagedMemoryStream over the
    /// SafeBuffer of a view accessor, which has no position pointer. The mapping lives as long as the view.
    /// </summary>
    private static Stream MappedView(byte[] bytes, bool accessor)
    {
        const int offset = 4099;
        if (bytes.Length == 0)
            return new MemoryStream(); // a view of size 0 is the rest of its mapping, so there is no empty view
        var file = MemoryMappedFile.CreateNew(null, offset + bytes.Length + 1);
        using (var writer = file.CreateViewStream(offset, bytes.Length))
            writer.Write(bytes, 0, bytes.Length);
        if (!accessor)
            return file.CreateViewStream(offset, bytes.Length, MemoryMappedFileAccess.Read);
        var view = file.CreateViewAccessor(offset, bytes.Length, MemoryMappedFileAccess.Read);
        return new UnmanagedMemoryStream(view.SafeMemoryMappedViewHandle, view.PointerOffset, bytes.Length);
    }

    // Its base Read method exposes one byte. The remaining allocated bytes are outside the stream.
    private sealed unsafe class LongerStream : UnmanagedMemoryStream
    {
        public LongerStream(byte* bytes) : base(bytes, 1) { }

        public override long Length => sizeof(int);
    }

    /// <summary>An UnmanagedMemoryStream over native memory it frees when collected.</summary>
    private sealed unsafe class NativeStream : UnmanagedMemoryStream
    {
        private IntPtr _memory;

        private NativeStream(IntPtr memory, int length) : base((byte*)memory, length) => _memory = memory;

        public static Stream Of(byte[] bytes)
        {
            IntPtr memory = Marshal.AllocHGlobal(Math.Max(bytes.Length, 1));
            Marshal.Copy(bytes, 0, memory, bytes.Length);
            return new NativeStream(memory, bytes.Length);
        }

        ~NativeStream()
        {
            Marshal.FreeHGlobal(_memory);
            _memory = IntPtr.Zero;
        }
    }

    /// <summary>A non-seekable stream over bytes that hands over at most a few per read, asynchronously when awaited.</summary>
    private sealed class TrickleStream : Stream
    {
        private readonly byte[] _bytes;
        private readonly int _chunk;
        private int _position;

        public TrickleStream(byte[] bytes, int chunk)
        {
            _bytes = bytes;
            _chunk = chunk;
        }

        public Action AfterRead { get; set; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = Math.Min(Math.Min(count, _chunk), _bytes.Length - _position);
            Buffer.BlockCopy(_bytes, _position, buffer, offset, n);
            _position += n;
            AfterRead?.Invoke();
            return n;
        }

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count,
            CancellationToken cancellationToken)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            return Read(buffer, offset, count);
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A seekable stream whose <see cref="Length"/> claims more than it holds.</summary>
    private sealed class LyingStream : Stream
    {
        private readonly byte[] _bytes;
        private readonly long _claimed;
        private int _position;

        public LyingStream(byte[] bytes, long claimed)
        {
            _bytes = bytes;
            _claimed = claimed;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = Math.Min(count, _bytes.Length - _position);
            Buffer.BlockCopy(_bytes, _position, buffer, offset, n);
            _position += n;
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _claimed;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A stream whose asynchronous write reads the memory it was given only when <see cref="Complete"/> is called.</summary>
    private sealed class PendingWriteStream : Stream
    {
        private readonly TaskCompletionSource<bool> _done = new();
        private ReadOnlyMemory<byte> _pending;

        public byte[] Written { get; private set; }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _pending = buffer;
            return new ValueTask(_done.Task);
        }

        public void Complete()
        {
            Written = _pending.ToArray();
            _done.SetResult(true);
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
