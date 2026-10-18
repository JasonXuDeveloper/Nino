using System;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;

namespace Nino.AspNetCore
{
    /// <summary>Minimal API results with a Nino body.</summary>
    public static class NinoResults
    {
        /// <summary>A 200 response with <paramref name="value"/> as its Nino body.</summary>
        public static NinoResult<T> Ok<T>(T value) => new NinoResult<T>(value, null);

        /// <summary>A response with status <paramref name="statusCode"/> and <paramref name="value"/> as its Nino body.</summary>
        public static NinoResult<T> Status<T>(int statusCode, T value) => new NinoResult<T>(value, statusCode);
    }

    /// <summary>
    /// A Minimal API result that writes <see cref="Value"/> as <see cref="NinoMediaTypes.Application"/>, serialized
    /// as <typeparamref name="T"/> straight into the response pipe. Return it from a handler, or create it with
    /// <see cref="NinoResults"/>; OpenAPI sees its type and content type.
    /// </summary>
    public sealed class NinoResult<T> : IResult, IStatusCodeHttpResult, IValueHttpResult, IValueHttpResult<T>,
        IContentTypeHttpResult, IEndpointMetadataProvider
    {
        internal NinoResult(T value, int? statusCode)
        {
            Value = value;
            StatusCode = statusCode;
        }

        /// <summary>The value written as the body.</summary>
        public T Value { get; }

        object? IValueHttpResult.Value => Value;

        /// <summary>The status code, or null to leave the response's (200 unless something changed it).</summary>
        public int? StatusCode { get; }

        /// <summary><see cref="NinoMediaTypes.Application"/>.</summary>
        public string ContentType => NinoMediaTypes.Application;

        public Task ExecuteAsync(HttpContext httpContext)
        {
            ArgumentNullException.ThrowIfNull(httpContext);
            if (StatusCode is int statusCode)
                httpContext.Response.StatusCode = statusCode;
            return httpContext.Response.WriteAsNinoAsync(Value, httpContext.RequestAborted);
        }

        static void IEndpointMetadataProvider.PopulateMetadata(MethodInfo method, EndpointBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);
            builder.Metadata.Add(new ProducesResponseTypeMetadata(StatusCodes.Status200OK, typeof(T),
                new[] { NinoMediaTypes.Application }));
        }
    }
}
