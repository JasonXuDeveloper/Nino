# The JIT: built-in formatters (BitArray, BigInteger, StringBuilder, hashing, caches)

Part of the `optimize` skill, split from [jit.md](jit.md), whose general rules (the inlining budget, the cursor in
registers) apply here. These are the measured experiments on single built-in formatters whose short and long inputs
pull in opposite directions. "Arm64 only; x64 unmeasured" marks a bullet timed only on the local Arm64 host (Apple M4,
.NET 10): its x64 sizes come from cross-JIT, ReadyToRun or composite listings and are code-size evidence, not
throughput.

Rules in short:

- Handle the shortest payloads (empty, one byte, two bytes, one character) on a shared short gate with direct
  unaligned loads and stores, and send everything longer through the existing shared copy kernel, out of line.
- Keep a large loop (the `StringBuilder` chunk loop, the long-payload path) out of line from a dispatcher that also
  serves short inputs.
- Reserve into the output when a BCL API can write there; look up before constructing a cached value.
- Keep a short-input path only when the neighboring sizes and the identical-binary control agree.

## BitArray

- Use the length of the public `BitArray` byte view when reserving its wire payload, and keep the final-byte mask with
  its bounds check (verify dirty padding on unaligned output). Why: the view is already sized for the bits, so slicing
  it again adds an unnecessary check; removing the mask's bounds check did not pay. Evidence: the formatter went from 86
  to 77 IL bytes and from 409 to 369 x64 ReadyToRun bytes; Arm64 timing varied around 4 KiB after changing run order,
  with identical bytes and zero allocations. An explicit nonempty guard let x64 fold the final-byte index check without
  using a byref: the standalone mask was 38 x64 bytes with the guard, 44 at baseline, and 62 with a guarded byref; both
  guarded forms had the same 712-byte Arm64 Tier 1 caller versus 716 at baseline, and neither consistently improved the
  local timings.
- Handle empty and one-byte writes together: store the single byte with its mask applied, and let the existing span
  copy handle larger payloads. Why: a one-byte-only gate moved the empty timings in the wrong direction in the initial
  probe. Evidence: the combined gate beat the identical-binary control on empty and seven-bit values in four local
  rounds at 0 B per write; the 62 and 66 KiB working sets showed no repeatable timing change. Formatter IL grew from 77
  to 114 bytes and the Arm64 Tier 1 caller from 868 to 896 bytes; the x64 cross-JIT caller stayed at 224 bytes, while
  the standalone Tier 0 formatter grew from 274 to 372 bytes. Generated files and wire bytes matched. Arm64 only; x64
  unmeasured.
- Share the short-payload gate when adding a two-byte write case. Evidence: on .NET 10 a single unaligned masked store
  consistently improved 15-bit writes in four local A/B rounds; a separate gate was less convincing at 66 KiB, while
  the shared gate showed no repeatable change at 62 or 66 KiB. Formatter IL grew from 114 to 157 bytes, the Arm64 Tier
  1 caller from 896 to 928 bytes, and the x64 cross-JIT caller stayed at 224 bytes. Wire bytes and steady-state
  allocations matched. Arm64 only; x64 unmeasured.
- Write payloads above two bytes with the shared copy kernel and keep the tail mask. Why: copying a shorter prefix and
  writing the masked tail separately, with either one or two copy sites, gave no repeatable gain around 64 KiB.
  Evidence: with the shared kernel, aligned and partial-byte counts at 62 and 66 KiB beat both controls in four local
  rounds at zero allocation; the short cases varied. IL moved from 157 to 161 bytes and the Arm64 Tier 1 caller from
  928 to 1,220 bytes; the x64 cross-JIT formatter grew from 471 to 694 bytes, with no x64 timing; an Arm64-only guard
  was retained by the offline compiler and grew that listing further, to 758 bytes. Seven wire comparisons and 577
  generated files matched. Arm64 only; x64 unmeasured.
- Leave the size split inside the copy kernel. Why: the alternatives either did nothing or slowed short writes.
  Evidence: calling its existing large helper explicitly while retaining the kernel for small writes kept the Arm64
  Tier 1 caller at 1,220 bytes and took x64 cross-JIT from 694 to 698 bytes; using the span move for payloads up to 64
  bytes instead shrank those listings to 972 and 507 bytes but slowed the 17-bit and 512-bit writes in all four local
  rounds. Formatter IL was 161 bytes at baseline, 177 with the explicit split and 181 with the span move. Both variants
  matched all seven wire comparisons and stayed at zero steady allocation. Arm64 only; x64 unmeasured.
- Skip the span copy for empty and one-byte reads, keeping the multi-byte test first and sharing the existing cleanup,
  including the length setter that invalidates enumerators on reuse. Why: masking inside a new single-byte branch
  slowed three fresh seven-bit rounds, and a separate reuse branch made the seven-bit samples less consistent.
  Evidence: the shared path beat the empty reuse control in all four local rounds at 0 B per read, with no repeatable
  longer-input timing change. Read IL grew from 63 to 98 bytes, Arm64 Tier 1 from 728 to 752 bytes and x64 offline
  from 211 to 272 bytes. The round trips, dirty tails and generated files matched. Arm64 only; x64 unmeasured.
- Use one unaligned load and store for the validated two-byte read too, keeping the shared tail cleanup and enumerator
  invalidation. Evidence: the 15-bit case at offset 7 beat both baseline and identical-binary control in four local
  rounds for fresh and reused values; neighboring 7- and 17-bit reads and working sets around 64 KiB were noisier.
  Read IL grew from 98 to 132 bytes, Arm64 Tier 1 from 752 to 776 bytes and x64 cross-JIT from 272 to 320 bytes.
  Allocation stayed at 0 bytes on reuse and 64 bytes for fresh reads. Arm64 only; x64 unmeasured.
- Use the out-of-line large-block kernel for disjoint reuse above 64 bytes, and keep the span move for overlapping
  input and fresh storage. Why: inlining the general copy brought all short-copy cases into the reader and took the
  x64 cross listing to 713 bytes. Evidence: calling the existing large helper kept the final listing at 440 bytes
  (baseline 320), with IL at 189 bytes (from 132) and Arm64 Tier 1 at 888 bytes (from 772 with a short reuse profile).
  At working sets of about 62 and 66 KiB, four paired local reuse rounds beat both controls at zero allocation; short
  and fresh results varied. Wire checks, including the 64-byte boundary, and all 577 generated files matched. Arm64
  only; x64 unmeasured.
- Reserve the output array and its offset when a BCL copy API accepts those directly. Why: the portable
  `BitArray.CopyTo` path then writes into the output instead of renting and copying a temporary. Evidence: for 24 KiB
  of bits the active payload footprint falls from about 72 KiB to 48 KiB, with the same padding on the wire
  (`a0ee2414`).

## BigInteger

- Decode a portable `BigInteger` payload of at most four bytes as a signed integer after the length check:
  sign-extend the last byte of a three-byte payload and use the existing unaligned reads for two and four bytes. Why:
  this removes a 32-byte temporary allocation per short value on CoreCLR running the netstandard2.0 build.
- Keep the four-byte read cutoff until larger reads avoid regressing the controls. Why: reading five to eight bytes into
  a signed long (sign-extend the upper word, preserve the low word) removed one 32 B temporary array on the local
  Arm64 .NET 10 host running the netstandard2.0 build, but no variant held on nine-byte values. Evidence: inline and
  small-helper variants slowed nine-byte values in ordered mixed runs, with some slower controls also in randomized
  runs; moving all reads above four bytes into one helper removed the consistent mixed nine-byte slowdown after
  shuffling the case order, but its positive nine-byte first-read median was still about 6% slower in the follow-up
  randomized comparison with two identical baseline controls, so it was withdrawn too. That variant changed the
  formatter from 159 to 150 IL bytes plus a 132-byte helper, the Arm64 public read from 644 to 456 bytes plus a
  400-byte helper, and the x64 composite read from 384 to 276 bytes plus a 281-byte helper (code-size evidence only).
  All 48 wire comparisons and 15,744 signed payload probes matched, with the nonempty payloads also checked for
  truncation. Arm64 only; x64 unmeasured.
- Keep the BCL write path on modern .NET. Why: a direct store for one to four bytes improved those cases after tier
  promotion but slowed wider writes, and branching before the reservation instead of sharing it did not fix the wider
  cases. Evidence: in the same public-entry probe, Arm64 Tier 1 for a two-byte value went from 1,728 bytes to 1,052 and
  1,152, respectively; formatter IL went from 36 to 96 and 103 bytes, and x64 cross-JIT formatter code from 204 to 271
  and 386, while the x64 caller stayed at 157 bytes. Six wire comparisons at offsets 0, 1 and 7 matched for both
  variants, with zero steady allocation. Arm64 only; x64 unmeasured.

## StringBuilder and strings

- Copy every chunk of a builder longer than one character with `NinoUnsafe.Copy` (`GetChunks`), never
  `StringBuilder.CopyTo` into the reservation. Why: `CopyTo` copies with the BCL's Memmove, and Nino's copy kernel is
  tuned per vendor (`vectors-copies.md`); a local Intel run showed no difference, so only runners catch it. Evidence:
  1b2e3404 sent builders over 64 characters through `CopyTo`; the runner A/B of the rc.10 review head against main
  measured `NinoSerializeStringBuilder` (1,000 characters) at 0.29 to 0.38x on EPYC 9V74 and 0.63 to 0.68x on
  Neoverse-N2, in every job, and the main-vs-main floor did not show it.
- Rent a character buffer for portable `StringBuilder` writes of 4,096 or more characters, use the existing UTF-16
  reservation, and copy only the builder's length. Why: a 1,024-character cutoff regressed multi-chunk builders.
  Evidence: 4,096-character single- and multi-chunk cases improved without a steady-state allocation; an empty pool
  bucket still allocates on the first rent.
- Write one UTF-16 builder character directly on modern .NET, with the existing short chunk loop out of line. Why:
  keeping the loop inline enlarged the Arm64 Tier 1 caller from 1,552 to 1,636 bytes and did not reliably improve the
  single-character case. Evidence: outlining it took the caller to 988 bytes plus an 812-byte helper and beat both
  controls on one character in four rounds; the x64 cross-JIT formatter moved from 921 to 573 bytes plus a 459-byte
  helper; copy dispatch IL went from 108 to 52 bytes plus an 82-byte helper. Other sizes and repeated multi-chunk
  rounds were noisy. Wire bytes and steady-state allocation matched. Arm64 only; x64 unmeasured.
- Pack a two-character payload in the short helper. Why: a second inline length check slowed three-character writes,
  and sharing the inline check with the single-character path moved its timing in the wrong direction. Evidence:
  outlining kept the two-character Arm64 caller near baseline (848 to 852 bytes), while the helper grew from 816 to
  1,084 Tier 1 bytes and from 459 to 522 x64 cross-JIT bytes; dispatch IL was 52 to 53 bytes and helper IL 82 to 111.
  Four cross-chunk CPU-time pairs beat both controls, while wall time did so in three rounds; single-chunk results
  overlapped A/A. Eight wire samples and 577 generated files matched, with zero steady write allocation. Arm64 only;
  x64 unmeasured.
- Keep the shared pointer append in the UTF-16 reader for now. Why: replacing it with the span overload gave no lasting
  timing advantage. Evidence: the span overload kept bytes, truncated-input errors and zero allocations in the reuse
  probe, and reduced the Arm64 Tier 1 formatter from 1,280 to 1,220 bytes but increased x64 ReadyToRun from 388 to 413
  bytes; no timing advantage persisted across the measured lengths.
- Append a single Latin-1 character directly instead of sizing the builder and widening a chunk. Evidence: the
  one-character reuse probe fell from about 30 ns to 6 ns in four local rounds, with zero allocations and identical
  bytes; longer inputs had no stable regression. The helper went from 97 to 111 IL bytes and 421 to 450 x64
  ReadyToRun bytes.
- For two to sixteen Latin-1 characters, widen into one small stack buffer and append once, with the stack allocation
  out of line from the single-character and chunk paths. Evidence: local Arm64 reuse probes for 2, 8 and 16 characters
  improved in all four rounds, at zero allocations; the 17-character control was mixed. Output bytes matched, including
  unaligned input, and a test covers embedded nulls and high Latin-1 characters. Arm64 only; x64 unmeasured.
- Extend the one-append approach to 256 characters through a separate bounded stack helper, and move the existing
  chunk loop out of line as well. Why: keeping the chunk loop in the dispatcher regressed the 16,384-character reuse
  probe even when the short helper was unchanged. Evidence: with the loop isolated, 64 and 256 characters improved in
  every local round, and 257 and 16,384 recovered and improved too; Tier 1 was present on both sides, with the probe
  caller shrinking from 2,004 to 1,336 Arm64 bytes at 16,384 characters. The dispatcher fell from 125 to 54 IL bytes,
  with separate 26- and 97-byte helpers. Wire bytes and steady-state allocations matched.
- Keep the existing chunk growth of the cleared builder for now. Why: pre-sizing it before the chunk loop saved 16 B
  when growing from a small capacity but regressed 1,024 characters; restricting it to insufficient capacity at 4,096
  or more characters improved growing builders at 4,096 and 16,384 in all four local rounds, yet the 4,096-character
  reuse control slowed in each round. Evidence: the guarded helper grew from 97 to 122 IL bytes, 928 to 992 Arm64
  FullOpts bytes and 421 to 472 x64 ReadyToRun bytes. Wire bytes matched.
- Keep the current seeded string hash loop. Why: an unaligned UTF-16 word load and a byref tail preserved the reference
  hash and removed range failures (Arm64 588 to 428 bytes, x64 611 to 550), but four-character inputs slowed by 6–8%
  across four local default-tiering rounds and 15-character inputs also slowed.

## Caches

- Use a direct lookup before constructing a cached value when the target has no stateful factory overload; on a miss,
  pass the value to `GetOrAdd` and return the value that wins publication. Use the stateful factory overload where the
  target supports it. Why: the new hit path removed the closure and allocation calls in both Arm64 and x64 listings
  (the portable layout cache's factory already computed outside the dictionary lock).
