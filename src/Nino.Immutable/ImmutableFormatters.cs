using Nino.Core;
using Nino.Immutable;

// Registered like Nino.Core's own BCL formatters: generated code and the runtime resolver both read these.
[assembly: NinoFormatter(typeof(ImmutableArrayFormatter<,>))]
[assembly: NinoFormatter(typeof(ImmutableListFormatter<,>))]
[assembly: NinoFormatter(typeof(ImmutableHashSetFormatter<,>))]
[assembly: NinoFormatter(typeof(ImmutableSortedSetFormatter<,>))]
[assembly: NinoFormatter(typeof(ImmutableQueueFormatter<,>))]
[assembly: NinoFormatter(typeof(ImmutableStackFormatter<,>))]
[assembly: NinoFormatter(typeof(ImmutableDictionaryFormatter<,,,>))]
[assembly: NinoFormatter(typeof(ImmutableSortedDictionaryFormatter<,,,>))]
