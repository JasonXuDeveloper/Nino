# Struct layouts and host-sized vectors

Part of the `platforms` skill: where a BCL struct's layout per runtime comes from, how every runtime is checked to
write CoreCLR x64's bytes, `Vector<T>` widths, and reading field types by reflection. Read it before changing
`NinoLayout`, `NinoLayoutCore`, `NinoReflection` or a raw struct path.

- Take the layout of a BCL struct on a runtime from docs/layouts.md (the measured tables, from the `# layout:` lines
  `tests/Nino.RuntimeTests` prints on every CI runtime), never from memory or from reference assemblies: these declare
  tuples sequential, so the generator once published raw structs' tuples in declaration order while .NET x64 wrote
  them reordered (`RawLayout` now forces `Auto` for `System.ValueTuple`). The canonical layout is the spec's §5.2;
  a runtime that differs measures and converts (`NinoLayout`, `MeasureMemory`, which also reads `Nullable` fields;
  `KeepsDeclarationOrder` detects .NET Framework's reordering).
- Check that every CI runtime writes and reads CoreCLR x64's bytes for each BCL struct: `BclStructs.cs` against the
  one fixture file `src/Nino.Unity/Assets/Test/Runtime/BclFixtures.cs`, which Unity's `BclLayoutTests` read too, in
  play mode and the IL2CPP player.
- Check a change to `NinoLayout` / `NinoLayoutCore` against random shapes, not only the listed ones: generate nested
  value tuples of every leaf (with `Nullable`, `KeyValuePair`, `DateTimeOffset`, `nint`) inside a sequential struct,
  fill them through reflection, `NinoSerializer.Serialize` them and diff the hex across runtimes (.NET 10, .NET 8 and
  Mono's net472 build on x64, the self-contained `linux-arm64` publish under qemu); in-process, `NinoLayout.Compute`
  with `measured: true` must give the model's result for every shape on x64. Round 104: 5,000 shapes wrote the same
  bytes on all four, and the measured path threw for a top-level `T?` (created as null by `Activator`, so
  `MeasureMemory` takes its flag and value from `NullableValue`).
- Never measure a vector by its declared fields: `Vector<T>`'s are two `ulong`s whatever its size (16, 32 or 64
  bytes, chosen by the CPU; the spec's §3 lists which host has which), and Nino did, writing 16 bytes and dropping the
  upper half on AVX2 hosts. A type whose size depends on the host is never serialized (`wire-format` owns that rule;
  NINO029 is `audit-analyzer`'s). `Vector64` to `Vector512` are fixed size: aligned 8/16/32/64 on x64, 16/16/16 on
  Arm64, 8 on Arm32 (`NinoLeaf.Vector`).
- Test every `Vector<T>` width on one x64 machine: `DOTNET_EnableAVX2=0` (16 bytes), the default (32),
  `DOTNET_MaxVectorTBitWidth=512` (64); `tests/Nino.RuntimeTests` prints `# Vector<float>.Count`. CI covers all three:
  the no-AVX2 unit tests (16), the default (32), and 64 in the net10.0 unit-test job (`VectorTTests`) and the Linux
  runtime-internals job (Nino.RuntimeTests, which must print Count 16). GitHub's x64 runners are Xeon 6973P-C
  (AVX-512) or EPYC 7763 (none): a check that needs AVX-512 asks `.github/scripts/has_avx512.sh` (F, BW, CD, DQ, VL,
  as .NET needs) and posts a notice instead of failing without it. A job's runner is drawn at random (3 of 7 net10.0
  jobs on main ran on an EPYC, round 121), so a path only one CPU kind takes is checked from several jobs: the
  no-avx512, no-avx2 and no-simd jobs rerun the vector-path tests with every set on when their runner has AVX-512
  ("Test the AVX-512 paths"), and a run misses the net10.0 AVX-512 paths only when all four x64 jobs drew EPYCs. Do not add a pinned
  AVX-512 runner or Intel SDE for certainty: the owner decided against it (round 121).
- Never read a pointer field by reflection: `FieldInfo.GetValue` on `IntPtr`'s `void*` crashed Mono 6.8 with SIGSEGV.
- Take a fact about a type that the generator sees from generated code, never from reflection at run time (the
  owner's rule): reflection differs per runtime, NativeAOT keeps only the metadata it is asked for, and every probe
  costs at startup. A function pointer (`delegate*`) is a pointer, 8 bytes zero-extended, but .NET 8 and later report
  its field as a function pointer type (`IsClass` true, `IsValueType` false, no base type) and .NET Framework, .NET 7,
  Mono and IL2CPP as an `IntPtr`, which a 32-bit host sign-extended (r105 told them apart by `IsFunctionPointer` and
  `Module.ResolveSignature`'s ELEMENT_TYPE_FNPTR; Mono resolves no FieldDef signature, IL2CPP none, so a 32-bit Unity
  player wrote FF FF FF FF over an address's high half). Now the generator lists the `delegate*` fields of every
  struct it measures or that the project declares (`LayoutTypes.FunctionPointerFields`, by assembly and runtime name so
  that registering loads no type) in the registry's static constructor, and `NinoReflection.LayoutFieldType`, which
  every layout reads field types through, asks `NinoFunctionPointerFields` for a field reported as an `IntPtr` or a
  base-less class. Evidence (r118): 32-bit Mono 6.12 on x86 read the canonical bytes of a field reported as an
  `IntPtr` as "an integer too large" without the registration and wrote and read them with it; NativeAOT (with the
  registry's `[DynamicDependency]`) and Arm32 .NET 10 under qemu wrote the same bytes as x64 (`Nino.AotTests`,
  tests/Nino.RuntimeTests). Stock Mono (6.8 on x64, 6.12 on x64 and x86) overflows its stack on any use of a struct
  with a `delegate*` field, even `sizeof`: tests/Nino.RuntimeTests declares one in net10.0 only and tests Mono with a
  registered `IntPtr` field. Unity compiles C# 9: its Mono (play mode) and IL2CPP (the player) run declared
  `delegate*` fields in Unity's FunctionPointerTests (src/Nino.Unity), with a pointer taken with `&` called after the
  round trip; only unity.yml on `main` runs them. Read pointer values in tests from a `static readonly ulong` at run time: Roslyn folds both `(nuint)0x87654321u`
  and `(nuint)0x87654321UL` to `ldc.i4` and `conv.u`, which IL2CPP's players sign-extend (the field held
  0xFFFFFFFF87654321 on 2022.3 and 6000.3 while Mono held 0x87654321; runs 41494277688 and 41501012528). 32-bit Mono 6.12 runs without installing it: `dpkg -x` the i386 debs of
  download.mono-project.com's stable-bionic (mono-runtime-sgen and its closure, the Facades from mono-devel) and
  bionic's libc6, libgcc1, libstdc++6, zlib1g, then run `root/lib/ld-linux.so.2 --library-path ... mono-sgen` with
  `MONO_PATH=root/usr/lib/mono/4.5:.../Facades` (else it loads /usr/lib/mono's corlib); there tests/Nino.RuntimeTests also
  failed its Waypoint cases (`Sys` type initializer: no libmono-native in that root) and found no Dictionary fast
  paths (not examined).
