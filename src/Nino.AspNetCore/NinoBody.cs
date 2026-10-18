using System;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;

namespace Nino.AspNetCore
{
    /// <summary>
    /// A Minimal API parameter bound from a Nino request body: <c>app.MapPost("/orders", (NinoBody&lt;Order&gt; order)
    /// =&gt; ...)</c>. A request with another content type gets a 415, and a body that is not a valid
    /// <typeparamref name="T"/> a 400. OpenAPI sees the accepted type and content type.
    /// </summary>
    public readonly struct NinoBody<T> : IEndpointParameterMetadataProvider
    {
        public NinoBody(T value) => Value = value;

        /// <summary>The deserialized body.</summary>
        public T Value { get; }

        /// <summary>
        /// Called by Minimal APIs to bind the parameter. A body that is not a valid <typeparamref name="T"/> binds
        /// nothing, which Minimal APIs answer with a 400 (and log); another content type never reaches it, since
        /// routing answers it with a 415 from the parameter's accepts metadata.
        /// </summary>
        public static async ValueTask<NinoBody<T>?> BindAsync(HttpContext context, ParameterInfo parameter)
        {
            ArgumentNullException.ThrowIfNull(context);
            if (!context.Request.HasNinoContentType())
                return null;
            try
            {
                return new NinoBody<T>(await context.Request.ReadFromNinoAsync<T>(context.RequestAborted)
                    .ConfigureAwait(false));
            }
            catch (BadHttpRequestException)
            {
                return null;
            }
        }

        static void IEndpointParameterMetadataProvider.PopulateMetadata(ParameterInfo parameter, EndpointBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);
            builder.Metadata.Add(new AcceptsMetadata(new[] { NinoMediaTypes.Application }, typeof(T)));
        }
    }
}
