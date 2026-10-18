using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Pipelines;
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

        public static async ValueTask<T> ReadAsync<T>(PipeReader reader, long? length, CancellationToken cancellationToken)
        {
            var body = await ReadToEndAsync(reader, length, cancellationToken).ConfigureAwait(false);
            try
            {
                return Read(body, typeof(T), static (data, _) => NinoDeserializer.Deserialize<T>(data));
            }
            finally
            {
                reader.AdvanceTo(body.End);
            }
        }

        public static async ValueTask<object> ReadBoxedAsync(PipeReader reader, long? length, Type type,
            CancellationToken cancellationToken)
        {
            var body = await ReadToEndAsync(reader, length, cancellationToken).ConfigureAwait(false);
            try
            {
                return Read(body, type, static (data, t) => NinoDeserializer.DeserializeBoxed(data, t));
            }
            finally
            {
                reader.AdvanceTo(body.End);
            }
        }

        /// <summary>
        /// Buffers the body in the pipe until it is complete; the caller advances past it. With a
        /// <c>Content-Length</c> it waits for that many bytes at once instead of waking on every segment.
        /// </summary>
        private static async ValueTask<ReadOnlySequence<byte>> ReadToEndAsync(PipeReader reader, long? length,
            CancellationToken cancellationToken)
        {
            var result = length is > 0 and <= int.MaxValue
                ? await reader.ReadAtLeastAsync((int)length.Value, cancellationToken).ConfigureAwait(false)
                : await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            while (true)
            {
                var buffer = result.Buffer;
                if (result.IsCompleted || buffer.Length >= length)
                    return buffer;
                if (result.IsCanceled)
                    throw new OperationCanceledException(cancellationToken);
                reader.AdvanceTo(buffer.Start, buffer.End);
                result = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
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
