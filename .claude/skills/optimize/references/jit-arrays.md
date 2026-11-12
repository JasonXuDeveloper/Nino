# The JIT: raw arrays, spans, memory and external buffer writers

Part of the `optimize` skill, split from [jit.md](jit.md), whose general rules (the inlining budget, the cursor in
registers) apply here. These are the measured experiments on copying unmanaged payloads: raw array reads and writes,
`ArraySegment<T>`, `Memory<T>`, `ReadOnlySequence<T>` and multidimensional arrays, and writing them straight into a
caller's `IBufferWriter<byte>`. "Arm64 only; x64 unmeasured" marks a bullet timed only on the local Arm64 host (Apple
M4): its x64 sizes come from cross-JIT, emulated, guest or offline listings and are code-size evidence, not
throughput.

Rules in short:

- Reuse a length or range that was already checked (the `ArraySegment<T>` range, the wide byte count, one payload
  slice) instead of slicing or multiplying again.
- Copy raw writes to caller buffers with `CopyToWire` (Nino's copy, or the masked copy) when the destination is
  disjoint; keep the overlap-safe span move (plus padding clear) for a destination that overlaps the source and for
  one padded element.
- Write canonical arrays, lists, array-backed memory and segments, single-segment sequences, fixed-size values and
  canonical multidimensional arrays directly into the external writer's span; keep `MemoryManager` values,
  multi-segment sequences and variable formats on scratch.
- Measure full arrays and offset slices separately, on runners, before applying a shared copy threshold.

## Raw arrays and segments

- Reuse the constructor-validated `ArraySegment<T>` range when writing value types on .NET 8+; reference types still
  take `AsSpan` to preserve its covariance check. Evidence: a one-byte slice at offset one beat the paired baseline in
  all eight local rounds and the identical-binary control in six; long copies on either side of a 64 KiB working set
  varied. The formatter IL rose from 33 to 90 bytes; the Arm64 Tier 1 caller shrank from 964 to 932 bytes and the
  concrete x64 cross-JIT write entry from 2,279 to 2,180. Six wire checks and all 577 generated files matched, the
  covariant array still threw, and steady-state write allocation was zero. Arm64 only; x64 unmeasured.
- Keep the existing byte copy dispatch for one-element segments. Why: neither specialization consistently beat the
  identical-binary control for one byte. Evidence: a separate reservation grew the formatter from 90 to 146 IL bytes and
  the Arm64 Tier 1 caller from 948 to 1,088 bytes; specializing after the shared reservation raised `WriteSpan` from 63
  to 115 IL bytes and kept the caller near baseline at 956 bytes. The x64 cross-JIT entry was 2,180 bytes at baseline,
  2,177 with the separate path and 2,122 with the shared path. Seven wire checks matched for both versions and
  steady-state allocation stayed at zero. Arm64 only; x64 unmeasured.
- Keep the existing reservation for canonical span writes. Why: passing the 64-bit payload size from `Writer.Write` to
  a reservation overload removed the separate 32-bit shift from the no-padding copy path but bought no time. Evidence:
  the Arm64 FullOpts callers stayed at 1,896 bytes, the x64 cross-JIT callers shrank by 14 bytes, and the changed
  Writer methods grew from 124 to 134 IL bytes; four local default-JIT rounds with duplicate baselines showed no
  repeatable gain for short or cache-sized inputs, at 0 B allocated. Arm64 only; x64 unmeasured.
- Reuse the checked wide byte count in raw array reads. Why: Arm64 Tier 1 then no longer shifts the element count again
  for the copy. Evidence: the sampled fresh and reuse callers went from 1,056 to 1,044 bytes and 1,088 to 1,072; the
  x64 FullOpts cross listing grew by 4 bytes. Four rounds with byte-identical controls showed no repeatable throughput
  gain. The `int[]` probe crossed a 64 KiB input-plus-output budget and matched wire bytes at offsets 0, 1 and 7. Reuse
  allocated 0 B and fresh allocation was unchanged (`30cd066b`). Arm64 only; x64 unmeasured.
- Use the overlap-safe span move for unpadded raw array writes to caller-provided buffers, and keep overlap detection
  for layouts that fuse padding clearing into the nonoverlapping copy. Evidence: the `int[]` write shrank from 800 to
  444 Arm64 Tier 1 bytes and from 640 to 296 in the x64 FullOpts cross listing; at 8,449 items four local rounds
  improved by 19–30%, with the identical-binary control within 9%; short inputs and the 7,935-item replay varied. Wire
  bytes matched at offsets 0, 1 and 7, and writes allocated 0 B (`c311e969`). Arm64 only; x64 unmeasured.
- Slice the payload once and use its checked length for the source span. Why: sharing the source size removes the
  duplicate multiplication and copy length check. Evidence: combining the destination slices alone grew Arm64 Tier 1
  from 444 to 452 bytes; sharing the source size cut Tier 1 to 384 bytes and the x64 FullOpts cross listing from 296
  to 240. Four local rounds and a byte-identical control showed no repeatable timing gain. Wire bytes matched, writes
  allocated 0 B, and targeted tests and both netstandard builds passed (`6590c2a2`). Arm64 only; x64 unmeasured.
- Use the same span move and padding clear for one padded element, reusing the existing branch. Why: it skips both the
  overlap check and the general mask loop; a separate value-write branch grew Arm64 Tier 1 by about 60 bytes.
  Evidence: reusing the existing branch grew it from 564 to 572, and the x64 cross listing from 424 to 432; the 24-byte
  padded struct probe improved by 10–20% for one element in four local rounds, with identical-binary controls within
  9%; multi-element timings varied. Dirty padding still serialized as zero at offsets 0, 1 and 7 (`23574bb0`). Arm64
  only; x64 unmeasured.
- Keep the original `NinoMask.Copy` entry. Why: passing the already checked payload size into it removed its checked
  multiplication but moved that calculation into other callers. Evidence: the sampled padded array writer and mask body
  together went from 1,260 to 1,252 Arm64 Tier 1 bytes and from 748 to 724 x64 cross-JIT bytes; neither
  caller-provided buffers nor `NinoArrayBufferWriter` showed a repeatable timing gain with identical-binary controls.
  The probe matched wire bytes and dirty-padding output at offsets 0, 1 and 7. Arm64 only; x64 unmeasured.
- Keep the shared span read in `MemoryFormatter.Deserialize`. Why: sending a fresh array directly to the reader instead
  of obtaining its span through `Memory<T>` did not change allocations. Evidence: the inline variant grew the Arm64
  Tier 1 caller from 524 to 556 bytes; moving the fresh path out of line reduced the entry from 118 to 54 IL bytes but
  added an 88-byte helper and slowed the single-int fresh case and the 8,192-int reuse case in every local round. The
  variants preserved the wire bytes and read an unaligned source into the same sliced destination.

## Writing into an external buffer writer

- Write canonical arrays directly into the span of a generic buffer writer, reusing `NinoLayout.CopyToWire` for the
  disjoint case and preserving overlap and padding semantics; the raw array dispatch stays out of line from the
  concrete buffer writer path. Why: plain span copying regressed the 8 KiB byte case in four local rounds. Evidence:
  the existing kernel improved it in each rerun and reduced the 8,192-element int path to two active 32 KiB buffers
  instead of three; both builds allocated 0 B after warmup.
- Keep the boxed buffer writer overload on the same path with its repeated formatter lookup; the isolated alternatives
  are kept for future measurement. Why: removing the lookup improved short boxed arrays locally, both when dispatch
  moved into the public method and when the existing helper received the lookup result, but both produced slower
  8,192-item external writer samples in three of four rounds. Evidence: moving dispatch into the caller grew its Arm64
  Tier 1 listing from 668 to 1,880 bytes, while passing the result kept it at 668 bytes.
- Write registered fixed-size values into the external buffer's span: use the same fixed writer as the byte-array
  entry, write the null marker explicitly into dirty memory, and advance only after the bytes are filled; keep scratch
  for variable-sized output and dispatch once through the formatter after the existing direct paths. Evidence: the
  53-byte sealed object moved from 18.5 to 7.2 ns and the 13-byte struct from 6.7 to 4.2 ns in four M4 pairs, with
  offsets zero, one and seven at zero allocation; the first call including initialization also took less time in four
  pairs; a mixed fixed and variable probe showed large A/A variation, and the dedicated mixed loop with longer warmup
  took less time in all four comparisons. `SerializeOther` went from 69 to 64 IL bytes and its Arm64 Tier 1 body from
  1,008 to 648 bytes plus a 428-byte fixed override; the x64 guest went from 731 to 522 plus 334 bytes (instruction
  evidence only). Wire bytes matched on both architectures, all 584 real-project generated files matched, and the four
  targeted configurations passed (`6e21668c`). Arm64 only; x64 unmeasured.
- Share the raw array span writer with canonical unmanaged lists: capture the element span and count before calling
  the destination's `GetSpan`, and write the header after the payload to preserve aliasing; converted layouts and
  variable formats use scratch. Keep the modern unchecked span construction after validating the size, and inline the
  two formatter adapters. Why: the first trial regressed two 128-int unaligned cases. Evidence: at 8,192 ints and
  input-independent destination offset seven, the M4 probe dropped from a median 909 to 414 ns across four
  comparisons, with zero allocation (input and destination take about 64 KiB together before scratch); padded lists
  also improved; with pinned destination placement the 128-int controls were within the A/A spread or faster; the
  final raw-array and string-list controls varied. The list adapter uses 94 IL bytes, the shared copy 144 and the
  dispatcher 43; Arm64 and x64 guest listings confirm that the path skips scratch. Wire bytes, all 584 generated files,
  callback growth and padding tests matched, and the four targeted configurations passed (`e2fb5762`). Arm64 only; x64
  unmeasured.
- Use the same span copy for canonical array-backed memory and segments, but keep `MemoryManager` values on scratch even
  when they expose an array. Why: `GetSpan` can reenter user code that releases their storage. The array-segment null
  marker and an empty memory's zero length remain distinct. Evidence: the final 8,192-int read-only slice at
  destination offset seven went from about 949 to 433 ns on M4, and the 4,096-element padded slice from 1,308 to 803
  ns, at zero steady allocation; the isolated nonraw string-memory control with longer warmup varied alongside A/A;
  first use of the 128-int fixture took less time in four rounds. The common copy uses 128 IL bytes, 348 Arm64 Tier 1
  bytes and 245 x64 guest Tier 1 bytes for int; the raw array and list callers reuse it. The four targeted
  configurations covered overlapping output, dirty destination padding, enums, bools and custom memory managers; wire
  bytes and all 584 generated files matched (`90efac75`). Arm64 only; x64 unmeasured.
- Reuse the memory entry for a single-segment `ReadOnlySequence<T>` (its first memory already describes the complete
  slice), and leave multi-segment output on scratch. Why: there copying one segment cannot overwrite another aliased
  segment before it is read. Evidence: at destination offset seven the 128-int probe moved from 35.4 to 17.2 ns and the
  4,096-element padded sequence from 1,897 to 832 ns in four M4 comparisons, with zero allocation; first-use timing
  improved in all four rounds; multi-segment and nonraw control timings varied. The adapter adds 25 IL bytes; the
  sampled Arm64 Tier 1 external formatter moved from 1,524 to 976 bytes and the x64 guest from 1,239 to 865, excluding
  the shared copy. Both architectures matched the wire output; overlap and custom memory-manager callbacks passed along
  with all four targeted configurations and the 584-file replay (`752a9c5f`). Arm64 only; x64 unmeasured.
- Reserve the shape and payload together for zero-based, canonical raw multidimensional arrays on modern .NET: share
  the raw payload copy, publish the dimensions after copying, and keep the normal formatter for other layouts.
  Evidence: at destination offset seven, four M4 pairs moved the single-int matrix from 8.5 to 6.2 ns, the 8,192-int
  matrix from 777 to 350 ns, and the 4,096-element padded matrix from 669 to 517 ns, at zero steady allocation (the
  8,192-int input and output already occupy 64 KiB before the shape and scratch). The shared copy went from 128 IL
  bytes to 50 plus an 83-byte inlined helper; the shape writer adds 169 IL bytes and the generated adapters add 9 each.
  Its Arm64 Tier 1 listing was 1,028 bytes and the formatter wrapper went from 1,172 to 1,080 bytes; the offline x64 v4
  shape writer was 1,127 bytes and the shared copy went from 878 to 873 bytes. Wire samples matched at offsets zero,
  one and seven, five targeted configurations passed, and all 584 project-generated files matched in each replay phase
  (`0d98165b`). Arm64 only; x64 unmeasured.
- Copy unpadded external payloads with Nino's copy when the destination is disjoint, and keep the span move only where
  it overlaps the source (`NinoLayout<T>.CopyRawExternal`, e7f15a27; the medium-array second writer of a619db65 went
  with it, 7fe8eec9). Why: the span move is Memmove at every size, glibc's rep movsb on AMD (2-30 times slower than the
  loop up to 16 KB, [vectors-copies.md](vectors-copies.md#long-copies-and-writers)). Only writers other than
  `NinoArrayBufferWriter` reach this copy (`NinoDirect.Serialize` and `NinoSerializer.Serialize` take the Writer for
  that one); `ExternalBufferTest` times it (`ExternalInts`, `ExternalMemory`), not yet on runners. Two earlier
  M4-only trials had kept the span move: a 128-byte to 64 KiB gate slowed the 8,192-int array at destination offset
  seven by 25-69 % on Arm64, and a 256-1,024-byte gate kept a 512-byte slice at offset seven 6-38 % slower. A test
  writes 100-5,000 ints over their own backing array, 2 bytes after and 6 before the source
  (`LongRawSlicesCanWriteOverTheirBackingArray`): Nino's copy is no memmove above 64 bytes.
- Do not blame `CopyRawExternal` for the `BclTypesTest.NinoSerializeMemory` / `NinoSerializeReadOnlySequence` runner
  results: those cases write into a `NinoArrayBufferWriter` through the Writer and `CopyOver64`. A runner A/B of
  51a7bd18 against e6f72cd3 (which holds 90efac75 and 752a9c5f) put both 8-15 % slower on Xeon 8370C and EPYC 9V45,
  9V74 and 7763, but the Tier1 listings of both commits call the same `CopyOver64` (identical normalized listing) and
  differ only in the inline copy of up to 15 bytes, which 4,000-byte payloads never run (581 against 591 bytes, x64
  AVX-512, `asm.py dump --ref 51a7bd18 --filter Memory`): same code, so by the A/B rules not a regression of theirs.

The fixed-stride multi-segment `WriteSegments` trials are in [jit-io.md](jit-io.md), "ReadOnlySequence input and
output".
