---
name: audit-security
description: "Audit and extend Nino's defenses against hostile input in every runtime (.NET, Go, Rust, TypeScript and the code nino-gen writes) - NinoDataException through NinoThrow, lengths checked before allocating, depth limits, canonical bytes only, hash-flooding limits, the Untrusted settings - and run the fuzzers, hostile-input tests and the malicious corpus. Use for any change to a read path, a new type or collection, a new setting, or a security review. Triggers: NinoDataException, NinoThrow, NinoLimits, TryReadLength, MaxDepth, MaxHashCollisions, Untrusted, EnterStruct, HashTables, tests/Nino.Fuzz, malicious corpus, StrictReadTests, crash, OOM, DoS, untrusted data, 安全, 恶意数据, 不可信输入, 模糊测试."
metadata:
  internal: true
---

# Security (hostile input)

Nino reads untrusted data (network messages, files, request bodies): crafted bytes must never crash the process, read
outside the input, allocate out of proportion to it, recurse without a bound or take time out of proportion to it.
These rules hold in .NET; each runtime's own form of them is in `runtimes/*/AGENTS.md`, and generated code follows
the runtime it targets.

## Rules

- Every check of the data throws `NinoDataException`, through a `NinoThrow` helper (out of line). The caller's
  mistakes and the write side (a graph nested too deep or holding itself, a collection changed while written) keep
  `InvalidOperationException`. Tests assert the exact type (`Assert.ThrowsExactly`).
- Check a length against the bytes left (`TryReadLength`, `NinoLimits.TooLong`) before allocating; grow, never
  presize, what an element larger in memory than on the wire claims. Every recursive path counts depth: class bodies
  through `Depth`, structs and collections that can hold themselves through `EnterStruct`/`ExitStruct` on both the
  reader and the writer, against `NinoSettings.MaxDepth`.
- Reject bytes no writer writes: a `bool` or presence byte other than 0 or 1 (`ReadBool`, `ReadPresent`,
  `NinoChecks.Bools`), keyed members that do not fill their entry (`EndMember`), sections of deferred bodies that do
  not match their region, headers that are not minimal. A raw block copy of a type with a `bool` in it goes through
  `NinoLayout<T>.CopiesAsIs`, never `IsCanonical` alone.
- A hash table Nino fills counts the keys in each bucket against `NinoSettings.MaxHashCollisions` (`HashTables`:
  `TryFill`, `Insert`, or `Added`/`Walk` after the table's own add, as `HashTables.Plan` says; `ConcurrentTables` for
  `ConcurrentDictionary`). A new path that adds keys from the data to a hash table does one of these.
- `NinoSettings.Untrusted` holds the checks that cost time on every read (seeded comparers, value checks of raw
  structs, trailing bytes). It is a constant once frozen (`NinoLimits.Untrusted`): keep each test of it a single
  `if` on the field, with the work out of line, so the default path compiles to what it was; prove it with
  `benchmarks/asm.py compare` (`optimize`).
- A rule the other runtimes must follow goes into `docs/spec/wire-format.md` §9, with malicious cases in the corpus
  (`wire-format`).

## Look for

- A new array allocated uninitialized (`NinoUnsafe.NewArray`, `CopiedWhole`) stored in the caller's slot before a
  read that can still reject the data (bools, has-value bytes, Untrusted checks): reading into an existing value
  then left it stale heap memory. Blocks with a bool (`NinoBlock<T, TFormatter>.Rejects`) are read by
  `NinoBlock.ReadRejecting`, a try/finally that clears the values when the read throws; the raw formatters (T?[],
  List<T?>, bool lists, Untrusted checks) store a new array once filled (`NinoElements.NewOrSame`, `Publish`);
  `UntrustedInputTests.ARejectedReadKeepsNewArraysOutOfTheSlot` covers each, over 0xCD-filled freed memory. A new
  read of that kind gets a member in the fuzzer's `Stale` type (below).
- A read that allocates from a count before checking it against the bytes left (collections, strings, multi-dim
  arrays, `BigInteger`, `BitArray`, `StringBuilder`, custom formatters' helpers).
- A hash table filled from the data whose hash the data can predict. In other runtimes the language's own table may
  be unseeded: V8 hashes number and `bigint` keys of `Map`/`Set` without a seed (8,000 crafted ints took 40 times as
  long as ordinary ones in Node 22), which `NinoKeyGuard` in `nino.ts` bounds by counting the buckets of the table V8 ends with; it is exact for
  V8 only (JavaScriptCore and SpiderMonkey hash their own way). A new collection of number or `bigint` keys in
  generated TypeScript takes the guard (`TsShape.Guard`); Go and Rust tables are seeded (maphash, `RandomState`).
- A recursive path without depth counting (a struct holding a collection of itself, polymorphic slots, tuples,
  `object` members, reference tracking's back references).
- A loop whose work grows faster than the input (nested lengths, quadratic rehashing, deferred sections re-read).
- A new built-in type or collection without its malicious cases and fuzz shapes.
- An exception other than `NinoDataException` escaping a read (`IndexOutOfRange`, `Overflow`, `OutOfMemory`,
  `ArgumentException` from a BCL constructor fed raw data).
- Settings that weaken a check by default.

## Proof

- `tests/Nino.Fuzz` (`tests/Nino.Fuzz/README.md`): `selftest`, then a timed run with and without
  `DOTNET_EnableHWIntrinsic=0`; a finding becomes a regression test and, when it is about the bytes, a malicious case.
- Catch a bug whose symptom is a valid value with an invariant that makes it invalid: poison what the bug would leak,
  then look for the poison. Stale heap memory left by a rejected read reads as ordinary bools and ints, so the
  fuzzer's plain reuse reads never saw fd9d92f0 / 1f4cdc21; its `Poisoned` targets (`Stale`, `StaleUntrusted`: every
  input read into a value of one-element arrays and null lists) read each rejected input again after filling freed
  memory with an 8-byte pattern tied to the address (`StaleMemory.Poison`), and report `STALE` when an unmanaged
  array the value reaches, private fields included, holds 16 bytes or more of a 64-byte line of it. Make such arrays
  large-object-heap sized (11,000 elements): freed memory there is reused as it is, and with both fixes undone
  every member was found within seconds; the gen0 allocator clears 8 KB ahead, so 8 KB lists were never found in
  120 s and 16 KB ones once in 800 rejected inputs. The selftest plants one (`SelfTestStale`).
- The hostile-input unit tests: `StrictReadTests`, `HashFloodingTests`, `UntrustedModeTests`, `SettingsTests`, the
  nesting tests (`tests/Nino.UnitTests/Security`).
- The conformance corpus in .NET, Go, Rust and TypeScript; the runtimes' own fuzzers (`FuzzReader`, `FuzzDecode`,
  `FuzzTagged`, `FuzzTaggedDecode`; Rust `cargo fuzz` and the corpus fuzz tests; the TypeScript suite).
- A security finding from a reviewer stays open until the owner decides; a weakened check is never the fix for a
  failing test.
