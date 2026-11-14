# Hot paths: buffers, reuse, pooling, build settings

Read this when the task says zero allocation, steady state, per-frame or per-tick messages, high throughput, object
pools, several values in one buffer, or build settings for speed. Full pages:
https://nino.xgamedev.net/en/docs/v4/advanced/zero-allocation.md and
https://nino.xgamedev.net/en/docs/v4/pooling.md

## Writing

- One `NinoArrayBufferWriter` per thread or connection (it is not thread safe): `ResetWrittenCount()` before each
  message, send `WrittenSpan` (or `WrittenMemory`), `Dispose()` at the end (returns its array to the pool).
  `new NinoArrayBufferWriter(capacity, pool)` rents from your own `ArrayPool<byte>`. Nino writes into it in place;
  into any other `IBufferWriter<byte>` (a `PipeWriter`) it writes raw values, raw arrays and lists and fixed-size
  values in place too (and raw array slices, single-segment sequences and, on modern .NET, raw multidimensional
  arrays), and copies everything else once. Do not use `WrittenSpan` after the next write or
  reset: it points into the reused array.
- `NinoSerializer.Serialize(value)` returns a new `byte[]` of exactly the data's size: use it only where an API needs
  a `byte[]`.
- Several values in one buffer: `var w = new Writer(ninoArrayBufferWriter);` (a `NinoArrayBufferWriter` only),
  `NinoSerializer.Serialize(a, ref w)`..., `w.Flush(); w.ReleaseReferences();`; read with `var r = new Reader(span);`,
  `NinoDeserializer.Deserialize<T>(ref r)`..., `r.ReleaseReferences()`. Release once per logical message.
- Arrays and lists of nullable values written often: `NinoNullable<T>[]` in place of `T?[]` (same bytes, one block
  copy; references/types.md).
- `NinoSerializer.GetFixedSize<T>()` gives the size of a fixed-size type; `NinoSerializer.IsSupported(type)` whether
  Nino can write a type.

## Reading into existing instances

`NinoDeserializer.Deserialize(data, ref value)` fills `value` in place (null: creates it). It reuses nested objects,
lists (by index) and dictionaries (merged by key); unchanged strings are kept (a `[NinoUtf8]` string only when it is
ASCII). Use `List<T>` for varying counts: an array is reused only at the same length. A polymorphic slot is reused only
when the data holds the same runtime type.

- Types re-created through a constructor (records, `init`/get-only/`readonly` members) are re-created even here.
  Hot-path types: a parameterless constructor and settable members.
- Instances the read drops go to `NinoSettings.SetRecycler<T>(handler)` for the slot's declared type, else
  `ReturnToPool()` of an `INinoPooled`, else the GC.

## Built-in pools

`[NinoType, NinoPooled] public sealed partial class X` gets a pool: Nino takes new instances from it and returns
dropped ones; you call `x.ReturnToPool()` once (twice throws). Pooled classes are sealed, partial and assignable
(NINO018) and implement both hooks (NINO019):

```csharp
[NinoType, NinoPooled]
public sealed partial class Alert
{
    public int DeviceId;
    public float Value;

    private static partial IEnumerable<Alert> OnNinoPoolWarmup() => Enumerable.Range(0, 64).Select(_ => new Alert());
    private partial void OnNinoPoolReturn() { } // release state Nino does not deserialize
}
```

- A class holding pooled children should be pooled too. `NinoPool<T>.Shared.Clear()` empties a pool (Unity with domain
  reload off). `NinoSettings.PoolMaxRetained` / `SetPoolMaxRetained<T>(n)` cap what a pool keeps (`PoolMaxRetained` for
  pools created later, so set it at startup; `SetPoolMaxRetained<T>` also changes an existing pool).
- `[NinoFactory] public static T Rent()` (public, static, callable without arguments, returns the type, never null:
  NINO017) is called whenever Nino needs a new instance; also for your own collection types (implement `INinoPooled`
  so dropped ones come back). Not together with `[NinoConstructor]` (NINO025).
- Never return to a pool, or hand to a recycler, an instance still referenced elsewhere: the pool hands it out again.

## Build settings

Release, tiered compilation and Dynamic PGO left on (never `TieredCompilation` false); Native AOT with
`<IlcInstructionSet>` for AVX2/AVX-512; Unity IL2CPP Release/Master with Burst. Rust, Go and TypeScript:
https://nino.xgamedev.net/en/docs/v4/advanced/build-configuration.md

Arrays or `List<T>` of classes much larger than the CPU cache, whose objects were replaced, shuffled or sorted:
`<NinoPrefetchLargeCollections>true</NinoPrefetchLargeCollections>` in the calling project makes their top-level calls
prefetch (same bytes; .NET 8+ through the interceptors, nothing in Unity). Short collections get a few % slower.
If the project sets `InterceptorsNamespaces` itself, append (`$(InterceptorsNamespaces);Mine`), don't replace.
