using System;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Nino.AspNetCore
{
    /// <summary>Adds Nino to MVC.</summary>
    public static class NinoMvcBuilderExtensions
    {
        /// <summary>
        /// Adds <see cref="NinoInputFormatter"/> and <see cref="NinoOutputFormatter"/> after the existing formatters
        /// (JSON stays the default for clients that accept anything) and maps <c>?format=nino</c> to
        /// <see cref="NinoMediaTypes.Application"/>.
        /// </summary>
        public static IMvcBuilder AddNinoFormatters(this IMvcBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);
            builder.Services.Configure<MvcOptions>(AddFormatters);
            return builder;
        }

        /// <inheritdoc cref="AddNinoFormatters(IMvcBuilder)"/>
        public static IMvcCoreBuilder AddNinoFormatters(this IMvcCoreBuilder builder)
        {
            ArgumentNullException.ThrowIfNull(builder);
            builder.Services.Configure<MvcOptions>(AddFormatters);
            return builder;
        }

        private static void AddFormatters(MvcOptions options)
        {
            options.InputFormatters.Add(new NinoInputFormatter());
            options.OutputFormatters.Add(new NinoOutputFormatter());
            options.FormatterMappings.SetMediaTypeMappingForFormat(NinoMediaTypes.Format, NinoMediaTypes.Application);
        }
    }
}
