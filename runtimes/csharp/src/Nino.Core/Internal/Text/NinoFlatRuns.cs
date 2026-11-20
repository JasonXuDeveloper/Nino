using System;
using System.Runtime.CompilerServices;
using Nino.Core.CompilerServices;

namespace Nino.Core.Internal
{
    // PROTOTYPE (#355, the generator seam): a run of user-type elements whose generated body is flat (raw blocks, bools
    // and strings, no nesting) moved by the string run kernels through a chain of items at runtime offsets, instead of
    // the generated per-element body. Every type of one shape (the sequence of item kinds) shares one instantiation of
    // the kernels: the items hold their offsets, sizes, the header's type id and the constructor as data, never the
    // type itself. A generated formatter opts in through INinoFormatter.HasFlat / WriteFlat / ReadFlat (internal here;
    // public and hidden in the real seam).

    /// <summary>
    /// Consecutive raw members laid out in the object as on the wire (checked once at startup): one copy of
    /// <c>_size</c> bytes at <c>_at</c> in the object's data. Values Untrusted mode checks never reach it (the type
    /// leaves the kernels in that mode).
    /// </summary>
    internal readonly struct NinoBlockItem : INinoRunItem
    {
        private readonly nint _at;
        private readonly int _size;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public NinoBlockItem(int at, int size)
        {
            _at = at;
            _size = size;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int MaxSize(ref byte element) => _size;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int WriteAt(ref byte destination, ref byte element)
        {
            NinoUnsafe.Copy(ref destination, ref Unsafe.AddByteOffset(ref element, _at), (uint)_size);
            return _size;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ReadAt(ref byte source, int available, ref byte element, bool fresh)
        {
            if ((uint)available < (uint)_size)
                NinoThrow.EndOfData();
            NinoUnsafe.Copy(ref Unsafe.AddByteOffset(ref element, _at), ref source, (uint)_size);
            return _size;
        }
    }

    /// <summary>A bool member: one byte, checked on every read (0 or 1), as the generated body checks it.</summary>
    internal readonly struct NinoBoolItem : INinoRunItem
    {
        private readonly nint _at;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public NinoBoolItem(int at) => _at = at;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int MaxSize(ref byte element) => 1;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int WriteAt(ref byte destination, ref byte element)
        {
            destination = Unsafe.AddByteOffset(ref element, _at);
            return 1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ReadAt(ref byte source, int available, ref byte element, bool fresh)
        {
            if (available < 1)
                NinoThrow.EndOfData();
            byte value = source;
            if (value > 1)
                NinoThrow.InvalidBool(value);
            Unsafe.AddByteOffset(ref element, _at) = value;
            return 1;
        }
    }

    /// <summary>
    /// The items composed so far, then one more: both held as values (each item carries its offset and size), so a
    /// chain is data and its type names only the kinds of its items.
    /// </summary>
    internal readonly struct NinoFlatChain<THead, TItem> : INinoRunItem
        where THead : struct, INinoRunItem
        where TItem : struct, INinoRunItem
    {
        private readonly THead _head;
        private readonly TItem _item;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public NinoFlatChain(THead head, TItem item)
        {
            _head = head;
            _item = item;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int MaxSize(ref byte element)
        {
            int first = _head.MaxSize(ref element);
            int second = _item.MaxSize(ref element);
            return (uint)first + (ulong)(uint)second > int.MaxValue ? -1 : first + second;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int WriteAt(ref byte destination, ref byte element)
        {
            int size = _head.WriteAt(ref destination, ref element);
            return size + _item.WriteAt(ref Unsafe.Add(ref destination, (nint)(uint)size), ref element);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ReadAt(ref byte source, int available, ref byte element, bool fresh)
        {
            int size = _head.ReadAt(ref source, available, ref element, fresh);
            return size + _item.ReadAt(ref Unsafe.Add(ref source, (nint)(uint)size), available - size, ref element,
                fresh);
        }
    }

    /// <summary>A string at <c>_at</c> in the object's data, as <see cref="NinoUtf16Item"/> or
    /// <see cref="NinoUtf8Item"/> (<typeparamref name="TString"/>).</summary>
    internal readonly struct NinoStringItem<TString> : INinoRunItem where TString : struct, INinoRunItem
    {
        private readonly nint _at;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public NinoStringItem(int at) => _at = at;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int MaxSize(ref byte element) => default(TString).MaxSize(ref Unsafe.AddByteOffset(ref element, _at));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int WriteAt(ref byte destination, ref byte element) =>
            default(TString).WriteAt(ref destination, ref Unsafe.AddByteOffset(ref element, _at));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ReadAt(ref byte source, int available, ref byte element, bool fresh) =>
            default(TString).ReadAt(ref source, available, ref Unsafe.AddByteOffset(ref element, _at), fresh);
    }

    /// <summary>The object header items: where an element of a class run is an object, and how it is made.</summary>
    internal static class NinoFlatObjects
    {
        /// <summary>The <see cref="INinoRunItem.MaxSize"/> of an element the run leaves (a subtype).</summary>
        public const int Leave = int.MinValue;

        private sealed class RawObject
        {
#pragma warning disable CS0649 // read through Unsafe.As
            public byte Data;
#pragma warning restore CS0649
        }

        /// <summary>The first byte of <paramref name="instance"/>'s fields.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static ref byte Data(object instance) => ref Unsafe.As<RawObject>(instance).Data;

        /// <summary>The method table of <paramref name="instance"/> (CoreCLR: the word before its fields; checked at
        /// startup by <see cref="TypeHandleOf"/>, else the type stays on its generated body).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static nint TypeOf(object instance) =>
            Unsafe.ReadUnaligned<nint>(ref Unsafe.Subtract(ref Data(instance), IntPtr.Size));

        /// <summary>The method table of <paramref name="type"/> as <see cref="TypeOf"/> reads it from
        /// <paramref name="probe"/> (an instance of it), or 0 where this runtime does not keep it there.</summary>
        public static nint TypeHandleOf(Type type, object probe)
        {
            if (probe == null || probe.GetType() != type)
                return 0;
            nint handle = type.TypeHandle.Value;
            return handle != 0 && TypeOf(probe) == handle ? handle : 0;
        }
    }

    /// <summary>
    /// The element of a run of a sealed class: its presence byte (a fixed block item: 0, or 1 then the body), then
    /// its body's items in the object's data. Reads make a new object through <c>_new</c> (the generated
    /// <c>new T()</c>); an element that already holds an object leaves the run (reuse keeps the generated body's
    /// semantics).
    /// </summary>
    internal readonly unsafe struct NinoSealedItem<TBody> : INinoRunItem where TBody : struct, INinoRunItem
    {
        private readonly TBody _body;
        private readonly delegate*<object> _new;

        public NinoSealedItem(TBody body, delegate*<object> create)
        {
            _body = body;
            _new = create;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static object At(ref byte element) => Unsafe.As<byte, object>(ref element);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int MaxSize(ref byte element)
        {
            object value = At(ref element);
            if (value == null)
                return 1;
            int body = _body.MaxSize(ref NinoFlatObjects.Data(value));
            return body < 0 || body == int.MaxValue ? -1 : body + 1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int WriteAt(ref byte destination, ref byte element)
        {
            object value = At(ref element);
            if (value == null)
            {
                destination = NinoWire.Null;
                return 1;
            }

            destination = NinoWire.NotNull;
            return 1 + _body.WriteAt(ref Unsafe.Add(ref destination, 1), ref NinoFlatObjects.Data(value));
        }

        /// <summary>Reads into the element: null, or a new object (one the caller made ahead, <see
        /// cref="NinoFlatRuns.ReadRun{T,TFormatter,TItem}"/>, else one made here).</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ReadAt(ref byte source, int available, ref byte element, bool fresh)
        {
            if (available < 1)
                NinoThrow.EndOfData();
            byte flag = source;
            if (flag != NinoWire.NotNull)
            {
                if (flag != NinoWire.Null)
                    NinoThrow.InvalidPresence(flag);
                Unsafe.As<byte, object>(ref element) = null;
                return 1;
            }

            object value = At(ref element) ?? _new();
            int size = _body.ReadAt(ref Unsafe.Add(ref source, 1), available - 1, ref NinoFlatObjects.Data(value),
                true);
            Unsafe.As<byte, object>(ref element) = value;
            return size + 1;
        }
    }

    /// <summary>
    /// As <see cref="NinoSealedItem{TBody}"/>, a class that is not sealed: its type id (a fixed block item: 0 for
    /// null), its exact type checked per element against <c>_type</c>; a subtype, or another type id on a read,
    /// leaves the run for the generated body.
    /// </summary>
    internal readonly unsafe struct NinoTypedItem<TBody> : INinoRunItem where TBody : struct, INinoRunItem
    {
        private readonly TBody _body;
        private readonly nint _type;
        private readonly int _id;
        private readonly delegate*<object> _new;

        public NinoTypedItem(TBody body, nint type, int id, delegate*<object> create)
        {
            _body = body;
            _type = type;
            _id = id;
            _new = create;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static object At(ref byte element) => Unsafe.As<byte, object>(ref element);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int MaxSize(ref byte element)
        {
            object value = At(ref element);
            if (value == null)
                return 4;
            if (NinoFlatObjects.TypeOf(value) != _type)
                return NinoFlatObjects.Leave;
            int body = _body.MaxSize(ref NinoFlatObjects.Data(value));
            return body < 0 || body > int.MaxValue - 4 ? -1 : body + 4;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int WriteAt(ref byte destination, ref byte element)
        {
            object value = At(ref element);
            if (value == null)
            {
                Unsafe.WriteUnaligned(ref destination, NinoWire.NullTypeId);
                return 4;
            }

            Unsafe.WriteUnaligned(ref destination, _id);
            return 4 + _body.WriteAt(ref Unsafe.Add(ref destination, 4), ref NinoFlatObjects.Data(value));
        }

        /// <summary>As <see cref="NinoSealedItem{TBody}.ReadAt"/>; returns -1 (nothing read) where the id is another
        /// type's.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public int ReadAt(ref byte source, int available, ref byte element, bool fresh)
        {
            if (available < 4)
                NinoThrow.EndOfData();
            int id = Unsafe.ReadUnaligned<int>(ref source);
            if (id != _id)
            {
                if (id != NinoWire.NullTypeId)
                    return -1;
                Unsafe.As<byte, object>(ref element) = null;
                return 4;
            }

            object value = At(ref element) ?? _new();
            int size = _body.ReadAt(ref Unsafe.Add(ref source, 4), available - 4, ref NinoFlatObjects.Data(value),
                true);
            Unsafe.As<byte, object>(ref element) = value;
            return size + 4;
        }
    }

    /// <summary>
    /// The kernels of runs of flat objects: the string run kernels' write loop (<see cref="NinoStringRuns.WriteFitting"/>)
    /// and a read loop, both leaving the run at an element the items do not take (a subtype, an existing object),
    /// which the caller writes or reads through the generated body and then goes on. Generic over the element's item
    /// alone, never over the type.
    /// </summary>
    internal static class NinoFlatRuns
    {
        /// <summary>
        /// Writes <paramref name="run"/>'s elements from <paramref name="next"/> on until one leaves the run
        /// (<paramref name="next"/> is then its index) or all are written (<paramref name="next"/> the count).
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static Writer Write<TItem>(scoped NinoRun run, TItem item, scoped ref int next, Writer writer)
            where TItem : struct, INinoRunItem
        {
            int from = next;
            while (true)
            {
                ulong left = NinoStringRuns.WriteFitting(run, item, from, ref writer.FreeSpace(out int available),
                    available);
                writer.Commit(available - (int)left);
                int entries = (int)(left >> 32);
                from = run.Count - entries;
                if (entries == 0)
                    break;
                int room = item.MaxSize(ref Unsafe.Add(ref run.First, (nint)from * run.Stride));
                if (room == NinoFlatObjects.Leave)
                    break;
                writer.Ensure(room);
            }

            next = from;
            return writer;
        }

        /// <summary>
        /// Reads <paramref name="run"/>'s elements from <paramref name="next"/> on from the data at
        /// <paramref name="data"/> (<paramref name="end"/> bytes) until one leaves the run (<paramref name="next"/> is
        /// then its index) or all are read; returns the bytes read.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static int Read<TItem>(scoped NinoRun run, TItem item, scoped ref int next, ref byte data, int end)
            where TItem : struct, INinoRunItem
        {
            ref byte source = ref data;
            int stride = run.Stride, available = end;
            ref byte element = ref Unsafe.Add(ref run.First, (nint)next * stride);
            int left = run.Count - next;
            for (; left > 0; left--)
            {
                int read = item.ReadAt(ref source, available, ref element, false);
                if (read < 0)
                    break;
                source = ref Unsafe.Add(ref source, (nint)(uint)read);
                available -= read;
                element = ref NinoRun.Next(ref element, stride);
            }

            next = run.Count - left;
            return end - available;
        }

        /// <summary>
        /// The write of a run of <typeparamref name="T"/> (not empty) through <paramref name="item"/>, an element the
        /// items leave written by <typeparamref name="TFormatter"/>'s generated body: what a generated
        /// <see cref="INinoFormatter.WriteFlat"/> calls.
        /// </summary>
        public static Writer WriteRun<T, TFormatter, TItem>(scoped NinoRun run, TItem item, Writer writer)
            where T : class
            where TFormatter : struct, INinoFormatter<T>
            where TItem : struct, INinoRunItem
        {
            int count = run.Count, stride = run.Stride;
            // Bodies too deep are deferred by the generated body: the whole run goes that way.
            if (writer.Depth >= NinoDepth.Inline)
                return NinoRuns.WriteEach<T, TFormatter>(run, NinoRuns.NoLength, writer);
            int next = 0;
            while (true)
            {
                writer = Write(run, item, ref next, writer);
                if (next >= count)
                    return writer;
                default(TFormatter).Serialize(Unsafe.As<byte, T>(ref Unsafe.Add(ref run.First, (nint)next * stride)),
                    ref writer);
                if (++next == count)
                    return writer;
            }
        }

        /// <summary>As <see cref="WriteRun{T,TFormatter,TItem}"/>, the read in place (a run in a row, every one in
        /// use).</summary>
        /// <remarks>
        /// A run that holds an object anywhere (a read into existing elements) is read by the generated body, which
        /// keeps or recycles each as reuse does. Otherwise each element is a new object: made by
        /// <paramref name="fill"/> (the generated <c>new T()</c> in a loop, inline) a chunk ahead of the kernel,
        /// so the kernel makes no call to construct, or else by the item's constructor pointer per element.
        /// </remarks>
        public static unsafe Reader ReadRun<T, TFormatter, TItem>(scoped NinoRun run, TItem item, Reader reader,
            delegate*<ref byte, int, void> fill = null)
            where T : class
            where TFormatter : struct, INinoFormatter<T>
            where TItem : struct, INinoRunItem
        {
            int count = run.Count, stride = run.Stride;
            if (reader.Depth >= NinoDepth.Inline || Holds(ref run.First, count, stride))
            {
                NinoRuns.ReadLoop<T, TFormatter>(ref run.First, count, stride, ref reader);
                return reader;
            }

            int next = 0;
            while (true)
            {
                int end = count;
                if (fill != null)
                {
                    end = Math.Min(count, next + FillChunk);
                    fill(ref Unsafe.Add(ref run.First, (nint)next * stride), end - next);
                }

                int at = next;
                reader.UnsafeAdvance(Read(new NinoRun(ref run.First, end, stride, NinoRun.NoKey, NinoRun.AllInUse),
                    item, ref at, ref reader.Peek(), reader.Remaining));
                if (at < end)
                {
                    // Another type's id: the object made ahead is dropped, the generated body reads the element.
                    ref T element = ref Unsafe.As<byte, T>(ref Unsafe.Add(ref run.First, (nint)at * stride));
                    element = null;
                    default(TFormatter).Deserialize(ref reader, ref element);
                    at++;
                }

                if (at >= count)
                    return reader;
                // After a leave the next chunk is made from it again: the objects made ahead past it are dropped.
                next = at;
            }
        }

        /// <summary>Elements made ahead of the kernel at a time: bounds the objects made for data not yet read.</summary>
        private const int FillChunk = 64;

        private static bool Holds(ref byte first, int count, int stride)
        {
            for (int i = 0; i < count; i++)
                if (Unsafe.As<byte, object>(ref Unsafe.Add(ref first, (nint)i * stride)) != null)
                    return true;
            return false;
        }
    }
}
