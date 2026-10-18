using System;
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
        public static async ValueTask<T> ReadFromNinoAsync<T>(this HttpRequest request,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(request);
            if (!request.HasNinoContentType())
                throw new InvalidOperationException(
                    $"Unsupported content type '{request.ContentType}'; expected '{NinoMediaTypes.Application}'.");
            try
            {
                return await NinoBodyReader.ReadAsync<T>(request.BodyReader, request.ContentLength, cancellationToken)
                    .ConfigureAwait(false);
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
            var flush = response.BodyWriter.FlushAsync(cancellationToken);
            return flush.IsCompletedSuccessfully ? Task.CompletedTask : flush.AsTask();
        }
    }
}
