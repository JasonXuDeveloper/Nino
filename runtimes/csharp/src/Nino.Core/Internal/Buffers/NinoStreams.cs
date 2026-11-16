using System;
using System.Buffers;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Nino.Core.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Nino.Core.Internal
{
    /// <summary>
    /// What <see cref="NinoStreams"/> reads the bytes of a whole stream with: a struct, so the call is direct (the
    /// regular span call, or the formatter an interceptor resolved).
    /// </summary>
    internal interface INinoStreamReader<T>
    {
        void Read(ReadOnlySpan<byte> data, ref T value);
    }

    /// <summary>
    /// The I/O of the <see cref="System.IO.Stream"/> overloads of <see cref="NinoSerializer"/>,
    /// <see cref="NinoDeserializer"/> and <c>NinoDirect</c>, shared by all of them. A write puts exactly the bytes of
    /// the span and buffer overloads on the stream, from the per-thread scratch buffer (<see cref="NinoScratch"/>),
    /// with no framing. A read takes the stream to its end into an array rented from
    /// <see cref="ArrayPool{T}.Shared"/> (a stream whose bytes are already in memory is read in place: a
    /// <see cref="MemoryStream"/> whose buffer is exposed, a pointer-backed <see cref="UnmanagedMemoryStream"/> of
    /// that exact type, or a memory-mapped view), then deserializes it as the span overloads do. The initial rental
    /// uses a capped length hint, then the array doubles when full. All span checks apply, including
    /// <see cref="NinoSettings.Untrusted"/>. The caller bounds streams from untrusted peers (a length limit, a frame).
    /// </summary>
    internal static class NinoStreams
    {
        /// <summary>The first array of a stream whose length is unknown.</summary>
        private const int FirstRead = 4096;

        /// <summary>
        /// The most a seekable stream's <see cref="Stream.Length"/> makes the first array hold before a byte is read:
        /// a length is a claim, and a stream over a peer's data (a ranged download) may claim anything.
        /// </summary>
        private const int MaxFirstRead = 1 << 20;

        /// <summary>Writes <paramref name="value"/> with <paramref name="writer"/>, then the bytes to <paramref name="stream"/>.</summary>
        public static void Write<T, TWriter>(T value, TWriter writer, Stream stream)
            where TWriter : struct, INinoScratchWriter<T>
        {
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));
            var buffer = NinoScratch.Rent();
            try
            {
                writer.Write(value, buffer);
                // The array overload everywhere: Stream.Write(ReadOnlySpan) copies into a rented array on a stream
                // that does not override it.
                stream.Write(buffer.Buffer, 0, buffer.Index);
            }
            finally
            {
                NinoScratch.Return(buffer);
            }
        }

        /// <summary>
        /// Reads <paramref name="stream"/> to its end and deserializes the bytes into <paramref name="value"/> with
        /// <paramref name="reader"/>.
        /// </summary>
        public static void Read<T, TReader>(Stream stream, ref T value, TReader reader)
            where TReader : struct, INinoStreamReader<T>
        {
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));
            if (TryReadInPlace(stream, ref value, reader))
                return;

            byte[] rented = ReadToEnd(stream, out int length);
            Read(rented, length, ref value, reader);
        }

        /// <summary>Deserializes the first <paramref name="length"/> bytes of <paramref name="rented"/>, then returns it to the pool.</summary>
        private static void Read<T, TReader>(byte[] rented, int length, ref T value, TReader reader)
            where TReader : struct, INinoStreamReader<T>
        {
            try
            {
                reader.Read(new ReadOnlySpan<byte>(rented, 0, length), ref value);
            }
            finally
            {
                // Nothing a read creates keeps a reference to its input (NinoSequences), so the array goes back now.
                ArrayPool<byte>.Shared.Return(rented);
            }
        }

        /// <summary>
        /// Deserializes the unread bytes of a stream that already holds them in memory, where they lie, with no copy:
        /// a <see cref="MemoryStream"/> whose buffer is exposed (<see cref="MemoryStream.TryGetBuffer"/>; not one made
        /// with <c>new MemoryStream(bytes)</c>), a pointer-backed <see cref="UnmanagedMemoryStream"/> of that exact
        /// type, and a <see cref="MemoryMappedViewStream"/>. The stream is consumed before deserializing.
        /// False for any other stream, which is then read to its end into a pooled array.
        /// </summary>
        private static bool TryReadInPlace<T, TReader>(Stream stream, ref T value, TReader reader)
            where TReader : struct, INinoStreamReader<T>
        {
            if (stream is MemoryStream memory)
            {
                if (!memory.TryGetBuffer(out var segment))
                    return false;
                reader.Read(Consume(memory, segment), ref value);
                return true;
            }

            if (stream is UnmanagedMemoryStream unmanaged)
            {
                // A derived stream may override Read, Length or Position.
                var type = unmanaged.GetType();
                if ((type == typeof(UnmanagedMemoryStream) || type == typeof(MemoryMappedViewStream)) &&
                    unmanaged.CanRead)
                    return ReadUnmanaged(unmanaged, ref value, reader);
            }
            return false;
        }

        /// <summary>
        /// <see cref="TryReadInPlace{T,TReader}"/> for an <see cref="UnmanagedMemoryStream"/>: its memory through
        /// <see cref="UnmanagedMemoryStream.PositionPointer"/>, or, for a memory-mapped view (whose
        /// <see cref="SafeBuffer"/> has no position pointer), through the view's handle, held while the bytes are read.
        /// False for a stream over another <see cref="SafeBuffer"/>, whose start is not public.
        /// </summary>
        private static unsafe bool ReadUnmanaged<T, TReader>(UnmanagedMemoryStream stream, ref T value, TReader reader)
            where TReader : struct, INinoStreamReader<T>
        {
            long position = stream.Position;
            long length = stream.Length;
            long left = position < length ? length - position : 0;
            if (left > NinoUnsafe.MaxArrayLength)
                NinoThrow.InvalidData("the stream holds more bytes than an array can.");
            if (left == 0)
            {
                // At or past its end (where PositionPointer would throw): no bytes, which the read rejects as truncated.
                reader.Read(default, ref value);
                return true;
            }

            if (stream is MemoryMappedViewStream view)
            {
                var handle = view.SafeMemoryMappedViewHandle;
                byte* start = null;
                handle.AcquirePointer(ref start);
                try
                {
                    // The view's bytes start PointerOffset past the mapped pages (the offset rounded down to them).
                    stream.Position = length;
                    reader.Read(new ReadOnlySpan<byte>(start + view.PointerOffset + position, (int)left), ref value);
                }
                finally
                {
                    handle.ReleasePointer();
                }

                return true;
            }

            byte* at;
            try
            {
                at = stream.PositionPointer;
            }
            catch (NotSupportedException)
            {
                // Created over a SafeBuffer: read to its end like any other stream.
                return false;
            }

            stream.Position = length;
            reader.Read(new ReadOnlySpan<byte>(at, (int)left), ref value);
            return true;
        }

        /// <summary>
        /// The unread bytes of a <see cref="MemoryStream"/> whose buffer is exposed, read in place with no copy; the
        /// stream is left at its end, as a read to the end leaves any other.
        /// </summary>
        private static ReadOnlySpan<byte> Consume(MemoryStream memory, ArraySegment<byte> segment)
        {
            // The segment is the stream's bytes from its origin to its length; Position counts from that origin and
            // may lie past the end.
            long position = memory.Position;
            int start = position < segment.Count ? (int)position : segment.Count;
            if (start < segment.Count)
                memory.Position = segment.Count;
            return new ReadOnlySpan<byte>(segment.Array, segment.Offset + start, segment.Count - start);
        }

        /// <summary>
        /// Reads <paramref name="stream"/> to its end into an array rented from <see cref="ArrayPool{T}.Shared"/>, its
        /// first <paramref name="length"/> bytes the stream's; the caller returns it.
        /// </summary>
        private static byte[] ReadToEnd(Stream stream, out int length)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(FirstSize(stream));
            int count = 0;
            try
            {
                while (true)
                {
                    int read;
                    if (count == buffer.Length)
                    {
                        if (AtEnd(stream))
                            break;
                        read = ReadPastFullBuffer(stream, ref buffer, count);
                    }
                    else
                        read = stream.Read(buffer, count, buffer.Length - count);
                    if (read <= 0)
                        break;
                    count += read;
                }
            }
            catch
            {
                ArrayPool<byte>.Shared.Return(buffer);
                throw;
            }

            length = count;
            return buffer;
        }

        /// <summary>The size of the first array: what a seekable stream has left (at most <see cref="MaxFirstRead"/>).</summary>
        private static int FirstSize(Stream stream)
        {
            if (!stream.CanSeek)
                return FirstRead;
            long left = stream.Length - stream.Position;
            // At least 1: a stream that says it is empty is still read once (to its end).
            return left <= 0 ? 1 : left < MaxFirstRead ? (int)left : MaxFirstRead;
        }

        /// <summary>
        /// Whether a full array has taken the whole stream: known only on a seekable one, which then needs no larger
        /// array to see its end. Any other is grown and read once more.
        /// </summary>
        private static bool AtEnd(Stream stream) => stream.CanSeek && stream.Position >= stream.Length;

        // Probe the next buffer before copying the prefix. A non-seekable stream may have ended exactly
        // at the old buffer's boundary.
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int ReadPastFullBuffer(Stream stream, ref byte[] buffer, int count)
        {
            byte[] larger = RentLarger(buffer.Length, count);
            bool keep = false;
            try
            {
                int read = stream.Read(larger, count, larger.Length - count);
                if (read <= 0)
                    return read;

                CopyPrefix(buffer, larger, count);
                keep = true;
                return read;
            }
            finally
            {
                if (keep)
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = larger;
                }
                else
                    ArrayPool<byte>.Shared.Return(larger);
            }
        }

        /// <summary>The <paramref name="count"/> bytes read so far, into the larger array that takes them over.</summary>
        private static void CopyPrefix(byte[] buffer, byte[] larger, int count) =>
            NinoUnsafe.CopyToNew(ref NinoUnsafe.GetArrayDataReference(larger),
                ref NinoUnsafe.GetArrayDataReference(buffer), (uint)count);

        private static byte[] RentLarger(int capacity, int count)
        {
            if (count >= NinoUnsafe.MaxArrayLength)
                NinoThrow.InvalidData("the stream holds more bytes than an array can.");
            int size = (int)Math.Min((long)capacity * 2, NinoUnsafe.MaxArrayLength);
            return ArrayPool<byte>.Shared.Rent(size);
        }

        /// <summary>An array twice as large holding the <paramref name="count"/> bytes read; the old one goes back to the pool.</summary>
        private static byte[] Grow(byte[] buffer, int count)
        {
            byte[] larger = RentLarger(buffer.Length, count);
            CopyPrefix(buffer, larger, count);
            ArrayPool<byte>.Shared.Return(buffer);
            return larger;
        }
        /// <summary>
        /// <see cref="Write{T,TWriter}"/> with the stream's asynchronous write. The value is written into the scratch
        /// buffer at once (a <see cref="Writer"/> cannot live in an async method); a stream write that completes at
        /// once (a <see cref="MemoryStream"/>, a buffered <see cref="FileStream"/>) then allocates nothing. One that
        /// is still pending takes the scratch buffer's array with it until it completes, and the scratch buffer goes
        /// on with another of the same size, so other calls on this thread are not held up.
        /// </summary>
        public static ValueTask WriteAsync<T, TWriter>(T value, TWriter writer, Stream stream,
            CancellationToken cancellationToken)
            where TWriter : struct, INinoScratchWriter<T>
        {
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));
            if (cancellationToken.IsCancellationRequested)
                return new ValueTask(Task.FromCanceled(cancellationToken));
            var buffer = NinoScratch.Rent();
            try
            {
                writer.Write(value, buffer);
                var pending = stream.WriteAsync(buffer.WrittenMemory, cancellationToken);
                if (pending.IsCompleted)
                    return pending;
                // The scratch buffer's arrays are rented from ArrayPool<byte>.Shared (NinoScratch).
                return Complete(pending, buffer.TakeBuffer());
            }
            catch (Exception e)
            {
                // As an async method would: the error is the task's.
                return new ValueTask(Task.FromException(e));
            }
            finally
            {
                NinoScratch.Return(buffer);
            }
        }

        private static async ValueTask Complete(ValueTask pending, byte[] taken)
        {
            try
            {
                await pending.ConfigureAwait(false);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(taken);
            }
        }

        /// <summary>
        /// <see cref="Read{T,TReader}(Stream, ref T, TReader)"/> into a new value with the stream's asynchronous
        /// reads. Reads that complete at once (a <see cref="MemoryStream"/>, whose exposed buffer is read in place)
        /// allocate nothing but the value.
        /// </summary>
        public static ValueTask<T> ReadAsync<T, TReader>(Stream stream, TReader reader,
            CancellationToken cancellationToken)
            where TReader : struct, INinoStreamReader<T>
        {
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));
            if (cancellationToken.IsCancellationRequested)
                return new ValueTask<T>(Task.FromCanceled<T>(cancellationToken));
            try
            {
                T value = default;
                if (TryReadInPlace(stream, ref value, reader))
                    return new ValueTask<T>(value);
            }
            catch (Exception e)
            {
                return new ValueTask<T>(Task.FromException<T>(e));
            }

            return ReadAsyncCore<T, TReader>(stream, reader, cancellationToken);
        }

        private static async ValueTask<T> ReadAsyncCore<T, TReader>(Stream stream, TReader reader,
            CancellationToken cancellationToken)
            where TReader : struct, INinoStreamReader<T>
        {
            var (rented, length) = await ReadToEndAsync(stream, cancellationToken).ConfigureAwait(false);
            T value = default;
            Read(rented, length, ref value, reader);
            return value;
        }

        /// <summary><see cref="ReadToEnd"/> with the stream's asynchronous reads; not generic, so one copy serves every type.</summary>
        private static async ValueTask<(byte[] Rented, int Length)> ReadToEndAsync(Stream stream,
            CancellationToken cancellationToken)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(FirstSize(stream));
            int count = 0;
            try
            {
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (count == buffer.Length)
                    {
                        if (AtEnd(stream))
                            break;
                        buffer = Grow(buffer, count);
                    }

                    int read = await stream.ReadAsync(buffer.AsMemory(count), cancellationToken).ConfigureAwait(false);
                    if (read <= 0)
                        break;
                    count += read;
                }
            }
            catch
            {
                ArrayPool<byte>.Shared.Return(buffer);
                throw;
            }

            return (buffer, count);
        }
    }
}
