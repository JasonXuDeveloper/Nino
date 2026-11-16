using System;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using Nino.Core;

namespace Nino.AspNetCore
{
    /// <summary>Serializes a response body straight into its pipe, then flushes it.</summary>
    internal static class NinoBodyWriter
    {
        /// <summary>Writes <paramref name="value"/> as <typeparamref name="T"/> (<see cref="NinoHttpExtensions.WriteAsNinoAsync{T}"/>).</summary>
        public static Task WriteAsync<T>(PipeWriter writer, T value, CancellationToken cancellationToken)
        {
            NinoSerializer.Serialize(value, writer);
            return FlushAsync(writer, cancellationToken);
        }

        /// <summary>Writes <paramref name="value"/> as <paramref name="type"/> (<see cref="NinoOutputFormatter"/>).</summary>
        public static Task WriteBoxedAsync(PipeWriter writer, object? value, Type type,
            CancellationToken cancellationToken)
        {
            NinoSerializer.SerializeBoxed(value, writer, type);
            return FlushAsync(writer, cancellationToken);
        }

        /// <summary>Flushes a response pipe, allocating a task only when the flush does not finish at once.</summary>
        private static Task FlushAsync(PipeWriter writer, CancellationToken cancellationToken)
        {
            var flush = writer.FlushAsync(cancellationToken);
            if (!flush.IsCompletedSuccessfully)
                return flush.AsTask();
            flush.GetAwaiter().GetResult();
            return Task.CompletedTask;
        }
    }
}
