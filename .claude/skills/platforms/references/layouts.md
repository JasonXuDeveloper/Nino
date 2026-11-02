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
  as .NET needs) and posts a notice instead of failing without it.
- Never read a pointer field by reflection: `FieldInfo.GetValue` on `IntPtr`'s `void*` crashed Mono 6.8 with SIGSEGV.
- Read every field type a layout needs through `NinoReflection.LayoutFieldType`: a function pointer (`delegate*`) is a
  pointer, 8 bytes zero-extended as the generator publishes it, but .NET 8 and later report the field as a function
  pointer type (`IsFunctionPointer`; `IsClass` true, `IsValueType` false, no base type), which `NinoLayout` took for
  unsupported and copied as it lay in memory (4 bytes on 32-bit), and .NET Framework and .NET 7 report `IntPtr`
  (sign-extended), which only the field's signature tells apart (`Module.ResolveSignature` of its FieldDef token,
  ELEMENT_TYPE_FNPTR 0x1B after any modifier; Mono resolves only StandAloneSig tokens, IL2CPP none, so there it stays
  an `IntPtr`). Mono 6.8 overflows its stack loading any struct with a `delegate*` field, even one only a generated
  registry names: tests/Nino.RuntimeTests declares its case in net10.0 only (r105: ok on x64 and on Arm32 under qemu).
