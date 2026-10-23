using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Nino.Core;

namespace Nino.AspNetCore
{
    /// <summary>Reads a whole request body from its pipe and deserializes it without copying a contiguous body.</summary>
    internal static class NinoBodyReader
    {
        private static readonly ConcurrentDictionary<Type, bool> Supported = new ConcurrentDictionary<Type, bool>();

        private delegate TResult SpanReader<TResult>(ReadOnlySpan<byte> data, Type type);

        /// <summary><see cref="NinoSerializer.IsSupported"/>, cached: MVC asks for every request.</summary>
        public static bool IsSupported(Type type) => Supported.GetOrAdd(type, static t => NinoSerializer.IsSupported(t));

        /// <summary>
        /// Whether <paramref name="exception"/> is what Nino throws for data it cannot read (truncated, corrupt or
        /// written for another type), as opposed to a bug: the client's fault, so a 400.
        /// </summary>
        public static bool IsInvalidData(Exception exception) =>
            exception is InvalidOperationException or OverflowException or IndexOutOfRangeException;

        // A body that is already in the pipe (the common case: small bodies arrive with the headers) is read without
        // an async method, so nothing is allocated whether or not the compiler made its state machines structs (it
        // makes them classes, allocated on every call, in a build without optimizations). A body that has not fully
        // arrived is awaited by an async method whose state machine comes from a pool, so it allocates nothing either.
        // Data Nino cannot read throws synchronously on the first path and from the task on the second.
        public static ValueTask<T> ReadAsync<T>(PipeReader reader, long? length, CancellationToken cancellationToken) =>
            ReadBodyAsync(reader, length, typeof(T), static (data, _) => NinoDeserializer.Deserialize<T>(data),
                cancellationToken);

        public static ValueTask<object> ReadBoxedAsync(PipeReader reader, long? length, Type type,
            CancellationToken cancellationToken) =>
            ReadBodyAsync(reader, length, type, static (data, t) => NinoDeserializer.DeserializeBoxed(data, t),
                cancellationToken);

        private static ValueTask<TResult> ReadBodyAsync<TResult>(PipeReader reader, long? length, Type type,
            SpanReader<TResult> read, CancellationToken cancellationToken)
        {
            var pending = StartRead(reader, length, cancellationToken);
            if (!pending.IsCompletedSuccessfully)
                return ReadRestAsync(reader, length, type, read, pending, cancellationToken);
            var result = pending.Result;
            return IsWhole(result, length)
                ? new ValueTask<TResult>(ReadAndAdvance(reader, result.Buffer, type, read))
                : ReadRestAsync(reader, length, type, read, new ValueTask<ReadResult>(result), cancellationToken);
        }

        /// <summary>With a <c>Content-Length</c>, waits for that many bytes at once instead of waking on every segment.</summary>
        private static ValueTask<ReadResult> StartRead(PipeReader reader, long? length,
            CancellationToken cancellationToken) =>
            length is > 0 and <= int.MaxValue
                ? reader.ReadAtLeastAsync((int)length.Value, cancellationToken)
                : reader.ReadAsync(cancellationToken);

        /// <summary>Buffers the body in the pipe until it is complete, then reads it.</summary>
        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
        private static async ValueTask<TResult> ReadRestAsync<TResult>(PipeReader reader, long? length, Type type,
            SpanReader<TResult> read, ValueTask<ReadResult> pending, CancellationToken cancellationToken)
        {
            while (true)
            {
                var result = await pending.ConfigureAwait(false);
                if (IsWhole(result, length))
                    return ReadAndAdvance(reader, result.Buffer, type, read);
                if (result.IsCanceled)
                    throw new OperationCanceledException(cancellationToken);
                reader.AdvanceTo(result.Buffer.Start, result.Buffer.End);
                pending = reader.ReadAsync(cancellationToken);
            }
        }

        private static bool IsWhole(in ReadResult result, long? length) =>
            result.IsCompleted || result.Buffer.Length >= length;

        private static TResult ReadAndAdvance<TResult>(PipeReader reader, in ReadOnlySequence<byte> body, Type type,
            SpanReader<TResult> read)
        {
            try
            {
                return Read(body, type, read);
            }
            finally
            {
                reader.AdvanceTo(body.End);
            }
        }

        /// <summary>Flushes a response pipe, allocating a task only when the flush does not finish at once.</summary>
        public static Task FlushAsync(PipeWriter writer, CancellationToken cancellationToken)
        {
            var flush = writer.FlushAsync(cancellationToken);
            if (!flush.IsCompletedSuccessfully)
                return flush.AsTask();
            flush.GetAwaiter().GetResult();
            return Task.CompletedTask;
        }

        // A body that arrived in one segment is read where it is; only a body split across segments is copied, into
        // a pooled buffer (Nino reads spans, and values never keep a reference to it).
        private static TResult Read<TResult>(in ReadOnlySequence<byte> body, Type type, SpanReader<TResult> read)
        {
            if (body.IsSingleSegment)
                return read(body.FirstSpan, type);
            var length = checked((int)body.Length);
            var buffer = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                body.CopyTo(buffer);
                return read(buffer.AsSpan(0, length), type);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }
}
