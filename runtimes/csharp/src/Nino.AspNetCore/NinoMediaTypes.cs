using System;
using Microsoft.AspNetCore.Http;

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
            return IsNino(request.ContentType);
        }

        /// <summary>
        /// Whether a <c>Content-Type</c> value is <see cref="Application"/>, ignoring case, whitespace and parameters,
        /// without parsing it into objects (it is checked on every request).
        /// </summary>
        internal static bool IsNino(string? contentType)
        {
            var value = contentType.AsSpan();
            int semicolon = value.IndexOf(';');
            if (semicolon >= 0)
                value = value.Slice(0, semicolon);
            return value.Trim().Equals(Application, StringComparison.OrdinalIgnoreCase);
        }
    }
}
