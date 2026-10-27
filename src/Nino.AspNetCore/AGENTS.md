# Nino.AspNetCore: file map

> Read when: reading or changing the ASP.NET Core integration (package Nino.AspNetCore).

net8.0 and net10.0 library (trimmable, NativeAOT compatible), packed as `Nino.AspNetCore`: MVC input and output
formatters and Minimal API bodies and results, read from the request pipe and written to the response pipe.
`audit-core` audits it; tests in `tests/Nino.AspNetCore.Tests`.

```
Nino.AspNetCore.csproj       targets, the Nino.Core reference, the Microsoft.AspNetCore.App framework reference
NinoMediaTypes.cs            application/x-nino (Application), the format name nino, HasNinoContentType
NinoMvcBuilderExtensions.cs  AddNinoFormatters() on IMvcBuilder / IMvcCoreBuilder
NinoInputFormatter.cs        MVC input formatter: [FromBody] parameters; invalid data is a model state error (400)
NinoOutputFormatter.cs       MVC output formatter: results as the declared type when the client asks for Nino
NinoBody.cs                  NinoBody<T>: a Minimal API parameter bound from a Nino body (415 / 400, OpenAPI metadata)
NinoResults.cs               NinoResults.Ok / Status and NinoResult<T>: Minimal API results with a Nino body
NinoHttpExtensions.cs        ReadFromNinoAsync<T> on HttpRequest, WriteAsNinoAsync<T> on HttpResponse
NinoBodyReader.cs            reads a whole body from its PipeReader and deserializes it without copying a contiguous body
```

## Rules for these files

- Data Nino rejects (`NinoDataException`, `NinoBodyReader.IsInvalidData`) becomes a 400 (BadHttpRequestException, a
  failed NinoBody bind) or a model state error, never a 500.
- Reading a body and writing a response allocate nothing of their own (`AllocationTests`).
