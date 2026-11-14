# Generated read paths

Part of the `audit-generated-code` skill. The measured rules for what Nino.Generator emits on the read side (raw
groups, regions and `EndRegion`, context-preserving formatter traits, keyed members and their fallback switch, the
read body's methods, reuse of strings), each with its evidence and the attempts that lost. Read it before changing
`BodyEmitter`'s read paths, `FormatterTraitsOf` or a built-in formatter's traits, or when a generated deserializer
got slower or larger. How the JIT inlines and folds these paths is `optimize`, references/jit.md.

How every bullet here was proven: the source and IL of the fixture's read body, its Arm64 Tier 1 or FullOpts listing
and the offline (Crossgen2 / cross-JIT, or emulated) x64 listing, local paired timings on Apple M4 at offsets 0, 1
and 7 against identical-binary (A/A) controls with the same Core DLL on both sides, steady allocation, and the wire
and round-trip comparisons; then the targeted test suites on both Roslyn builds. Local timings decide only between
candidates; a published claim still needs runner numbers (`benchmark`).

## Contents

- Raw groups
- Regions and context-preserving formatters
- Keyed members
- The read body's methods
- Reusing strings

## Raw groups

- **Keep the remaining-length guard followed by `ReadBlock` for a complete version-tolerant raw group; keep the
  checked member-by-member fallback for old data and truncated fields.** The JIT folded the duplicate source check
  in the inspected Arm64 and x64 listings.
  - Do not use a ref-returning `TryReadBlock` with an out flag: it reduced generated source by 706 bytes but enlarged
    the Arm64 `PaddedItem[]` read loop and slowed the local A/B.

## Regions and context-preserving formatters

- **Omit `EndRegion` for a version-tolerant member region whose raw members and formatters all preserve read
  context; keep `ReadRegion`** to validate lengths and any section of deferred bodies from newer writers. A formatter
  preserves context when its read never queues deferred bodies or registers references with the reader; classify it
  in `FormatterTraitsOf`, and propagate the trait through a wrapper only when its inner formatters are raw or
  preserve context themselves (keep unknown and custom formatters on the writeback path). Cover each new
  classification with an older empty region, truncation and the deferred-body tests, and keep the unaligned raw reads
  and a region at offset one in the probe.
  - The first classification (built-in strings, raw arrays and lists, and dictionaries with raw keys and raw or UTF-16
    string values): the Arm64 `PaddedItem[]` loop dropped from 572 to 452 machine-code bytes and `NamedItem`'s body
    from 1,136 to 940.
  - Fixed-stride nullable arrays and lists (context-preserving without the separate flat trait; they validate
    presence bytes and advance the reader) (`1dc38168`): the 582-file unit-test replay saved 254 bytes across four
    formatters. The mixed nullable array/list probe saved 73 source bytes and moved the reusable read body from 129 to
    120 IL bytes, 500 to 416 Arm64 FullOpts bytes and 230 to 216 offline x64 bytes. Four M4 default-JIT pairs showed
    about 3–5% lower medians for 16, 512 and 1,024 objects at offsets zero and one, with a mixed single-object control.
    All 84 samples allocated 0 B. Wire bytes matched exactly, and the legacy empty region, truncation and deferred-body
    tests passed.
  - Scalar `DateTimeOffset` (a checked fixed-width block) (`47dda0cb`): the 582-file replay removed 38 bytes from
    OffsetBox only. The inline read body went from 62 to 54 IL bytes; its caller went from 484 to 376 Arm64 FullOpts
    bytes and 196 to 182 offline x64 bytes. Four same-Core M4 pairs at each size and offset had mixed timing, so it
    landed for code size. All 84 samples allocated 0 B and four serialized arrays matched byte for byte. The two
    Roslyn builds and .NET 8 passed 54 targeted tests each.
  - `NullableFormatter` over a raw or context-preserving inner formatter (`49ba68fe`): the real EnumHolder lost its
    EndRegion call and saved 35 bytes across the 582-file replay. Its ReadBody fell from 65 to 56 IL bytes, 3,124 to
    2,724 Arm64 FullOpts bytes and 823 to 809 offline x64 bytes. Four same-Core M4 default-JIT pairs per case gave
    4–9% lower medians at 16, 128 and 512 objects, with mixed repeats and a flat single-object control. All 84 samples
    allocated 0 B and serialized arrays matched byte for byte. Three configurations passed 59 targeted tests each.
  - `BigInteger`, `Uri` and `Version` (scalar data and strings only): the 583-file unit-test replay removed 38 source
    bytes from the new BCL scalar fixture and kept the other files identical. Its ReadBody fell from 109 to 101 IL
    bytes, 1,376 to 1,272 Arm64 FullOpts bytes, and 1,118 to 1,020 emulated x64 bytes. Four M4 pairs at 512 objects
    with absent URI and version values reduced median time by 8–9% at offsets zero and one, at 0 B allocation per
    object. Short and non-null controls varied; allocations and all 12 wire comparisons matched.
  - `StringBuilder`, `CultureInfo` and `TimeZoneInfo` (their reads can allocate or reuse the value, but do not change
    reader context): the isolated formatter's ReadBody fell from 109 to 101 IL bytes, 1,324 to 1,232 Arm64 FullOpts
    bytes and 1,036 to 956 emulated x64 bytes. Four M4 pairs improved the unaligned 512-item null case by 7–11% at
    0 B per item. Non-null timings varied, including identical-binary controls, and retained 32 B per item. All 12
    wire cases matched.
  - `BitArray` (copies the bit payload and updates the collection): the generated read body went from 47 to 39 IL
    bytes, 556 to 436 Arm64 FullOpts bytes and 428 to 295 emulated x64 bytes. Four M4 replays with longer warmup and
    QoS gave mixed timings, including the identical-binary control. Reuse allocated 0 B and all ten wire comparisons
    matched, including payloads on both sides of 64 KiB.
  - `Memory`, `ReadOnlyMemory`, `ArraySegment` and `ReadOnlySequence` over a raw or context-preserving element
    formatter (their readers create no context apart from the element read): the Memory<string> fixture lost 38
    source bytes and went from 47 to 39 IL bytes, 764 to 644 Arm64 FullOpts bytes, and 623 to 483 emulated x64 bytes.
    Four M4 pairs improved the aligned 512-item, two-string reuse case by 2–22%; short, unaligned and larger cases
    varied. All 18 wire comparisons matched at 0 B per reused item.
  - `ArrayFormatter` and `ListFormatter` (their collection management does not change reader context): ten unit-test
    formatters lost 362 source bytes in total. In a string array/list fixture, ReadBody went from 78 to 70 IL bytes,
    1,472 to 1,328 Arm64 FullOpts bytes, and 1,226 to 1,013 emulated x64 bytes. Short and unaligned timing varied
    after longer warmup; the larger batch was within 1% at both offsets. All 18 wire comparisons matched, and reuse
    allocated 0 B.
  - `KeyValuePairFormatter` only when both component formatters are raw or preserve context (the pair only reads its
    components and assigns the result): both Roslyn builds changed one of 583 real-project files, removing 38 bytes
    from PairBox. The generated inline body went from 62 to 54 IL bytes, and its out-of-line body from 540 to 460
    Arm64 FullOpts bytes and 720 to 623 x64 Crossgen2 bytes. Four M4 rounds improved 128- and 1,024-item reuse at
    offsets 0/1/7. Single-item timings varied within 5%, and all 15 wire comparisons matched at 0 B per reuse read.
    Verify that the old empty region keeps the current pair and that both a truncated region and a partial key
    reject. Pairs with tracked components keep the writeback and preserve shared and cyclic identities. The trait
    switch grew from 976 to 1,063 IL bytes and from 2,464 to 2,584 Arm64 FullOpts bytes; the x64 cross listing grew
    from 2,054 to 2,174 bytes. Fresh, cached and method-edit replay differences were comparable to the spread between
    identical baseline controls. Both Roslyn builds reused all 578 tracked models and passed compilation, syntax-tree,
    symbol and generated-text collection checks.
- **Do not extend context preservation to dictionaries with any context-preserving key and value formatters; keep
  the existing dictionary trait.** It changed five real-project files. A string-to-string fixture's read body fell
  from 47 to 39 IL bytes, 864 to 668 Arm64 FullOpts bytes, and 698 to 524 emulated x64 bytes. Short cases varied, but
  four longer M4 pairs of 512 dictionaries with 16 pairs each at offset one slowed by 5–12%, against −2% to +4% for
  the identical-binary control. The allocations and all 18 wire comparisons matched. The candidate was withdrawn.
- **Keep `EndRegion` for version-tolerant collection reads.** Omitting it when all members preserved context shrank
  the `NamedList` body from 1,228 to 1,148 Arm64 Tier 1 bytes and from 1,744 to 1,643 offline x64 bytes, but 16
  ASCII items slowed in all four paired rounds. A guard that skipped the call only at EOF brought the short array back
  into the A/A spread but added 12 Arm64 bytes, 6 offline x64 bytes and 9 IL bytes, with the 1,000-item case still
  slower. Both experiments matched all 27 wire and round-trip probes at zero steady allocation.

## Keyed members

- **Keep the outer `EndRegion` for keyed bodies.** Applying the omission to context-preserving keyed members saved
  936 source bytes in the unit test project and reduced Arm64 Tier 1 `KeyedStruct` read-body code from 1,444 to
  1,352 bytes, but the local unaligned array read slowed by about 2% in each of four alternating rounds. Retesting
  after the member changes below still favored keeping it: pin the same Core binary on both sides and warm 16-item
  reordered reads until the array caller and element loop reach Tier 1, and the apparent large short-array
  regression fell to about 1% (157.85–158.34 to 159.00–159.59 ns in four rounds); the 1,000-item reordered case also
  slowed slightly. Removing the call saved 39 source bytes, 8 IL bytes, 92 Arm64 Tier 1 bytes and 167 offline x64
  bytes, and all 30 wire comparisons passed. The omission stays an experiment, not landed.
- **For a context-preserving member on the keyed in-order path, call `EndMember` only when its reader is not at EOF;
  use the same guard on the shared switch exit only when every member in that switch preserves context.** This
  retains the exact-length error and skips copying the read context on success.
  - The member guard: the `KeyedStruct` fixture gained 14 source bytes and 12 IL bytes but shrank from 1,444 to 1,372
    Arm64 Tier 1 bytes and from 1,338 to 1,251 offline x64 bytes. Four local timings overlapped the A/A spread. All 15
    wire comparisons matched.
  - The switch exit: another 14 source and 12 IL bytes, but the reordered body's Arm64 Tier 1 size fell from 1,440 to
    1,356 bytes and offline x64 from 1,251 to 1,141. After a longer warmup, four local 1,000-item reordered reads took
    9.24–9.39 µs against 9.46–9.47 µs, at zero steady allocation.
- **On keyed fallback switches with at most three members, open the member reader in each known case and discard
  the validated region on the unknown path; keep one shared region read for larger switches** to bound code growth.
  Discarding the region lets the JIT omit the unused child Reader. On `KeyedStruct` with one unknown key per value,
  1,000 reused items went from 8.43–8.49 to 7.35–7.56 µs in four local rounds, at zero steady allocation. Known
  reordered keys also improved, while the in-order array cases overlapped A/A. The file gained 174 source bytes and the
  body gained 33 IL bytes, 324 Arm64 Tier 1 bytes and 384 offline x64 bytes. The alternatives below used the same Core
  DLL, stayed at zero steady allocation, and matched all 30 wire comparisons per candidate:
  - Do not share one region read for known cases after a separate unknown-key check: it saved 52 source bytes, 200
    Arm64 Tier 1 bytes and 304 offline x64 bytes, but increased IL by 7 bytes and slowed 1,000 known reordered entries
    from roughly 8.77 to 9.49–9.76 µs.
  - Do not map each key to an ordinal before reading the shared region: it also slowed that case and took
    8.57–8.68 µs for 1,000 unknown-key entries, against about 7.49 µs with separate reads.
  - Do not share a no-inline region reader while preserving the key switch: it needed a scoped parent reference to
    return the child. The Arm64 Tier 1 body fell from 1,684 to 1,556 bytes but the helper added 236 bytes; offline
    x64 fell from 1,525 to 1,127 + 161 bytes. Generated source grew by 156 bytes and IL by 7 bytes. 1,000 known
    reordered entries took 16.95–17.13 µs against 8.77–8.79 µs, and 16 ordered entries slowed even though they
    bypassed the helper. Kept as a code-generation experiment.
  - Do not add a separate region-skipping API for the unknown-key branch: an experimental `SkipRegion` shared the
    section validator and advanced only the parent, but discarding `ReadRegion` already lets the JIT remove the
    child. The Arm64 Tier 1 body stayed at 1,684 bytes, and offline x64 kept the same instructions and 1,525-byte
    size. Source size matched and IL saved one byte. Four same-Core A/B rounds overlapped A/A.
- **Classify `MemoryFormatter`, `ReadOnlyMemoryFormatter`, `ArraySegmentFormatter` and `ReadOnlySequenceFormatter`
  over a raw element formatter as preserving read context on the keyed path, keeping the existing formatter call;
  keep allocating fresh backing storage** (and the default segment's null marker). Each keyed fixture cost 28 source
  bytes and 21 IL bytes, and all 30 wire and round-trip comparisons passed; the keyed memory regression checks reuse
  and the exact-length error.
  - `Memory<int>` reuse: saved 228 Arm64 Tier 1 bytes and 200 offline x64 bytes. Four local 1,000-item ordered reads
    fell from about 5.47 to 4.90–4.94 µs, and reordered reads also improved. Allocation stayed at zero.
  - `ReadOnlyMemory`: stayed at 40 B per item. Arm64 Tier 1 fell from 2,260 to 2,008 bytes and offline x64 from 3,904
    to 3,472. Sixteen reordered items improved in four paired rounds, while the 1,000-item results overlapped the A/A
    spread.
  - `ArraySegment`: Arm64 Tier 1 fell from 1,928 to 1,712 bytes and offline x64 from 1,666 to 1,466. After longer
    warmup, 1,000 reordered items fell from 9.80–10.04 to 9.35–9.49 µs over four rounds. Ordered results overlapped
    A/A and allocation stayed at 40 B per item.
  - `ReadOnlySequence` (the same array read): stayed at 40 B per item. Arm64 Tier 1 fell from 2,348 to 2,092 bytes and
    offline x64 from 4,012 to 3,557. Four 1,000-item ordered reads went from 8.93–8.98 to 8.29–8.74 µs, and
    reordered reads also improved.
- **Check a constant keyed header and its plain raw value together on read; leave the cursor unchanged on a
  mismatch** so the checked fallback handles truncation, unknown keys and reordered members. Emit little-endian
  constants directly because Reader already rejects big-endian hosts. The unit test project emitted 3,820 fewer bytes
  and the local `KeyedStruct[]` timing was broadly on par.
  - Read keyed bools as bytes through that same helper and validate the byte before assigning it (`e8be71c3`). Arm64
    Tier 1 eliminated the separate payload bounds check and cursor advance, dropping the two-bool fixture's body from
    1,128 to 1,052 bytes. Offline x64 went from 814 to 806 bytes, while IL grew from 298 to 311 bytes and the six
    fixture files grew by 99 source bytes. The pre-existing 583 unit test outputs matched. On M4, the in-order
    offset-7 case improved in both four-round sweeps, by about 20–40% in the confirmation against paired
    identical-baseline averages; other offsets and the reordered case were closer to the control spread. Steady
    allocation was zero. Both builds emitted the same bytes and agreed on all 2,598 payload mutations and prefixes. A
    missing payload behind a matching header now reports invalid length through the checked fallback. All four
    targeted configurations passed.

## The read body's methods

- **Emit the standalone out-of-line read body as the instance method when no inline twin or top-level reader calls
  its static body; keep the no-inline boundary** (`709af0ae`). The unit test project saved 50,736 source bytes
  across 328 of 582 files, with no changes outside the merged forwarders. The string reuse fixture kept its 65-byte
  body and removed an 8-byte IL forwarder. Arm64 FullOpts kept the body at 2,556 B and moved the caller from 612 to
  628 B. In the x64 cross-JIT MinOpts listing the body grew from 233 to 256 B and the caller stayed at 163 B.
  Unaligned 64-character reuse improved in four paired runs and the A/A/B replay; adjacent lengths varied. Wire bytes,
  allocations, 577-model reuse and collection matched. Both Roslyn target suites passed.
- **A struct's read body follows the rule of its write body**: on a local reader where it opens no class body,
  inlined where it is small (`write-paths.md`, "The write body's methods", has the listings and what did not help).
- **Return a null value (presence byte 0) in the twin's inlined `Read`, and read the rest in a static no-inline
  `__ReadTop`**; write a null value in the inlined `Write` (`NinoDirect.WriteNull`). Why: the null read and write
  created a reader or writer out of line, which v3 3.3.13 did not (rc.10 `NinoDeserializeNullableObject` 2.68 ns
  against v3's 0.95, `...Serialize...` 3.50 against 2.55); inlined, 3.05 → 0.80 and 3.67 → 1.43 ns (Xeon Emerald
  Rapids, eight alternated in-process rounds), and a static `__ReadTop` stores no dummy byte for the twin's `this`.
  The earlier form below merged `__ReadTop` into an instance no-inline `Read`.
- **Return directly after reading an absent presence flag in the twin's top-level read**, before any body can acquire
  reference state (`8ab1698e`, which also merged `__ReadTop` into the twin's no-inline `Read`, since split again as
  above). Moving the body without the
  early return repeatedly slowed the null case. The final null-only, mixed and first-call timings varied with
  controls. The 68 affected files saved 4,080 source bytes across all four replay states. `SimpleClass` used 39 IL
  bytes in place of 37 plus a 7-byte forwarder. Arm64 Tier 1 moved the caller from 108 to 120 bytes because it
  materialized the empty receiver, and the reader body from 904 to 928 bytes. Offline x64 moved the caller from 97 to
  113 bytes and the reader from 387 to 394. Wire bytes, old short data, truncation, invalid presence, allocations,
  579-model reuse and collection matched. Each of the three targeted configurations passed 63 tests.
- **Emit twin traits from the same model as the main formatter and return `default` directly when a twin cannot
  defer construction; forward actual construction** so pooling and factories stay in one body (`f0188eea`). This
  saved 8,693 source bytes across 140 files in the real test project. The probe twin's trait getter shrank from 15 to
  2 IL bytes. The Arm64 and x64 FullOpts reader listings kept their code sizes. Four local A/B rounds showed no
  repeatable time change, and wire bytes and per-operation allocations matched. The targeted .NET 8 and 10 tests
  passed.

## Reusing strings

- **Keep the unchanged-string check at the generated field assignment.**
  - Do not add an assembly-wide byref helper for normal string fields: it removed 18,847 source bytes in the real test
    project, the probe read body fell from 167 to 90 IL bytes plus a 19-byte helper, and x64 ReadyToRun fell from 501
    to 377 bytes including that helper, but Arm64 FullOpts grew from 1,500 to 1,524 bytes. After reversing run order,
    unchanged-string reuse was 4–8% slower across four rounds despite matching wire bytes and allocations. The isolated
    candidate passed 82 targeted tests.
  - Do not take a ref local to a private field to remove the second accessor: it may add a live interior reference
    across the string read, and the baseline x64 FullOpts listing already had no accessor calls. On the private-field
    probe the read body IL stayed at 169 bytes, while the enclosing FullOpts read grew from 1,500 to 1,512 Arm64 bytes
    and from 367 to 373 x64 cross-JIT bytes. Unchanged reuse varied within 1.2% across four default-tiering runs and
    allocated 0 B. Changed and fresh reads varied across runs, with matching allocation and wire bytes. The real test
    project replay added 75 bytes across four of 577 generated files. Revisit only when a probe shows an actual
    machine-code or throughput benefit.
