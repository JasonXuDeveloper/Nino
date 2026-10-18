using System;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace Nino.AspNetCore
{
    /// <summary>The media type of Nino bodies.</summary>
    public static class NinoMediaTypes
    {
        /// <summary><c>application/x-nino</c>: the request or response body is one value written by Nino.</summary>
        public const string Application = "application/x-nino";

        /// <summary>The format name MVC maps to <see cref="Application"/> (<c>?format=nino</c>, <c>[FormatFilter]</c>).</summary>
        public const string Format = "nino";

        /// <summary>Whether the request's <c>Content-Type</c> is <see cref="Application"/> (parameters ignored).</summary>
        public static bool HasNinoContentType(this HttpRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            return MediaTypeHeaderValue.TryParse(request.ContentType, out var contentType) &&
                   contentType.MediaType.Equals(Application, StringComparison.OrdinalIgnoreCase);
        }
    }
}
