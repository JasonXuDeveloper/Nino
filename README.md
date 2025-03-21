# Nino

Ultimate high-performance binary serialization library for C#.

![build](https://img.shields.io/github/actions/workflow/status/JasonXuDeveloper/Nino/.github/workflows/ci.yml?branch=main)
![license](https://img.shields.io/github/license/JasonXuDeveloper/Nino)
[![nino.nuget](https://img.shields.io/nuget/v/Nino?label=Nino)](https://www.nuget.org/packages/Nino)
[![openupm](https://img.shields.io/npm/v/com.jasonxudeveloper.nino?label=openupm&registry_uri=https://package.openupm.com)](https://openupm.com/packages/com.jasonxudeveloper.nino/)

[Official Website](https://nino.xgamedev.net/en/)

Plausibly the fastest and most flexible binary serialization library for C# projects.

![Activity](https://repobeats.axiom.co/api/embed/a9aea9d0b7b75f40c14af83e3c1f20eca39486c4.svg "Repobeats analytics image")

## Features

- Support all **unmanaged types** (`int`/`float`/`DateTime`/`Vector`/`Matrix`, etc)

- Support `ValueTuple`/`Tuple`/`KeyValuePair` of supported types

- Support all custom `interfaces`/`classes`/`structs`/`records`/`record structs` annotated with **[NinoType]** (including `generics`,
  support custom constructor for deserialization)

- Support all **`IEnumerable<SupportedType>`** types (`List`, `Dictonary`, `ConcurrentDictonary`, `Hashset`, `ArraySegment`, `Stack`, `ReadOnlyList` etc)

- Support all **`Span<SupportedType>`** types

- Support all **`Nullable<SupportedType>`** types

- Support all **Embed** serializable types (i.e. `Stack<Dictionary<Int, List<SupportedType[]>[]>[]>`)

- Support **polymorphism**

- High **performance** with low GC allocation

- Support **type check** (guarantees data integrity)

- Support **version compatibility** (i.e. adding fields, changing field type, etc)

- Support **cross-project** (C# Project) type serialization (i.e. serialize a class with member of types in A.dll from B.dll)

## Quick Start

[Documentation](https://nino.xgamedev.net/en/doc/start)

## Performance

[Microbenchmark](https://nino.xgamedev.net/en/perf/micro)
