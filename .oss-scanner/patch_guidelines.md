# How we would like patches made

## Size and scope of a fix
- Fix the whole class of bug where every caller goes through (the shared reader, helper or formatter), not only the
  path the payload took. When the bug is in a rule every runtime must follow, fix it in .NET, Go, Rust and
  TypeScript and in nino-gen's templates, or say which runtimes are not affected and why.
- In .NET, a check of the data throws `NinoDataException` through a `NinoThrow` helper (out of line); check a length
  against the bytes left before allocating; count depth on recursive paths. Go returns an `error`, Rust an `Err`,
  TypeScript throws `NinoError`; none of them panics.
- Keep the default read path as fast as it was: a check only `NinoSettings.Untrusted` needs is a single `if` on the
  setting with the work out of line. Do not weaken an existing check to make a test pass.
- Never change the bytes Nino writes; a change to what is accepted follows `docs/spec/wire-format.md` §9.

## Code style
- Match the surrounding code; few comments, only where the code is not obvious. `dotnet build` must stay at 0
  warnings; `gofmt`, `rustfmt` (runtimes/rust/rustfmt.toml) and `tsc` clean.
- Fast paths stay behind `#if` or a startup check, with the shared path as fallback writing and reading the same bytes.

## Tests
- A regression test next to the closest existing one (`tests/Nino.UnitTests/Security` for .NET, asserting the exact
  type with `Assert.ThrowsExactly<NinoDataException>`; the runtime's own tests otherwise).
- When the bug is about which bytes are accepted, a malicious case in `tests/conformance/` (see `tests/conformance/AGENTS.md`), so
  every runtime is checked.

## Branch and commit message
- Against `main`. Conventional Commits: `fix(scope): summary` (scope: `core`, `generator`, `go`, `rust`, `ts`, ...),
  a body that says why, and a last paragraph `Fixed: <one sentence for users>`.
