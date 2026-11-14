# Bounds checks and vector loops (.NET)

Part of the `optimize` skill: removing bounds checks, reusing a length proof instead of checking twice, the SIMD
dispatch policy and the rules every vector loop follows on x64 (AVX-512, AVX2, SSSE3), Arm64 (tbl, ldp/stp) and the
netstandard builds. Read it before writing or changing a loop over elements; then read the file for the concern you
touch, with the per-CPU decisions (Ice Lake, Zen, Neoverse) and the kernels measured and not taken:

- [vectors-checks.md](vectors-checks.md): checks of values in a block: bools, strided bytes, value ranges (Untrusted
  mode), the bool prepass of block reads.
- [vectors-nullable.md](vectors-nullable.md): nullable slots: presence-flag scans, the fused fresh read, nullable bool
  reads and writes.
- [vectors-blocks.md](vectors-blocks.md): blocks of padded structs: permutes and lane groups, the per-CPU gates (Ice
  Lake), uninitialized allocation, reservations, the scalar block reads and writes (`BlockWaypoint`).
- [vectors-copies.md](vectors-copies.md): copies: the Arm64 fixed head and tail for 65-128 bytes, per-vendor long
  copies, `BitArray`, sequences, byte writers, the masked copy (`NinoMask`).
- [vectors-strings.md](vectors-strings.md): strings: Latin-1 comparison, widening and narrowing, UTF-8 and UTF-16
  payloads.
- [vectors-collections.md](vectors-collections.md): `SortedList` ordered keys and split, pair interleave,
  `PriorityQueue` heap checks.
- [vectors-multidim.md](vectors-multidim.md): multidimensional arrays: heavy reads, headers and shape checks.

Platform scope: a bullet that names a GitHub runner run (or "runners") was measured on every runner CPU. The others
were timed on a local Apple M4 (Arm64, default JIT) or, where they say so, on an emulated x64 AVX2 guest; their x64
evidence is listings only (Crossgen2, cross-JIT), and the rules lists mark them "M4". Measure on native x64 before
carrying such a threshold or verdict there (`benchmark`).

Contents: [Rules](#rules), [SIMD dispatch](#simd-dispatch), [Bounds checks](#bounds-checks), [Reusing a length
proof](#reusing-a-length-proof), [Vector loops](#vector-loops).

## Rules

- Keep SIMD capabilities and CPU policy in `NinoSimd`; dispatch 512, then 256, then 128, then scalar with foldable
  checks; audit the dispatch with every loop it reaches before calling a width covered.
- Write a byte-vector kernel once over `INinoVectors<TVector>` (`NinoVectors128/256/512`, `NinoVectors` for
  `Vector<byte>` in the netstandard builds) and add an operation there only when it is the same per width for every
  kernel; a kernel whose per-width work is its own (Latin-1 widening compares, bool slots on 16-bit lanes and their
  `Vector64` / word shapes) keeps its own structs. Why: the masked copy and the flag checks each declared the same
  load, store, and/or per width. Evidence: merged, with `default(TOps)` instance calls in place of the flag checks'
  static abstract ones, every one of 1,453 (AVX-512) and 1,442 (`DOTNET_EnableAVX512=0`) Tier1 methods of an `asm.py`
  dump was identical after normalization, and `NinoMask.Copy`, `CopyVectors`, `CopyFlags` and `ValidFlagBlocks`
  (which no benchmark case reaches) matched at 512, 256 and 128 bits on x64 and at 128 on the Arm64 cross JIT, `CopyPairs`
  included (FullOpts, `DOTNET_EnableAVX2=0` for the 128-bit instantiations; Emerald Rapids, round of the core dedupe).
- Index through a ref to the first element and `Unsafe.Add` behind the one length check that makes it safe; validate a
  pooled array's exact element type through a span before storing through a byref.
- Reuse a length proof already made (fixed-stride arrays, lists opened at the exact count, the nullable reader,
  `Memory<T>`, queue and stack backing arrays, multidimensional shapes) instead of comparing again (M4).
- Bump `ref` cursors and count down, with everything loop-invariant hoisted. Several loops measured otherwise and keep
  an index: the scalar masked copy (vectors-copies.md), the `SortedList` reuse split and the narrow zip loop
  (vectors-collections.md), the `BlockWaypoint` write countdown (vectors-blocks.md).
- On Arm64 take the `Vector128` parts of wider vectors out of the loop, keep hot state in registers and build `tbl`
  tuples from loads made once per iteration.
- Load and store `Vector<T>` in the netstandard builds with `Unsafe.As`, never `ReadUnaligned`/`WriteUnaligned`.

## SIMD dispatch

- Keep shared managed SIMD capabilities and CPU policy in `Nino.Core/Internal/Simd/NinoSimd.cs`. At kernels with
  equivalent implementations, check 512, then 256, then 128, then scalar. Use inlinable capability checks so the JIT
  can discard unsupported widths. Keep each kernel’s length, alignment, exact ISA requirements and tail beside its
  loop. Unity and WebGL register function pointers through `NinoKernels`; `Internal/Simd/NinoKernelHook.cs` owns their
  dispatch. Generated formatters call Core entry points and leave ISA selection to the runtime. Keep the complete text
  SIMD entry and loop in `NinoSimd.Text.cs` or `NinoSimd.Text.Legacy.cs`, so the exact ISA guard can fold in Tier 0.
  Leave string headers, scalar handling and the legacy native hook ordering in `NinoStrings`. Moving the guarded
  methods preserved the sampled Arm64 code sizes and the aligned fallback timings (`2a596daf`).
- Audit the dispatch and every reachable loop together: a 128-bit tail, mask seed or Arm64-only helper does not
  establish the width of the whole operation. Conversely, `System.Numerics.Vector<T>` does not establish an explicit
  512-bit path. The November 11 audit after `ed6b9311` found managed 512/256/128 paths for nullable bool slots, flag
  validation, long Latin-1 text and packed pair interleaving. Short Latin-1 reuse now has dedicated 512/256-bit
  adapters (the short-text entry in [vectors-strings.md](vectors-strings.md)); outstanding width work remains in
  ordered-key validation (`AreStrictlyIncreasingInt32Vector`, Arm64-only `SplitOrderedPairs`), `NinoValueChecks`, and
  Burst's text loops.
  Keep those findings open until their actual entry points, exact ISA guards and tails have been implemented and
  checked; never report all SIMD covered from one widened kernel.

## Bounds checks

- Index an array or span on a hot path through a ref to its first element and `Unsafe.Add`, never `a[i]` or `span[i]`:
  take `ref MemoryMarshal.GetReference(span)` or `ref NinoUnsafe.GetArrayDataReference(array)` once (the wrapper
  covers netstandard2.1, which lacks `MemoryMarshal.GetArrayDataReference`; on Mono a `ReadOnlySpan` indexer call
  also fails outright, `platforms`), check the length once against what the
  loop will touch, then walk with `Unsafe.Add(ref first, i)` or a bumped cursor. Why: the JIT removes a check only
  when it can prove `i < Length` (a `for` over `0..Length` of the same local); anything else (two arrays, an index
  from the data, a count read earlier, `i + 1`) leaves a compare and a `CORINFO_HELP_RNGCHKFAIL` call per access, and
  the throw block also costs the method its inlining budget. On IL2CPP the gain is larger: it emits its own check on
  every array access unless the project turns it off, and `Unsafe.Add` has none.
- The one check you keep is what makes it safe: on a read path it is the length check against the bytes left
  (`audit-security`), and every `Unsafe.Add` must stay inside what that check covered. Never `ref array[0]` (throws on
  an empty array, and is a bounds check itself); `GetReference` of an empty span is a valid ref that must not be read.
  `ref span.GetPinnableReference()` is the same as `GetReference` but returns a null ref for an empty span: use it
  only with `fixed`.
- Find the checks left: `CORINFO_HELP_RNGCHKFAIL` in the Tier1 listing (`benchmarks/asm/asm.py dump --asm FILE`), counted
  per method; a hot method with one in its loop is a finding, one on a cold path out of line is not.
- Validate a pooled reference array's exact element type with a writable span before obtaining a byref for stores. The
  shared pool checks the returned array's length, and its API expects clients to return their own rentals. In the
  dictionary refill probe, a checked span or its first ref removed the array-store helper and helped 17 string-key
  pairs, but the larger cases stayed on par and the formatter's machine code grew.
- Go, Rust and TypeScript: `optimize-runtimes`, references/blocks.md.

## Reusing a length proof

- Reuse a length check that already proved a fixed-stride payload fits (and opened a destination of exactly that
  count) instead of comparing again before reading it; keep every check that validates content (presence flags,
  bools). M4 timings and offline x64 listings; no x64 throughput was measured.
  - Recheck fixed-stride array reads before bypassing the second bounds check. The length check already proves the
    block fits before allocation, so an array-only probe advanced the reader without repeating the comparison. The
    span-read IL grew from 252 to 255 bytes plus a helper. Arm64 Tier 1 caller code fell from 644 to 620 bytes, and
    offline optimized x64 from 1,071 to 1,017. At offset 1, the 65-item SIMD case was 1.8–5.9% slower in four
    follow-up rounds, while the byte-identical A/A control ranged from 2.8% faster to 9.8% slower. With intrinsics
    disabled it stayed within 1%. The one-item and 1,700-item results varied. Wire, dirty-padding, truncation and
    invalid-bool checks passed at offsets 0, 1 and 7, with 0 B allocated per read. Pin the probe arrays and select
    actual input and output alignments before comparing SIMD timing. `Lanes` matched after normalizing addresses at
    both output alignments. At output alignment zero and input offset one, the four rounds improved 4.2–9.7%; the
    other offsets and output alignment eight were mixed. The original slowdown did not reproduce. Retain the
    array-only path. All Core targets built and 106/107 targeted tests passed on .NET 8/10 with and without hardware
    intrinsics (`b9f1a4be`).
  - A list opened at the exact validated count can use the same path. The reused-list Arm64 hot branch already folded
    the duplicate comparison and the Tier 1 caller stayed at 1,216 bytes. Offline x64 shrank from 1,446 to 1,392, with
    unchanged formatter IL. Pinned-alignment A/B and byte-identical A/A both varied across four rounds, with 0 B
    allocated. Wire output matched at offsets 0, 1 and 7, and 111/112 targeted tests passed on .NET 8/10 with and
    without hardware intrinsics (`d3a36d9a`).
  - Use the same proof for the shared nullable array/list reader. Every caller checks the whole wire stride and opens
    a destination of that exact count. Reuse the byte count for advancing and copying, and keep the presence-flag
    checks. The helper went from 169 to 163 IL bytes. For `int?` and `bool?`, Arm64 FullOpts went from 192/248 to
    148/212 bytes, and x64 cross-JIT from 405/283 to 389/261. Local reuse stayed at 0 B with no repeatable timing
    change against the duplicate baseline, including the 64 KiB boundary. Wire, truncation and invalid-flag checks
    matched at aligned and unaligned offsets.
  - Apply the same rule to `Memory<T>` after the exact-length check and the growing fallback. In the aligned
    reused-memory probe the Arm64 Tier 1 entry went from 828 to 812 bytes and the formatter from 768 to 756. Offline
    x64 formatter code went from 1,015 to 975, and formatter IL stayed at 83 bytes. Four A/B and A/A rounds varied,
    with 0 B allocated and byte-identical writes at offsets 0, 1 and 7. The 98/99 targeted tests passed on .NET 8/10
    with hardware intrinsics on and off (`c82748e0`).
  - For queue and stack backing-array reads, reuse both the capacity proof and the fixed-stride length validation.
    Removing only the payload comparison repeatedly slowed 65 items in the first aligned probe. Creating the span from
    its validated ref and length brought both 65-item timings back within the variable A/A range. Arm64 Tier 1 callers
    shrank by 44 bytes each and offline x64 `ReadInto` went from 780 to 686 bytes. IL went from 127 to 130 bytes. Wire
    bytes matched at offsets 0, 1 and 7, allocations were 0 B, and 111/112 targeted tests passed on .NET 8/10 with
    intrinsics on and off (`ef43854c`).
  - Reuse the shape validation for 2D, 3D and 4D fixed-stride array reads, updating the generator template and
    checked-in output together. In the aligned 2D probe the formatter went from 181 to 182 IL bytes, 1,176 to 1,164
    Arm64 Tier 1 bytes, and 973 to 926 offline x64 bytes. Four A/B and A/A rounds varied, all writes matched at
    offsets 0, 1 and 7, and reads allocated 0 B. The 87 targeted tests passed on each of .NET 8 and 10 with intrinsics
    on and off (`1c1e62df`).

## Vector loops

- Bump `ref` cursors and count down (`at = ref Unsafe.Add(ref at, step)`, `while (--n != 0)`), never `(nuint)i * size`
  per iteration; take everything that does not change per iteration (masks, indices) out of the loop into locals.
  Scalar tails too: `NinoBlockCollections.WriteEach`'s per-value loop with an index scaled twice per value wrote 1,000
  dictionary entries in 658 ns without hardware intrinsics, with cursors 393 (main's per-entry loop: 399).
- On Arm64 (and anywhere without AVX-512) `Vector512<T>` and `Vector256<T>` are structs of 16-byte vectors: passed
  into an inlined loop body they are copied through the stack every iteration. Take their `Vector128` parts out once,
  before the loop, and pass those.
- ARM64 is a load/store architecture: what x64 does in one instruction on memory is a load, an operation and a store
  there, and 64-bit constants and addresses take several instructions. Keep hot state in registers.
- Arm64 `tbl` with two to four table registers needs consecutive registers: build the tuples from loads made once per
  iteration, before any lookup (`(w0, w1)`, `(w1, w2)`), never from per-lookup loads, or the JIT adds `mov`s. Each
  table register is a micro-op on Neoverse cores: give each lookup only the registers its bytes come from, and store
  only the bytes the values fill (`NinoBlockCollections.WireTables64`); check each shape, as types that compile to the
  same code can differ by the data (`(byte, int)` took twice as long as `(short, int)`).
- Load and store a `Vector<T>` in the netstandard builds with `Unsafe.As<byte, Vector<T>>(ref at)` (plain `ldobj` /
  `stobj`), never `Unsafe.ReadUnaligned` / `WriteUnaligned`: classic Mono compiles the `unaligned.` prefix on a struct
  wider than its inline-copy limit to a call to memcpy through a stack slot (`mini_emit_memory_load` in
  mono/mini/memory-access.c), and plain ones to `movups`. RyuJIT never assumes a vector aligned, so its code is the
  same. Evidence: `NinoMask.CopyVectors` on Mono 6.8 serialized 1,000 padded structs in 36 to 40 µs with
  `ReadUnaligned`, 8.4 to 9.2 with `Unsafe.As` (11.8 to 13.4 with ulongs); its .NET 10 listings (netstandard2.0 and
  net10.0 builds) were identical. Which `Vector<T>` operations Mono accelerates at all is `platforms`.
