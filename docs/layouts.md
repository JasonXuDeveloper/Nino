# Layouts of BCL structs across runtimes

The memory layout of every unmanaged BCL struct Nino copies as raw bytes inside a plain struct, on each runtime CI runs,
against 64-bit CoreCLR's (the first column). The canonical layout Nino writes is that column (spec §5.2), with
exceptions that do not depend on the runtime: `DateTimeOffset` as .NET 10 lays it out (its minutes as an `int`; .NET 8
has a `short` at the same place), `nint`/`nuint`/pointers and function pointers as 8 bytes, and the SIMD vectors aligned as on x64.
`Vector<T>` is not serializable (below). Every runtime below writes the canonical bytes and reads them, checked case by
case against the bytes CoreCLR x64 wrote: one fixture file, `src/Nino.Unity/Assets/Test/Runtime/BclFixtures.cs`,
linked into `tests/Nino.RuntimeTests` (`BclStructs.cs`) and read by Unity's `BclLayoutTests` in its editor and its
IL2CPP player: a runtime whose layout is the canonical one copies the struct as
it is, every other one converts it field by field, at offsets it finds once (`NinoLayout`, `MeasureMemory`).

Measured by CI run 41280617045 (`# layout` lines of `tests/Nino.RuntimeTests`, tabulated with
`tests/Nino.RuntimeTests/layouts.py`): .NET 10.0.13/14 on Linux x64, Linux Arm64, Windows x86 and Linux Arm32 (qemu), .NET
Framework 4.8.9363 64-bit and 32-bit, Mono 6.14.1 on Linux x64 and Arm64. The .NET Framework columns are history:
Nino no longer runs there since 4.0.0-rc.11 (no netstandard2.0 build), and CI no longer measures it. `=` is the same size and field offsets as
CoreCLR x64; a bold cell differs (offsets only, in field order); `n/a` is a type the runtime does not have (the
net472 build). Field names differ between runtimes (`_dateData` and `dateData`); offsets are what count. Generic
instantiations mix field sizes on purpose: `(int,long)`, `(byte,long,int)`, nested tuples, tuples holding `DateTime`,
`decimal`, `Guid`, `Nullable` and `DateTimeOffset`.

What the tables show:

- **Value tuples** are `LayoutKind.Auto`. 64-bit CoreCLR (x64 and Arm64 alike) puts primitive items first, largest
  first, then struct items, and rounds the size to 8 (or to a power of two up to 8). Mono keeps declaration order and
  natural alignment. 32-bit CoreCLR and .NET Framework x86 use CoreCLR's rule with 4-byte alignment; .NET Framework
  x64 uses an older form of it (`(int,(byte,short),long)` is 24 bytes there, 16 on CoreCLR).
- **.NET Framework** lays out a sequential struct that holds an auto-layout struct (a tuple, `DateTime`,
  `DateTimeOffset`) as auto-layout too: in `Around<T>` the `int` comes first.
- **`DateTimeOffset`**: CoreCLR and .NET Framework put the minutes first and the date at 8 (at 4 on 32-bit); Mono
  puts the date first. .NET 8 x64 (not in CI's matrix, measured locally) has a `short` at 0 and the date at 8.
- **`decimal`** is aligned to 8 by CoreCLR and Mono 6.14, to 4 by .NET Framework.
- **`KeyValuePair`** and **`Nullable`** are sequential everywhere; they differ only where their items do.
- **`Int128`**, **`Vector128`**, **`Vector256`** and **`Vector512`** are aligned to 16, 16, 32 and 64 by CoreCLR
  x64 (the canonical layout), the two wide vectors to 16 on Arm64, everything to 8 on Arm32.
- **`Vector<T>`** is not in the tables: it is 16, 32 or 64 bytes, chosen by the CPU (spec §3 lists the hosts; CI run
  41280617045 and Unity's `BclLayoutTests` measured them), so Nino serializes no type that holds one (NINO029);
  [pitfalls.md](pitfalls.md#vectort-is-not-serializable) has the fixed-lane form that replaces it.
- **IL2CPP**: see the section after the tables.

### Each type: size, then the offset of each of its fields

| Type | CoreCLR x64 | CoreCLR Arm64 | CoreCLR x86 | CoreCLR Arm32 | .NET Framework x64 | .NET Framework x86 | Mono x64 | Mono Arm64 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| `(int,long)` | 16: Item1 8, Item2 0 | = | **12: 8, 0** | = | = | **12: 8, 0** | **16: 0, 8** | **16: 0, 8** |
| `(long,int)` | 16: Item1 0, Item2 8 | = | **12: 0, 8** | = | = | **12: 0, 8** | = | = |
| `(byte,short)` | 4: Item1 2, Item2 0 | = | = | = | = | = | **4: 0, 2** | **4: 0, 2** |
| `(byte,long,int)` | 16: Item1 12, Item2 0, Item3 8 | = | = | = | = | = | **24: 0, 8, 16** | **24: 0, 8, 16** |
| `(byte,int,short)` | 8: Item1 6, Item2 0, Item3 4 | = | = | = | = | = | **12: 0, 4, 8** | **12: 0, 4, 8** |
| `(short,byte,int)` | 8: Item1 4, Item2 6, Item3 0 | = | = | = | = | = | **8: 0, 2, 4** | **8: 0, 2, 4** |
| `(int,int)` | 8: Item1 0, Item2 4 | = | = | = | = | = | = | = |
| `(int,int,int)` | 16: Item1 0, Item2 4, Item3 8 | = | **12: 0, 4, 8** | **12: 0, 4, 8** | = | **12: 0, 4, 8** | **12: 0, 4, 8** | **12: 0, 4, 8** |
| `(byte,byte,byte)` | 4: Item1 0, Item2 1, Item3 2 | = | = | = | = | = | **3: 0, 1, 2** | **3: 0, 1, 2** |
| `(bool,char,byte)` | 4: Item1 2, Item2 0, Item3 3 | = | = | = | = | = | **6: 0, 2, 4** | **6: 0, 2, 4** |
| `(double,float)` | 16: Item1 0, Item2 8 | = | **12: 0, 8** | = | = | **12: 0, 8** | = | = |
| `(Tag,long)` | 16: Item1 8, Item2 0 | = | **12: 8, 0** | = | = | **12: 8, 0** | **16: 0, 8** | **16: 0, 8** |
| `(int,IntPtr)` | 16: Item1 8, Item2 0 | = | **8: 0, 4** | **8: 0, 4** | = | **8: 0, 4** | **16: 0, 8** | **16: 0, 8** |
| `(byte,UIntPtr)` | 16: Item1 8, Item2 0 | = | **8: 4, 0** | **8: 4, 0** | = | **8: 4, 0** | **16: 0, 8** | **16: 0, 8** |
| `ValueTuple<long>` | 8: Item1 0 | = | = | = | = | = | = | = |
| `(int,DateTime)` | 16: Item1 0, Item2 8 | = | **12: 0, 4** | = | = | **12: 0, 4** | = | = |
| `(byte,DateTime,int)` | 16: Item1 4, Item2 8, Item3 0 | = | = | = | = | = | **24: 0, 8, 16** | **24: 0, 8, 16** |
| `(int,TimeSpan)` | 16: Item1 0, Item2 8 | = | = | = | = | **12: 0, 4** | = | = |
| `(byte,decimal)` | 24: Item1 0, Item2 8 | = | = | = | = | **20: 0, 4** | = | = |
| `(int,decimal)` | 24: Item1 0, Item2 8 | = | = | = | = | **20: 0, 4** | = | = |
| `(byte,Guid)` | 24: Item1 0, Item2 4 | = | **20: 0, 4** | **20: 0, 4** | **24: 0, 8** | **20: 0, 4** | **20: 0, 4** | **20: 0, 4** |
| `(byte,int?)` | 16: Item1 0, Item2 4 | = | **12: 0, 4** | **12: 0, 4** | **16: 0, 8** | **12: 0, 4** | **12: 0, 4** | **12: 0, 4** |
| `(long?,byte)` | 24: Item1 8, Item2 0 | = | = | = | = | **20: 4, 0** | **24: 0, 16** | **24: 0, 16** |
| `(byte,(int,long))` | 24: Item1 0, Item2 8 | = | **16: 0, 4** | = | = | **16: 0, 4** | = | = |
| `((byte,short),long)` | 16: Item1 8, Item2 0 | = | **12: 8, 0** | = | = | **12: 8, 0** | **16: 0, 8** | **16: 0, 8** |
| `(int,(byte,short),long)` | 16: Item1 8, Item2 12, Item3 0 | = | = | = | **24: 8, 16, 0** | = | **16: 0, 4, 8** | **16: 0, 4, 8** |
| `(int,KVP<int,long>)` | 24: Item1 0, Item2 8 | = | = | = | = | **20: 0, 4** | = | = |
| `(byte,DateTimeOffset)` | 24: Item1 0, Item2 8 | = | **16: 0, 4** | = | = | **16: 0, 4** | = | = |
| `(byte,short,int,long,byte,short,int)` | 24: Item1 20, Item2 16, Item3 8, Item4 0, Item5 21, Item6 18, Item7 12 | = | = | = | = | = | **24: 0, 2, 4, 8, 16, 18, 20** | **24: 0, 2, 4, 8, 16, 18, 20** |
| `(byte,long,byte,long,byte,long,byte,int)` | 32: Item1 24, Item2 0, Item3 25, Item4 8, Item5 26, Item6 16, Item7 27, Rest 28 | = | = | = | **40: 24, 0, 25, 8, 26, 16, 27, 32** | = | **56: 0, 8, 16, 24, 32, 40, 48, 52** | **56: 0, 8, 16, 24, 32, 40, 48, 52** |
| `(int,(byte,long),(short,(byte,int)))` | 40: Item1 0, Item2 8, Item3 24 | = | **28: 0, 4, 16** | = | = | **28: 0, 4, 16** | = | = |
| `KVP<int,long>` | 16: key 0, value 8 | = | = | = | = | = | = | = |
| `KVP<byte,long>` | 16: key 0, value 8 | = | = | = | = | = | = | = |
| `KVP<long,byte>` | 16: key 0, value 8 | = | = | = | = | = | = | = |
| `KVP<int,DateTime>` | 16: key 0, value 8 | = | **12: 0, 4** | = | = | **12: 0, 4** | = | = |
| `KVP<byte,(int,long)>` | 24: key 0, value 8 | = | **16: 0, 4** | = | = | **16: 0, 4** | = | = |
| `DateTimeOffset` | 16: _dateTime 8, _offsetMinutes 0 | = | **12: 4, 0** | = | = | **12: 4, 0** | **16: 0, 8** | **16: 0, 8** |
| `DateTime` | 8: _dateData 0 | = | = | = | = | = | = | = |
| `TimeSpan` | 8: _ticks 0 | = | = | = | = | = | = | = |
| `decimal` | 16: _flags 0, _hi32 4, _lo64 8 | = | = | = | **16: 0, 4, 8, 12** | **16: 0, 4, 8, 12** | **16: 0, 4, 8, 12, 8** | **16: 0, 4, 8, 12, 8** |
| `Guid` | 16: _a 0, _b 4, _c 6, _d 8, _e 9, _f 10, _g 11, _h 12, _i 13, _j 14, _k 15 | = | = | = | = | = | = | = |
| `int?` | 8: hasValue 0, value 4 | = | = | = | = | = | = | = |
| `long?` | 16: hasValue 0, value 8 | = | = | = | = | = | = | = |
| `(int,long)?` | 24: hasValue 0, value 8 | = | **16: 0, 4** | = | = | **16: 0, 4** | = | = |
| `DateTimeOffset?` | 24: hasValue 0, value 8 | = | **16: 0, 4** | = | = | **16: 0, 4** | = | = |
| `IntPtr` | 8: _value 0 | = | **4: 0** | **4: 0** | = | **4** | = | = |
| `Complex` | 16: m_real 0, m_imaginary 8 | = | = | = | = | = | = | = |
| `Int128` | 16: _lower 0, _upper 8 | = | = | = | n/a | n/a | n/a | n/a |
| `(byte,Int128)` | 32: Item1 0, Item2 16 | = | = | **24: 0, 8** | n/a | n/a | n/a | n/a |
| `Half` | 2: _value 0 | = | = | = | n/a | n/a | n/a | n/a |
| `(byte,Half)` | 4: Item1 0, Item2 2 | = | = | = | n/a | n/a | n/a | n/a |
| `DateOnly` | 4: _dayNumber 0 | = | = | = | n/a | n/a | n/a | n/a |
| `TimeOnly` | 8: _ticks 0 | = | = | = | n/a | n/a | n/a | n/a |
| `(byte,DateOnly,TimeOnly)` | 16: Item1 0, Item2 4, Item3 8 | = | = | = | n/a | n/a | n/a | n/a |
| `Range` | 8: Start 0, End 4 | = | = | = | n/a | n/a | n/a | n/a |
| `Index` | 4: _value 0 | = | = | = | n/a | n/a | n/a | n/a |
| `Rune` | 4: _value 0 | = | = | = | n/a | n/a | n/a | n/a |
| `Vector2` | 8: X 0, Y 4 | = | = | = | n/a | n/a | n/a | n/a |
| `Vector3` | 12: X 0, Y 4, Z 8 | = | = | = | n/a | n/a | n/a | n/a |
| `Vector4` | 16: X 0, Y 4, Z 8, W 12 | = | = | = | n/a | n/a | n/a | n/a |
| `Quaternion` | 16: X 0, Y 4, Z 8, W 12 | = | = | = | n/a | n/a | n/a | n/a |
| `Plane` | 16: Normal 0, D 12 | = | = | = | n/a | n/a | n/a | n/a |
| `Matrix3x2` | 24: M11 0, M12 4, M21 8, M22 12, M31 16, M32 20 | = | = | = | n/a | n/a | n/a | n/a |
| `Matrix4x4` | 64: M11 0, M12 4, M13 8, M14 12, M21 16, M22 20, M23 24, M24 28, M31 32, M32 36, M33 40, M34 44, M41 48, M42 52, M43 56, M44 60 | = | = | = | n/a | n/a | n/a | n/a |
| `Vector64<int>` | 8: _00 0 | = | = | = | n/a | n/a | n/a | n/a |
| `Vector128<int>` | 16: _lower 0, _upper 8 | = | = | = | n/a | n/a | n/a | n/a |
| `Vector256<long>` | 32: _lower 0, _upper 16 | = | = | = | n/a | n/a | n/a | n/a |
| `Vector512<long>` | 64: _lower 0, _upper 32 | = | = | = | n/a | n/a | n/a | n/a |

### In `Around<T> { byte A; T B; int C; }`: its size, then the offsets of A, B and C

| Type | CoreCLR x64 | CoreCLR Arm64 | CoreCLR x86 | CoreCLR Arm32 | .NET Framework x64 | .NET Framework x86 | Mono x64 | Mono Arm64 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| `(int,long)` | 32: 0, 8, 24 | = | **20: 0, 4, 16** | = | **24: 4, 8, 0** | **20: 4, 8, 0** | = | = |
| `(long,int)` | 32: 0, 8, 24 | = | **20: 0, 4, 16** | = | **24: 4, 8, 0** | **20: 4, 8, 0** | = | = |
| `(byte,short)` | 12: 0, 4, 8 | = | = | = | **16: 4, 8, 0** | **12: 4, 8, 0** | **12: 0, 2, 8** | **12: 0, 2, 8** |
| `(byte,long,int)` | 32: 0, 8, 24 | = | **24: 0, 4, 20** | = | **24: 4, 8, 0** | **24: 4, 8, 0** | **40: 0, 8, 32** | **40: 0, 8, 32** |
| `(byte,int,short)` | 24: 0, 8, 16 | = | **16: 0, 4, 12** | **16: 0, 4, 12** | **16: 4, 8, 0** | **16: 4, 8, 0** | **20: 0, 4, 16** | **20: 0, 4, 16** |
| `(short,byte,int)` | 24: 0, 8, 16 | = | **16: 0, 4, 12** | **16: 0, 4, 12** | **16: 4, 8, 0** | **16: 4, 8, 0** | **16: 0, 4, 12** | **16: 0, 4, 12** |
| `(int,int)` | 24: 0, 8, 16 | = | **16: 0, 4, 12** | **16: 0, 4, 12** | **16: 4, 8, 0** | **16: 4, 8, 0** | **16: 0, 4, 12** | **16: 0, 4, 12** |
| `(int,int,int)` | 32: 0, 8, 24 | = | **20: 0, 4, 16** | **20: 0, 4, 16** | **24: 4, 8, 0** | **20: 4, 8, 0** | **20: 0, 4, 16** | **20: 0, 4, 16** |
| `(byte,byte,byte)` | 12: 0, 4, 8 | = | = | = | **16: 4, 8, 0** | **12: 4, 8, 0** | **8: 0, 1, 4** | **8: 0, 1, 4** |
| `(bool,char,byte)` | 12: 0, 4, 8 | = | = | = | **16: 4, 8, 0** | **12: 4, 8, 0** | **12: 0, 2, 8** | **12: 0, 2, 8** |
| `(double,float)` | 32: 0, 8, 24 | = | **20: 0, 4, 16** | = | **24: 4, 8, 0** | **20: 4, 8, 0** | = | = |
| `(Tag,long)` | 32: 0, 8, 24 | = | **20: 0, 4, 16** | = | **24: 4, 8, 0** | **20: 4, 8, 0** | = | = |
| `(int,IntPtr)` | 32: 0, 8, 24 | = | **16: 0, 4, 12** | **16: 0, 4, 12** | **24: 4, 8, 0** | **16: 4, 8, 0** | = | = |
| `(byte,UIntPtr)` | 32: 0, 8, 24 | = | **16: 0, 4, 12** | **16: 0, 4, 12** | **24: 4, 8, 0** | **16: 4, 8, 0** | = | = |
| `ValueTuple<long>` | 24: 0, 8, 16 | = | = | = | = | = | = | = |
| `(int,DateTime)` | 32: 0, 8, 24 | = | **20: 0, 4, 16** | = | **24: 4, 8, 0** | **20: 4, 8, 0** | = | = |
| `(byte,DateTime,int)` | 32: 0, 8, 24 | = | **24: 0, 4, 20** | = | **24: 4, 8, 0** | **24: 4, 8, 0** | **40: 0, 8, 32** | **40: 0, 8, 32** |
| `(int,TimeSpan)` | 32: 0, 8, 24 | = | = | = | **24: 4, 8, 0** | **20: 4, 8, 0** | = | = |
| `(byte,decimal)` | 40: 0, 8, 32 | = | = | = | **32: 4, 8, 0** | **28: 4, 8, 0** | = | = |
| `(int,decimal)` | 40: 0, 8, 32 | = | = | = | **32: 4, 8, 0** | **28: 4, 8, 0** | = | = |
| `(byte,Guid)` | 40: 0, 8, 32 | = | **28: 0, 4, 24** | **28: 0, 4, 24** | **32: 4, 8, 0** | **28: 4, 8, 0** | **28: 0, 4, 24** | **28: 0, 4, 24** |
| `(byte,int?)` | 32: 0, 8, 24 | = | **20: 0, 4, 16** | **20: 0, 4, 16** | **24: 4, 8, 0** | **20: 4, 8, 0** | **20: 0, 4, 16** | **20: 0, 4, 16** |
| `(long?,byte)` | 40: 0, 8, 32 | = | = | = | **32: 4, 8, 0** | **28: 4, 8, 0** | = | = |
| `(byte,(int,long))` | 40: 0, 8, 32 | = | **24: 0, 4, 20** | = | **32: 4, 8, 0** | **24: 4, 8, 0** | = | = |
| `((byte,short),long)` | 32: 0, 8, 24 | = | **20: 0, 4, 16** | = | **24: 4, 8, 0** | **20: 4, 8, 0** | = | = |
| `(int,(byte,short),long)` | 32: 0, 8, 24 | = | **24: 0, 4, 20** | = | **32: 4, 8, 0** | **24: 4, 8, 0** | = | = |
| `(int,KVP<int,long>)` | 40: 0, 8, 32 | = | = | = | **32: 4, 8, 0** | **28: 4, 8, 0** | = | = |
| `(byte,DateTimeOffset)` | 40: 0, 8, 32 | = | **24: 0, 4, 20** | = | **32: 4, 8, 0** | **24: 4, 8, 0** | = | = |
| `(byte,short,int,long,byte,short,int)` | 40: 0, 8, 32 | = | **32: 0, 4, 28** | = | **32: 4, 8, 0** | **32: 4, 8, 0** | = | = |
| `(byte,long,byte,long,byte,long,byte,int)` | 48: 0, 8, 40 | = | **40: 0, 4, 36** | = | **48: 4, 8, 0** | **40: 4, 8, 0** | **72: 0, 8, 64** | **72: 0, 8, 64** |
| `(int,(byte,long),(short,(byte,int)))` | 56: 0, 8, 48 | = | **36: 0, 4, 32** | = | **48: 4, 8, 0** | **36: 4, 8, 0** | = | = |
| `KVP<int,long>` | 32: 0, 8, 24 | = | = | = | = | = | = | = |
| `KVP<byte,long>` | 32: 0, 8, 24 | = | = | = | = | = | = | = |
| `KVP<long,byte>` | 32: 0, 8, 24 | = | = | = | = | = | = | = |
| `KVP<int,DateTime>` | 32: 0, 8, 24 | = | **20: 0, 4, 16** | = | **24: 4, 8, 0** | **20: 4, 8, 0** | = | = |
| `KVP<byte,(int,long)>` | 40: 0, 8, 32 | = | **24: 0, 4, 20** | = | **32: 4, 8, 0** | **24: 4, 8, 0** | = | = |
| `DateTimeOffset` | 32: 0, 8, 24 | = | **20: 0, 4, 16** | = | **24: 4, 8, 0** | **20: 4, 8, 0** | = | = |
| `DateTime` | 24: 0, 8, 16 | = | **16: 0, 4, 12** | = | **16: 4, 8, 0** | **16: 4, 8, 0** | = | = |
| `TimeSpan` | 24: 0, 8, 16 | = | = | = | = | = | = | = |
| `decimal` | 32: 0, 8, 24 | = | = | = | **24: 0, 4, 20** | **24: 0, 4, 20** | = | = |
| `Guid` | 24: 0, 4, 20 | = | = | = | = | = | = | = |
| `int?` | 16: 0, 4, 12 | = | = | = | = | = | = | = |
| `long?` | 32: 0, 8, 24 | = | = | = | = | = | = | = |
| `(int,long)?` | 40: 0, 8, 32 | = | **24: 0, 4, 20** | = | **32: 4, 8, 0** | **24: 4, 8, 0** | = | = |
| `DateTimeOffset?` | 40: 0, 8, 32 | = | **24: 0, 4, 20** | = | **32: 4, 8, 0** | **24: 4, 8, 0** | = | = |
| `IntPtr` | 24: 0, 8, 16 | = | **12: 0, 4, 8** | **12: 0, 4, 8** | = | **12: 0, 4, 8** | = | = |
| `Complex` | 32: 0, 8, 24 | = | = | = | = | = | = | = |
| `Int128` | 48: 0, 16, 32 | = | = | **32: 0, 8, 24** | n/a | n/a | n/a | n/a |
| `(byte,Int128)` | 64: 0, 16, 48 | = | = | **40: 0, 8, 32** | n/a | n/a | n/a | n/a |
| `Half` | 8: 0, 2, 4 | = | = | = | n/a | n/a | n/a | n/a |
| `(byte,Half)` | 12: 0, 4, 8 | = | = | = | n/a | n/a | n/a | n/a |
| `DateOnly` | 12: 0, 4, 8 | = | = | = | n/a | n/a | n/a | n/a |
| `TimeOnly` | 24: 0, 8, 16 | = | = | = | n/a | n/a | n/a | n/a |
| `(byte,DateOnly,TimeOnly)` | 32: 0, 8, 24 | = | = | = | n/a | n/a | n/a | n/a |
| `Range` | 16: 0, 4, 12 | = | = | = | n/a | n/a | n/a | n/a |
| `Index` | 12: 0, 4, 8 | = | = | = | n/a | n/a | n/a | n/a |
| `Rune` | 12: 0, 4, 8 | = | = | = | n/a | n/a | n/a | n/a |
| `Vector2` | 16: 0, 4, 12 | = | = | = | n/a | n/a | n/a | n/a |
| `Vector3` | 20: 0, 4, 16 | = | = | = | n/a | n/a | n/a | n/a |
| `Vector4` | 24: 0, 4, 20 | = | = | = | n/a | n/a | n/a | n/a |
| `Quaternion` | 24: 0, 4, 20 | = | = | = | n/a | n/a | n/a | n/a |
| `Plane` | 24: 0, 4, 20 | = | = | = | n/a | n/a | n/a | n/a |
| `Matrix3x2` | 32: 0, 4, 28 | = | = | = | n/a | n/a | n/a | n/a |
| `Matrix4x4` | 72: 0, 4, 68 | = | = | = | n/a | n/a | n/a | n/a |
| `Vector64<int>` | 24: 0, 8, 16 | = | = | = | n/a | n/a | n/a | n/a |
| `Vector128<int>` | 48: 0, 16, 32 | = | = | **32: 0, 8, 24** | n/a | n/a | n/a | n/a |
| `Vector256<long>` | 96: 0, 32, 64 | **64: 0, 16, 48** | = | **48: 0, 8, 40** | n/a | n/a | n/a | n/a |
| `Vector512<long>` | 192: 0, 64, 128 | **96: 0, 16, 80** | = | **80: 0, 8, 72** | n/a | n/a | n/a | n/a |

### IL2CPP and Unity's Mono

Unity's tests log the layouts of a few types (`Assets/Test/Runtime/BclLayoutTests.cs`; CI run 41270331318: Unity
2022.3.62f2 and 6000.3.27f1, the editor's Mono and a Linux x64 IL2CPP player, the same on all four):

| Type | CoreCLR x64 | Unity Mono and IL2CPP |
| --- | --- | --- |
| `(int,long)` | 16: Item1 8, Item2 0 | **16: 0, 8** |
| `(byte,short)` | 4: Item1 2, Item2 0 | **4: 0, 2** |
| `(byte,long,int)` | 16: Item1 12, Item2 0, Item3 8 | **24: 0, 8, 16** |
| `KeyValuePair<int,long>` | 16 | = |
| `DateTimeOffset` | 16: minutes 0 (`int`), UTC 8 | **16: UTC 0, minutes 8 (`short`)** |
| `Around<(int,long)>` | 32 | = |

IL2CPP also drops the custom attributes on generic parameters, which the runtime resolver reads; how it gets them
instead is in [v4-design, "Formatter registrations"](v4-design/runtime.md#formatter-registrations).
