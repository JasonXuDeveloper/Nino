# Sets and dictionary keys outside .NET

Part of the `optimize-runtimes` skill: checking a set's elements or a map's keys for repeats (Go's open-addressing
table, TypeScript's key guard and `add` loop, Rust's IndexSet). Read it before changing how a runtime reads a set
or a dictionary.

- Check a set's elements or a dictionary's keys for a repeat in an open-addressing table of the reader's own (Go
  `unique.go`: a slot array kept in `Reader.keys` across reads, linear probing from a multiply-shift hash with a
  per-process random multiplier for numbers, `hash/maphash` with a per-process seed for strings, a bit per key for 1-
  and 2-byte keys), with a bound on the probes of one key that falls back to a sort or a Go map, never a sort or a Go
  map first. Why: sorting a copy of 1,000 int32s took twice protobuf's whole read, and a Go map for strings a lookup
  plus an insert per element. Runner numbers (runtime A/B run 41042990902, jobs of 12 rounds against main's bench
  with sets, floor within 0.98-1.01): a `HashSet<int>` of 1,000 read in 0.16-0.19 of the time on EPYC 7763, 9V45,
  9V74 and Neoverse-N2, a `HashSet<string>` of 1,000 in 0.54-0.66; SimpleClass on par. Read a set of primitives as one
  block (`ReadPrimitives`) before the check. Rust: reading an `IndexSet<i32>`'s elements as one block of
  `decode_fixed` instead of `read_key` per element did not move it locally (SipHash dominates the insert), so it was
  not landed; Nino already led every serde serializer and prost made into the set (run 41042990902: deserialize
  1.14-1.37x faster than each on EPYC 7763 and Neoverse-N2).
- TypeScript: a `Set` or `Map` of numbers or bigints from the data takes `NinoKeyGuard` (V8 hashes them without a
  seed; `audit-security`). Count only the final table's buckets: following every growth of V8's table (the hashes
  kept, recounted on each doubling) took twice the time per key locally; a pre-pass over a block of fixed-size keys
  before the add loop was no faster than the check inside it (44.7 against 44.0 us for 1,000 ints). The guard cost a
  `HashSet<int>` of 1,000 4-9 % in deserialize on runners (run 41042990902, Node and Chromium). Keep a loop of `add`
  for a fresh set: reading the elements into an array and building `new Set(elements)` was 10 % faster locally and
  0.87-0.99 of the time in Chromium, but 1.06-1.15 in Node 24 on every CPU (run 41067631023), so it was not landed.
  Why Chromium differs: its V8 compresses pointers, so Smis are 31-bit and an int32 at or above 2^30 is a heap
  number, while Node's are 32-bit. `Set.prototype.add` is slow on keys that mix Smis and heap numbers (the bench's
  `rng.Next()` values, half of them past 2^30), and the constructor's loop over a double array is not. Locally, 20
  sets of 1,000 (Chromium 141, the runners' Playwright 1.56.1, and 153 alike): `add` loop 1,340 us mixed, 1,050 all
  past 2^30, 800 all under it; `new Set(array)` 1,000-1,040 in all three, so it loses 30 % on small ints, which most
  real sets hold. Node 24 (all Smis): the constructor 1,060 against 810. Making it pay would take an engine check
  (no JavaScript semantics show the Smi width) plus a per-set range check, for a win in Chromium on this data only,
  so the `add` loop stays. Main against msgpackr (run 41198256082, medians of jobs, msgpackr time over Nino's):
  Chromium 1.05 EPYC 7763, 0.96 9V45, 1.00 9V74, 0.95 Xeon 6973P (one job), 1.00 8370C, 1.07 Neoverse-N2; Node
  1.07-1.14 on every CPU; StringSet 1.16-1.28 in both engines. msgpackr reads an array and the bench makes it a Set
  with the constructor, so in Chromium it gets the fast path on exactly this data.
