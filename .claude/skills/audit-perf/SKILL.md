---
name: audit-perf
description: "Find the next performance win in Nino (.NET, Unity/Burst, Go, Rust, TypeScript) - gaps against MemoryPack, MessagePack, protobuf-net and each language's competitors per CPU, work that is not needed, SIMD beyond strings, and the plan to prove each; measuring is benchmark, the techniques and the diagnosis of a case that got slower than main are optimize and optimize-runtimes. Use for a performance round, when Nino trails a competitor in a case, or when asked to optimize further. Triggers: performance round, gap, MemoryPack, MessagePack, protobuf-net, rmp-serde, msgpackr, competitor, trails, beat, lead every case, 性能, 反超, 领先, 差距, 输给, 还能优化吗, 还能更快吗."
metadata:
  internal: true
---

# Performance

Performance is a requirement on every platform, and every number that counts comes from a GitHub runner. This skill
owns the search; measuring is `benchmark`, the code-level techniques are `optimize` (.NET, Unity) and
`optimize-runtimes` (Go, Rust, TypeScript), each runtime's rules are in
`runtimes/*/AGENTS.md`, and a finding is built by the skill that owns its files.

## Where wins come from

1. **Gaps**: every case where Nino is not clearly ahead of MemoryPack (and of MessagePack and protobuf-net where they
   can express the case) on x64 and Arm64; for Go, Rust and TypeScript, the competitors in each `bench` module. List
   them per CPU from the latest release's assets (`dotnet-x64.md`, `dotnet-arm64.md`, `runtime-*.md`).
2. **Regressions against the previous main** (a release, or the commit before a wire-format change): nothing gets
   slower than main beyond the floor, and a change that should help must beat it. Diagnosing one case that got slower
   is `optimize` (.NET, Unity: compare the listings first) or `optimize-runtimes`; this skill collects them for the
   round's list.
3. **Work that is not needed**: a length checked twice, a copy where a span would do, a per-element call where a block
   copy or a vector loop would do, a check that could be a `static readonly` field, a generic lookup on a hot path.
4. **SIMD beyond strings**: blocks of primitives, bools (validation), fixed-size structs, transcoding, hash table
   fills; every SIMD path has its Arm64 and no-SIMD forms (`platforms`).
5. **The generator**: code it could emit faster for a shape users write (`audit-generated-code`), and its own speed
   per keystroke (`audit-generator`).

## Method

1. **Read first.** What does the code do now, what would change, which benchmark shows it, what did earlier attempts
   find (commit messages of reverted tries)? Look up the technique in `optimize` or
   `optimize-runtimes` before inventing one.
2. **Choose locally, decide on runners**, as `benchmark` says: local runs only guide, the A/B on runners per CPU
   against the floor decides, and only a net win ships.
3. **Record what you learned** in `optimize` or `optimize-runtimes` (the technique, faster or slower, with the runner numbers).

## Report

A table: idea | where it comes from | what Nino does now | expected gain | benchmark | runner result per CPU | verdict
(land / drop / later), plus the cases still not ahead and why.
