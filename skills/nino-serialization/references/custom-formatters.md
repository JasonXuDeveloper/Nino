# Custom formatters

Read this when a member needs its own encoding (varints, compression of one field, a third-party type Nino does not
know) or NINO007, NINO008 or NINO020 appears. Full page: https://nino.xgamedev.net/en/docs/v4/custom-formatters.md

A formatter is a **struct** implementing `INinoFormatter<T>` (`Serialize(T, ref Writer)`, `Deserialize(ref Reader,
ref T)`), called as `default(F)`: no instance state. Read exactly what you wrote, in order, and bound every loop by
the input (the data may be hostile). Those two members are all a formatter needs: the interface's other members
are optional capabilities with defaults. Implement `FixedSize`, `WriteFixed`, `BlockMap` and `ReadBlock` only for a
struct of a fixed size that collections should move as blocks, all four, writing the same bytes as `Serialize`;
`BlockMap` names each field in wire order with its formatter, `new NinoBlockMap().Item<int,
UnmanagedFormatter<int>>(NinoBlocks.Offset(ref v, ref v.X))...`. Tuples and pairs of such values are blocks too. The
rest (on the non-generic `INinoFormatter` it extends: `BodyTraits`, the run loop `HasRunLoop` and `WriteRun`, and the
prefetch members `PrefetchBytes`, `HasMemberPrefetch` and `PrefetchMembers`) are
what generated formatters of classes and polymorphic bases implement; leave them alone.

- One member: `[NinoMemberFormatter(typeof(F))]`, `F` for exactly the member type (NINO008).
- A type Nino does not know: `[assembly: NinoFormatter(typeof(F<,>))]`, generic formatters as open definitions; a
  type parameter constrained `struct, INinoFormatter<E>` gets the element formatter; `[NinoRaw]` and `[NinoCreated]`
  narrow what binds. Invalid registrations: NINO020. There is no runtime registration call.
- Lengths: `writer.WriteLength(n)` / `WriteNullLength()` and `reader.ReadLength(minElementSize)` (checked against the
  bytes left before you allocate). Dropped instances: `NinoRecycler.Discard(ref value)` /
  `NinoRecycler.Replace(ref value, x)`. Nested values: `NinoSerializer.Serialize(v, ref writer)`,
  `NinoDeserializer.Deserialize(ref reader, ref v)`.
- Compose Nino's own formatter structs instead of writing loops: `ListFormatter<TList, T, TF>`,
  `DictionaryFormatter<TKey, TValue, TKF, TVF>`, `HashSetFormatter`, `ArrayFormatter`, `NullableFormatter`,
  `ValueTupleFormatter`... with `UnmanagedFormatter<T>`, `StringFormatter` or `CachedFormatter<T>` (whatever Nino uses
  for `T`, a `[NinoType]` included) as element formatters:
  `[NinoMemberFormatter(typeof(ListFormatter<List<long>, long, ZigZagLongFormatter>))]`. Full table:
  https://nino.xgamedev.net/en/docs/v4/custom-formatters#built-in-formatters
- Fixed-size bytes of your own: `ref byte b = ref writer.Reserve(n)` / `reader.ReadBlock(n)` (NinoDataException when
  fewer are left), encoded little-endian (`BinaryPrimitives`). Reserved bytes are not cleared, and the `Reserve`
  reference is invalid after the next write; never write through the `ReadBlock` reference.
- nino-gen cannot write what a custom formatter writes: in TypeScript, Go and Rust the type holding such a member is
  left out, with the types that hold it (NINOGEN002). Keep cross-language types to built-in encodings.

```csharp
public struct ZigZagLongFormatter : INinoFormatter<long>
{
    public void Serialize(long value, ref Writer writer)
    {
        ulong v = (ulong)((value << 1) ^ (value >> 63));
        while (v >= 0x80) { writer.Write((byte)(v | 0x80)); v >>= 7; }
        writer.Write((byte)v);
    }

    public void Deserialize(ref Reader reader, ref long value)
    {
        ulong v = 0;
        for (int shift = 0; ; shift += 7)
        {
            reader.Read(out byte b);
            if (shift == 63 && b > 1) throw new NinoDataException("Varint longer than 64 bits"); // hostile input
            v |= (ulong)(b & 0x7F) << shift;
            if (b < 0x80) break;
        }
        value = (long)(v >> 1) ^ -(long)(v & 1);
    }
}

[NinoType]
public sealed class BalanceChange
{
    public int AccountId;
    [NinoMemberFormatter(typeof(ZigZagLongFormatter))] public long DeltaCents;
}
```
