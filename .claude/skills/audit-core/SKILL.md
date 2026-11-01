---
name: audit-core
description: "Audit Nino's runtime libraries (src/Nino.Core, Nino.Immutable, Nino.AspNetCore, Nino.UnityTypes) for correctness and design - redundant mechanisms, duplicated logic, coupling, API shapes that differ for one act, lifecycle bugs (pooling, reuse, recycling, unloading), thread safety of registries and caches, unbounded memory, platform fallbacks - and fix what is proven. Use for a core review round, before a release, or when a bug shows up in serialization, deserialization into existing instances, pooling or registration. Triggers: src/Nino.Core, NinoSerializer, NinoDeserializer, Reader, Writer, NinoSettings, formatters, registries, pools, NinoPooled, reuse, thread safety, races, leaks, 核心, 运行时, 冗余, 线程安全, 正确性."
metadata:
  internal: true
---

# Core audit

AGENTS.md's principles apply; speed is `optimize`, hostile input `audit-security`, runtime internals
`runtime-internals`. A fix is done when `feature`'s checklist is. The map of each project is its `AGENTS.md`.

## Look for

- **Redundancy**: two mechanisms for one job (two registries, two ways to resolve a formatter, two buffer
  abstractions); public overloads that duplicate each other; options nobody needs.
- **The code standard** (`audit-code`: DRY, decoupled, clear), here in its Nino.Core forms: the same loop or
  bookkeeping per collection kind, per TFM or per formatter family; a generic method with a body of its own per T
  (one core with thin typed wrappers; families from `tools/CodeGen`).
- **Not uniform**: features whose APIs have different shapes for the same act (a `TryX` here, a nullable there; a
  span overload for one direction and not the other), or internals that solve one problem two ways.
- **One contract per fact, kept apart where the JIT needs it** (round 101, owner-approved): "the size is known
  before writing" is `INinoFixedSizeFormatter` (`FixedSize`, a JIT-time constant, -1 for a block formatter whose
  values vary; `WriteFixed(in T value, ref byte destination)`), which `INinoBlockFormatter` extends with `BlockMap`
  and `ReadBlock(ref T value, ref byte source)`: a block struct is a fixed-size type, one write for both. Two stay
  apart on purpose: `INinoRawBodyFormatter` (`BodySize`, `WriteRawBody(T value, ref byte, int offset)`) writes a
  non-sealed class's body without its type id, into a reservation the caller shares with that id (not
  `Serialize`'s bytes, so not `FixedSize`), with a constant offset folded into every store; and the internal
  `INinoSizedFormatter` (`Size(value)`, arrays and lists of blocks) is a size per value, not a constant, and
  internal, so generated code never implements it. Members put the value first, then the memory
  (`Serialize(value, ref writer)`, `WriteFixed(in value, ref destination)`, `ReadBlock(ref value, ref source)`).
- **Coupling**: Nino.Core depending on an optional package's concerns; formatters reaching into each other's
  internals instead of a shared helper; `#if` blocks that grew into separate implementations.
- **Lifecycle**: deserializing into existing instances (reuse by index and by key, strings kept when unchanged,
  re-created types), pooled instances (`[NinoPooled]`, recyclers, double return), reference tracking, collectible
  assemblies (registry entries that keep an unloaded assembly alive).
- **Threads**: registries and caches are safe to read while another thread registers (copy-on-write or locks, with
  the contract written next to the code); per-thread state does not leak between calls; statics initialize once.
- **Exceptions**: data errors are `NinoDataException` (`audit-security`), caller mistakes say which call and why.
- **Platforms**: every TFM (netstandard2.0, 2.1, net8.0, net10.0) builds the same behavior; Unity and IL2CPP
  constraints (`platforms`); trim and AOT annotations on every reflection path.
- **Unbounded memory**: pools, caches and registries with a cap or a release.
- **Dead code**: members, overloads and helpers nothing uses (`audit-code` owns the cross-project sweep).

- The code standard (`audit-code`: DRY, decoupled, clear) applies to these files, and redundancy in them
  (duplicated logic, dead code, two ways to do one job) is part of this audit; the sweep across layers is
  `audit-redundancy`.

## Fixing

- Every bug gets a regression test that fails before the fix.
- A hot-path change ships with the machine code checked and runner numbers (`optimize`, `benchmark`).
- A public API change: XML docs, the docs to-dos (`audit-docs`), the public skill, a changelog trailer; a removal or
  break is the owner's call.
