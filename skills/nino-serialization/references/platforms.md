# Unity, IL2CPP, Burst, HybridCLR, Native AOT, Godot, ASP.NET Core

Read this when the code runs in Unity (any backend), under IL2CPP or Native AOT, loads assemblies with HybridCLR or
plugins, runs in Godot, or serves HTTP bodies with ASP.NET Core. Full pages:
https://nino.xgamedev.net/en/docs/v4/advanced/unity.md, https://nino.xgamedev.net/en/docs/v4/platforms.md,
https://nino.xgamedev.net/en/docs/v4/advanced/aspnetcore.md

## Unity

- Install: `openupm add com.jasonxudeveloper.nino` (Unity 2022.3+: runtime, generator, code fixes and
  `Nino.UnityTypes.dll`). Assembly definitions need `Nino.Core.dll` (Auto Referenced, or listed under Override
  References; also `Nino.UnityTypes.dll` for `AnimationCurve`, `Gradient`, `RectOffset`).
- Unity compiles against .NET Standard: no `[UnsafeAccessor]`, so private members and `[NinoPooled]` need the type
  and every containing type `partial` (NINO002); no interceptors (same bytes).
- Unity math structs (`Vector3`, `Quaternion`, `float3`...) are unmanaged structs: written raw, no attribute needed.
  `MonoBehaviour`, `ScriptableObject` and other engine objects are not serialized: copy their data into a
  `[NinoType]`.
- Burst: install `com.unity.burst` 1.8+ and Nino's Burst SIMD kernels register on their own (`Nino.Burst.NinoBurst`)
  for tuples, padded structs, Latin-1 strings and the ASCII text of `[NinoUtf8]` strings; include SSE4 and AVX2 in
  Burst AOT Settings for x64 players. Each kernel tests itself against Nino's scalar code at startup; one that throws
  or writes other bytes on the installed Burst is not used, with a warning naming it. Without Burst, IL2CPP and Mono
  take Nino's scalar paths (the same bytes); WebGL players built with WebAssembly SIMD get the same kernels from `Nino.WebGL` with no setup.
- Server on .NET and client in Unity read each other's bytes: raw structs use one canonical layout on every runtime
  (references/types.md). `Vector<T>` is the exception (NINO029).

## IL2CPP and Native AOT

- No reflection over members: make every type appear in code the generator sees (a member type, the type
  argument of a `NinoSerializer`/`NinoDeserializer` call, or the `typeof` argument of `SerializeBoxed`,
  `DeserializeBoxed` or `IsSupported`). A `Type` known only at run time (a closed generic `[NinoType]` made with
  `MakeGenericType`) registers nothing: Native AOT throws an error naming it; IL2CPP builds its formatter at run
  time and runs it through Unity's full generic sharing (2022.1 and later), slower than generated code.
- Native AOT on x64 takes Nino's SSSE3 paths on CPUs that have SSSE3; its AVX2 and AVX-512 paths need
  `<IlcInstructionSet>` (`x86-64-v3`, `x86-64-v4` or `native`), which makes the app require those CPUs.

## HybridCLR and plugins

`Assembly.Load` is enough: generated registries load on first use, and subtypes in the loaded assembly are found
then. The generator must run in that assembly too. `NinoRegistry.EnsureInitialized(asm)` / `EnsureAllInitialized()`
only to pay the registry load on a loading screen. `NinoFormatterCache<T>.IsRegistered` and
`NinoActivator<T>.IsRegistered` only say whether Nino has met `T` yet (false before first use, even with generated
code); to ask whether a type is supported, call `NinoSerializer.IsSupported(typeof(T))`. A closed generic type the process only reads through a base:
`NinoPolymorphic.TryRegister<Box<int>>()` at startup.

## Godot 4 (.NET)

A normal NuGet project (`dotnet add package Nino --prerelease`); engine objects (`Node`, `Resource`) are not
serialized, math structs are raw.

## ASP.NET Core (Nino.AspNetCore, net8.0/net10.0)

```csharp
builder.Services.AddControllers().AddNinoFormatters(); // MVC: application/x-nino in and out, JSON stays the default
app.MapPost("/orders", (NinoBody<Order> order) => NinoResults.Ok(order.Value));
app.MapGet("/shape", () => NinoResults.Ok<Shape>(new Circle())); // give T to write a subtype as its base
```

Invalid bodies become 400 (`NinoBody<T>`: 415 for another content type). By hand:
`request.ReadFromNinoAsync<T>(ct)`, `response.WriteAsNinoAsync(value, ct)`, `NinoMediaTypes.Application`. Request
bodies are untrusted: set `NinoSettings.Untrusted = true` at startup and cap the request size.
