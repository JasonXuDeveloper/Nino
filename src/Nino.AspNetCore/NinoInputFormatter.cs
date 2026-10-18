using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc.Formatters;

namespace Nino.AspNetCore
{
    /// <summary>
    /// Reads <see cref="NinoMediaTypes.Application"/> request bodies into action parameters (<c>[FromBody]</c>) for
    /// any type Nino supports. The body is read from the request pipe without being copied when it arrives in one
    /// segment; data Nino cannot read becomes a model state error (a 400 under <c>[ApiController]</c>).
    /// </summary>
    public sealed class NinoInputFormatter : InputFormatter
    {
        public NinoInputFormatter() => SupportedMediaTypes.Add(NinoMediaTypes.Application);

        protected override bool CanReadType(Type type) => NinoBodyReader.IsSupported(type);

        public override async Task<InputFormatterResult> ReadRequestBodyAsync(InputFormatterContext context)
        {
            ArgumentNullException.ThrowIfNull(context);
            var http = context.HttpContext;
            object model;
            try
            {
                model = await NinoBodyReader.ReadBoxedAsync(http.Request.BodyReader, http.Request.ContentLength,
                    context.ModelType, http.RequestAborted).ConfigureAwait(false);
            }
            catch (Exception e) when (NinoBodyReader.IsInvalidData(e))
            {
                throw new InputFormatterException(e.Message, e);
            }

            return model is null && !context.TreatEmptyInputAsDefaultValue
                ? InputFormatterResult.NoValue()
                : InputFormatterResult.Success(model);
        }
    }
}
