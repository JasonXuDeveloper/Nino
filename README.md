# Nino

Ultimate high-performance binary serialization library for C#.

![build](https://img.shields.io/github/actions/workflow/status/JasonXuDeveloper/Nino/.github/workflows/ci.yml?branch=main)
![license](https://img.shields.io/github/license/JasonXuDeveloper/Nino)
[![nino.nuget](https://img.shields.io/nuget/v/Nino?label=Nino)](https://www.nuget.org/packages/Nino)
[![openupm](https://img.shields.io/npm/v/com.jasonxudeveloper.nino?label=openupm&registry_uri=https://package.openupm.com)](https://openupm.com/packages/com.jasonxudeveloper.nino/)

[Official Website](https://nino.xgamedev.net/en/)

Plausibly the fastest and most flexible binary serialization library for C# projects.

## Features

- Support all **unmanaged types** (`int`/`float`/`DateTime`/`Vector`/`Matrix`, etc)

- Support all custom `interfaces`/`classes`/`structs`/`records` annotated with **[NinoType]** (including `generics`,
  support custom constructor for deserialization)

- Support all **`ICollection<SupportedType>`** types (`List`, `Dictonary`, `ConcurrentDictonary`, `Hashset`, etc)

- Support all **`Span<SupportedType>`** types

- Support all **`Nullable<SupportedType>`** types

- Support all **Embed** serializable types (i.e. `Dictionary<Int, List<SupportedType[]>>`)

- Support **polymorphism**

- High **performance** with low GC allocation

- Support **type check** (guarantees data integrity)

- Contains **version tolerance** (i.e. add/remove fields, change field type, etc)

- Support **cross-project** (C# Project) type serialization (i.e. serialize a class with member of types in A.dll from B.dll)

## Quick Start

[Documentation](https://nino.xgamedev.net/en/doc/start)

## Performance

[Microbenchmark](https://nino.xgamedev.net/en/perf/micro)
