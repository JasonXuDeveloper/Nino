# Strings outside .NET

Part of the `optimize-runtimes` skill: the Rust AVX2, AVX-512 and NEON string kernels, the scalar decoders and how
to model them with llvm-mca before spending runner time, and what was tried in TypeScript. Read it before touching
`strings_wire`, `simd.rs`, a per-char loop or a string cache.

- Keep sizing the Rust AVX2 writer's UTF-16 units by the longest char in the whole 32-byte window: sizing each
  16-byte half by its own longest char (test branch 907b0f7; a lane after a four-byte lead must then count for the
  next half too, `(three >> 16) | (four >> 14)`, or `"xxxxxxxxxxxxxx𐀀"` breaks) made text mixed/16 and /24 5.5 and
  6.6 % faster and strings-mixed-1000 2.3 % on EPYC 7763, but cyrillic/16 3.0 % slower (n=32, floor -0.3 %, runs
  40753062510 and 40756504471): the second computation costs more than it saves when both halves are alike.
- Hand the window a Latin-1 pass stopped at to the UTF-16 pass instead of loading it again. Rust AVX-512
  (`wide_avx512`, 4e5465d): strings-mixed-1000 serialize 8.8 % faster on EPYC 9V45 (n=20), 8.9 % on Xeon 8573C
  (n=14), 6.0 % on Xeon 8370C (n=9), 3.5 % on 9V74 (n=35); text of 16 chars 4-16 % faster, every class (runs
  40753062510, 40756504471, 40761692830).
- Rust Arm64 keeps scalar `short_wide_inline` for text up to 64 bytes: routing short non-ASCII text to `neon::wide`
  (test branch 0fced91, dda2c89; run 40734299173, n=15 on Neoverse-N2) made strings-mixed-1000 50 % slower, text
  mixed/16 112 %, cjk/16 75 %, cyrillic/16 18 % and latin1/16 9 %; only latin1/24 (-25 %) and cjk/24 (-12 %) gained. A
  16-byte port of `x86::wide_sse41` inlined in the list loop (`neon::wide16`, as x86's `strings_sse41`; test branch
  182d5123, round 103) was still 13.3 % slower on strings-mixed-1000 serialize on Neoverse-N2 (run 41189492851, n=10,
  floor -0.3 %). Why (llvm-mca 18, `-mcpu=neoverse-n2`, on the compiled loops): the scalar loop costs 2.5 cycles a
  Latin-1 char and 3.0-3.3 a UTF-16 one; a 16-byte window ~11-13 cycles of ASIMD throughput (two 128-bit pipes) plus a
  mask-to-offset chain (`umaxv`, `bits`' `uzp` ladder, `umov`, `ONES`) the next string's stores wait on, so a vector
  step wins only from ~32-48 chars a string. simdutf agrees: its UTF-8→UTF-16 `convert_valid` runs vectors only over
  80 bytes or more and leaves shorter input to scalar code. Before another vector attempt at short text on Arm64, model
  it against the scalar loop over the executed instruction stream (next bullet); the research is in the round's notes
  (arm-strings-research.md).
- Give each arm of a Rust per-char decoder its own end check and back edge, and read bytes as `i8` (ASCII `>= 0`, its
  unit the sign-extended byte; `& 0x1F`-style masks drop the sign bits; class bounds `-0x3C`, `-0x20`, `-0x10`). Why:
  one loop with if/else arms and a shared `p += k; if p == end` tail lets LLVM sink the increments into a phi of
  constants (`mov w, #k; add`) and turns `b < 0x80` into `ldrsb` + `and #0xff` + `tbz`: `short_wide_inline` took 9
  instructions an ASCII char and 15 a two-byte one on aarch64, now 6 and 11. Evidence (runtime A/B run 41206038132,
  Neoverse-N2, n=15, floors within 0.15 %): strings-mixed-1000 serialize -7.7 %, text write 16-24 chars -4 to -13.5 %,
  everything else on par. Modeled first, and the model ranked the candidates right: run the aarch64 build (musl,
  rust-lld) under `qemu-aarch64 -one-insn-per-tb -d nochain,exec`, map the PCs inside the function to `llvm-objdump`
  text, and give a window of that executed stream to `llvm-mca -mcpu=neoverse-n2 -iterations=1` (it then sees the
  loop-carried chains and the overlap between strings, which one loop body's block throughput does not; main
  modeled 29.5 cycles a mixed string against ~31-37 measured). It predicted -23 % for this change (7.7 % measured: a
  ceiling, not a forecast) and ruled out, before any runner time: an 8-byte ASCII shortcut sized by `trailing_zeros`
  of the high bits (simdutf's scalar shortcut made branch-free; +30 % modeled, the next char waits on load, rbit, clz
  and shift where a predicted branch makes it an add), a stack copy with a 0xFF sentinel to drop the end checks
  (+10 %, LLVM merged the tails again and added four induction variables), and Höhrmann/Wellons table decoders (a
  load on every char's chain). Scripts and numbers: round 104's notes (rust-scalar/research.md).
- Rust strings-mixed serialize trails rmp-serde on every CPU by design: the competitors copy UTF-8 with memcpy, and
  Nino's wire format is Latin-1 or UTF-16, so mixed text is transcoded. Narrow it in the kernels; do not expect to
  pass a copy.
- TypeScript: a cache of decoded map-key strings (4,096 and 16,384 slots, keyed by a hash of the bytes) made Lookup
  deserialize slower (0.90 to 1.12-1.14 ms locally): hashing bytes in JavaScript costs more than decoding them, and
  the case has 5,000 distinct keys. On Chromium, decoding short strings through a windowed `TextDecoder('latin1')`
  left ASCII unchanged and made mixed strings 30 times slower (0.045 to 1.34 ms). The bench's JSON leads on Lookup and
  ASCII strings deserialize are an artifact of parsing the same text every iteration: V8's JSON.parse reuses
  hidden-class transitions and internalized keys for repeated text (a Lookup-like text took 0.21 ms a parse repeated,
  0.25 with two texts alternating, 7.8-8.4 ms with 200 in turn; string arrays 63 against 112 µs), while building Maps
  from fresh key strings costs the same either way (0.65 against 0.67 ms). Vary the bench data before chasing those
  cases.
