using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nino.Core;

namespace Nino.AspNetCore
{
    /// <summary>Reads and writes Nino bodies on <see cref="HttpRequest"/> and <see cref="HttpResponse"/>.</summary>
    public static class NinoHttpExtensions
    {
        /// <summary>
        /// Reads the whole request body as one <typeparamref name="T"/>. The body is deserialized where the request
        /// pipe holds it, and copied once (into a pooled buffer) only when it arrived in several segments.
        /// </summary>
        /// <exception cref="InvalidOperationException">The request's content type is not <see cref="NinoMediaTypes.Application"/>.</exception>
        /// <exception cref="BadHttpRequestException">The body is not a valid <typeparamref name="T"/> (status 400).</exception>
        public static ValueTask<T> ReadFromNinoAsync<T>(this HttpRequest request,
            CancellationToken cancellationToken = default)
        {
            // A body that is already in the pipe is read without an async method, so nothing is allocated in any
            // build; a body still arriving is awaited by a pooled one. Every exception still comes out of the task.
            if (request is null)
                return ValueTask.FromException<T>(new ArgumentNullException(nameof(request)));
            if (!request.HasNinoContentType())
                return ValueTask.FromException<T>(new InvalidOperationException(
                    $"Unsupported content type '{request.ContentType}'; expected '{NinoMediaTypes.Application}'."));
            ValueTask<T> read;
            try
            {
                read = NinoBodyReader.ReadAsync<T>(request.BodyReader, request.ContentLength, cancellationToken);
            }
            catch (Exception e)
            {
                // Through the async method, so an OperationCanceledException cancels the task as it always did.
                read = ValueTask.FromException<T>(e);
            }

            return read.IsCompletedSuccessfully ? read : AwaitBodyAsync(read);
        }

        /// <summary>Awaits a body that has not fully arrived, turning data Nino cannot read into a 400.</summary>
        [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
        private static async ValueTask<T> AwaitBodyAsync<T>(ValueTask<T> read)
        {
            try
            {
                return await read.ConfigureAwait(false);
            }
            catch (Exception e) when (NinoBodyReader.IsInvalidData(e))
            {
                throw new BadHttpRequestException("Invalid Nino request body: " + e.Message,
                    StatusCodes.Status400BadRequest, e);
            }
        }

        /// <summary>
        /// Writes <paramref name="value"/> as the response body (<see cref="NinoMediaTypes.Application"/>), serialized
        /// straight into the response pipe.
        /// </summary>
        public static Task WriteAsNinoAsync<T>(this HttpResponse response, T value,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(response);
            response.ContentType = NinoMediaTypes.Application;
            NinoSerializer.Serialize(value, response.BodyWriter);
            return NinoBodyReader.FlushAsync(response.BodyWriter, cancellationToken);
        }
    }
}
