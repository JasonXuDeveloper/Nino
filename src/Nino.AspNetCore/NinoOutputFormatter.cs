using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Formatters;
using Nino.Core;

namespace Nino.AspNetCore
{
    /// <summary>
    /// Writes action results as <see cref="NinoMediaTypes.Application"/> when the client asks for it
    /// (<c>Accept</c>, or <c>?format=nino</c> with <c>[FormatFilter]</c>), for any type Nino supports. Values are
    /// serialized as the declared type (<c>ActionResult&lt;TBase&gt;</c> writes the type id of a subtype) straight
    /// into the response pipe.
    /// </summary>
    public sealed class NinoOutputFormatter : OutputFormatter
    {
        public NinoOutputFormatter() => SupportedMediaTypes.Add(NinoMediaTypes.Application);

        protected override bool CanWriteType(Type? type) => type is not null && NinoBodyReader.IsSupported(type);

        public override Task WriteResponseBodyAsync(OutputFormatterWriteContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            var response = context.HttpContext.Response;
            var type = context.ObjectType ?? context.Object?.GetType() ?? typeof(object);
            NinoSerializer.SerializeBoxed(context.Object, response.BodyWriter, type);
            return NinoBodyReader.FlushAsync(response.BodyWriter, context.HttpContext.RequestAborted);
        }
    }
}
