# How we would like reports written

## Layout and length
- Under one page, in English, with these sections in this order: Summary, Affected, Reproduction, Root cause,
  Suggested fix.
- One root cause per report. When the same root cause shows in several runtimes, one report that lists each.

## What every report should state
- Affected: the runtime (.NET with its target framework and runtime: CoreCLR, Mono, NativeAOT; Go; Rust; TypeScript;
  code written by nino-gen), the commit, and the mode (.NET default mode or `NinoSettings.Untrusted`, plus any other
  setting that differs from the defaults). Say whether a vector path is needed (`DOTNET_EnableHWIntrinsic=0`
  makes it go away, or not).
- The attacker's position: which bytes they control and through which public API they reach the faulty code
  (`NinoDeserializer.Deserialize<T>` on a `T` declared how, the ASP.NET Core input formatter, deserializing into an
  existing instance, Go `Unmarshal`, Rust `nino::deserialize`, ...).
- Reproduction: the payload as hex, the type declarations it needs, and a minimal test that fails before the fix in
  the style of the nearest existing test (`tests/Nino.UnitTests/Security`, `runtimes/*/` tests). Say what was observed
  (the exception type and message, the crash, the time or bytes allocated) and what the contract expects.
- Whether the same bytes behave differently in another runtime, when checked.

## Style
- Plain Markdown, no tables. Start the title with the component: `Nino.Core: ...`, `Go runtime: ...`,
  `Rust runtime: ...`, `TypeScript runtime: ...`, `Generator: ...`.
- Severity per `threat_model.md`, with one sentence on why.
