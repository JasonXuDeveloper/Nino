/**
 * Values up to this size are copied into the bytes of one reader that `deserialize` reuses: a copy costs less than
 * the `DataView` a new reader makes (about 75 ns in Node 22, 50 in Chromium), which is most of the time of a small
 * value.
 */
export const scratchSize = 4096;

// ignoreBOM keeps a leading U+FEFF, which is text in Nino.
export const utf8Decoder = new TextDecoder("utf-8", { fatal: true, ignoreBOM: true });
export const utf8Encoder = new TextEncoder();

/**
 * The bytes a string header (below 2^32, wire-format spec §2.3.1) takes: 1 below 0x80 (the value itself), 3 up to
 * 0xFFFF (0x80 and a uint16), else 5 (0x81 and a uint32).
 */
export function headerSize(value: number): number {
  return value < 0x80 ? 1 : value <= 0xffff ? 3 : 5;
}
/** Browsers decode "latin1" as windows-1252, which differs from Latin-1 in 0x80-0x9F (see `decodeLatin1`). */
const latin1Decoder = new TextDecoder("latin1");
/** Replaces unpaired surrogates with U+FFFD, which .NET keeps (see `decodeUtf16`). */
export const utf16Decoder = new TextDecoder("utf-16le", { ignoreBOM: true });
/** A char wider than a byte. A string without one is one byte per char in Nino. */
export const wide = /[^\x00-\xff]/;
/** A char outside ASCII. */
export const nonAscii = /[^\x00-\x7f]/;

/** Chars per `String.fromCharCode` call, well under any engine's argument limit. */
const chunk = 4096;
/**
 * With Node's methods, text longer than this is written by a native call (`latin1Write`, `ucs2Write`, after a regex
 * for chars past U+00FF); shorter text is narrowed char by char in JavaScript, which costs less up to about 48 chars
 * on x64 and Arm64 (32 and 64 measured the same, 8 took twice as long on short text).
 */
export const shortText = 48;
/** Without Node's methods, the longest text read and written in JavaScript: TextDecoder and TextEncoder cost more. */
export const mediumText = 32;
/**
 * Without Node's methods, the longest text written in JavaScript: a `TextEncoder.encodeInto` call costs about as
 * much as writing 300 chars in Chromium.
 */
export const loopText = 256;
/**
 * With Node's methods, the longest text sliced from the reader's decoded window. V8 copies a substring this short
 * into a plain string; a longer one would be a slice of the window, slower to write or test with a regex later.
 */
export const textSlice = 12;
/** The window's largest size. */
export const textWindow = 4096;
/** The char codes of a text being read, passed to `String.fromCharCode` at once. */
export const codes: number[] = [];

/** The native Latin-1 and UTF-16 text methods of Node's `Buffer` (Bun's and Deno's too), called on a Uint8Array. */
interface NodeText {
  latin1Slice(this: Uint8Array, start: number, end: number): string;
  ucs2Slice(this: Uint8Array, start: number, end: number): string;
  latin1Write(this: Uint8Array, text: string, offset: number, length: number): number;
  ucs2Write(this: Uint8Array, text: string, offset: number, length: number): number;
}

/** `Buffer.prototype` when it reads and writes Latin-1 and UTF-16 (unpaired surrogates included) as Nino does. */
export const nodeText = ((): NodeText | null => {
  try {
    const text = (globalThis as { Buffer?: { prototype: NodeText } }).Buffer?.prototype;
    if (text === undefined) return null;
    const probe = new Uint8Array(5);
    text.latin1Write.call(probe, "\u00e9", 0, 1);
    text.ucs2Write.call(probe, "\ud800\u0101", 1, 4);
    return text.latin1Slice.call(probe, 0, 1) === "\u00e9" && text.ucs2Slice.call(probe, 1, 5) === "\ud800\u0101" &&
      probe[1] === 0x00 && probe[2] === 0xd8 ? text : null;
  } catch {
    return null;
  }
})();

/**
 * The WebAssembly kernels (wasm/text.wat, built by `npm run build:wasm`): text between JavaScript strings and the
 * bytes of Nino's string encodings, where no native method does it (browsers and Deno; Node, Bun and Deno with Node's
 * `Buffer` keep `nodeText`). They take the strings through the JS String Builtins and move their chars in SIMD
 * registers. An engine without WebAssembly, its SIMD, GC or the builtins, or a page whose CSP forbids compiling it,
 * keeps the JavaScript paths, which write and read the same bytes.
 */
export interface Kernels {
  /** The module's one page of memory: the buffer of `deserialize`'s reader, then `transfer`. */
  memory: { readonly buffer: ArrayBuffer };
  latin1(at: number, length: number): string;
  utf16(at: number, length: number): string;
  narrow(text: string, length: number, out: number): number;
  widen(length: number, out: number): void;
  /** `memory` as bytes (it never grows, so this view stays valid). */
  bytes: Uint8Array<ArrayBuffer>;
}

/** The module (wasm/text.wat), base64. Generated: `npm run build:wasm` writes it. */
const kernelModule =
  // wasm:begin
  // wasm:source sha256:8236fe8bc177570b7fe91f090e92de136a666423240cdcc8af50573fdc337719
  "AGFzbQEAAAABJwZedwFgA29jAH8Bf2ADYwB/fwFkb2ACf38Bb2ADb39/AX9gAn9/AAJHAg53YXNtOmpzLXN0cmluZxFpbnRvQ2hhckNvZGVBcnJheQABDndhc206anMtc3RyaW5nEWZyb21DaGFyQ29kZUFycmF5AAIDBQQDAwQFBQQBAQEBBgsBZAAAQYAg+wcACwcsBQZtZW1vcnkCAAZsYXRpbjEAAgV1dGYxNgADBm5hcnJvdwAEBXdpZGVuAAUKuwgE0gIDAWQAAn8BeyMAIQIgAUFwcSEEAkADQCADIARPDQEgACADav0ABAAhBSACIANBAGogBf0WAPsOACACIANBAWogBf0WAfsOACACIANBAmogBf0WAvsOACACIANBA2ogBf0WA/sOACACIANBBGogBf0WBPsOACACIANBBWogBf0WBfsOACACIANBBmogBf0WBvsOACACIANBB2ogBf0WB/sOACACIANBCGogBf0WCPsOACACIANBCWogBf0WCfsOACACIANBCmogBf0WCvsOACACIANBC2ogBf0WC/sOACACIANBDGogBf0WDPsOACACIANBDWogBf0WDfsOACACIANBDmogBf0WDvsOACACIANBD2ogBf0WD/sOACADQRBqIQMMAAsLAkADQCADIAFPDQEgAiADIAAgA2otAAD7DgAgA0EBaiEDDAALCyACQQAgARABC+ABAwFkAAJ/AXsjACECIAFBeHEhBAJAA0AgAyAETw0BIAAgA0EBdGr9AAQAIQUgAiADQQBqIAX9GQD7DgAgAiADQQFqIAX9GQH7DgAgAiADQQJqIAX9GQL7DgAgAiADQQNqIAX9GQP7DgAgAiADQQRqIAX9GQT7DgAgAiADQQVqIAX9GQX7DgAgAiADQQZqIAX9GQb7DgAgAiADQQdqIAX9GQf7DgAgA0EIaiEDDAALCwJAA0AgAyABTw0BIAIgAyAAIANBAXRqLwEA+w4AIANBAWohAwwACwsgAkEAIAEQAQuYAgMBZAADfwF7IwAhAyAAIANBABAAGiABQXhxIQUCQANAIAQgBU8NASAHIAMgBEEAavsNAP0aACEHIAcgAyAEQQFq+w0A/RoBIQcgByADIARBAmr7DQD9GgIhByAHIAMgBEEDavsNAP0aAyEHIAcgAyAEQQRq+w0A/RoEIQcgByADIARBBWr7DQD9GgUhByAHIAMgBEEGavsNAP0aBiEHIAcgAyAEQQdq+w0A/RoHIQcgB/0MAP8A/wD/AP8A/wD/AP8A//1O/VMNASACIARqIAcgB/1m/VsDAAAgBEEIaiEEDAALCwJAA0AgBCABTw0BIAMgBPsNACEGIAZB/wFLDQEgAiAEaiAGOgAAIARBAWohBAwACwsgBAvoAQMBZAACfwF7IwAhAiAAQXhxIQQCQANAIAMgBE8NASAFIAIgA0EAavsNAP0aACEFIAUgAiADQQFq+w0A/RoBIQUgBSACIANBAmr7DQD9GgIhBSAFIAIgA0EDavsNAP0aAyEFIAUgAiADQQRq+w0A/RoEIQUgBSACIANBBWr7DQD9GgUhBSAFIAIgA0EGavsNAP0aBiEFIAUgAiADQQdq+w0A/RoHIQUgASADQQF0aiAF/QsEACADQQhqIQMMAAsLAkADQCADIABPDQEgASADQQF0aiACIAP7DQA7AQAgA0EBaiEDDAALCws=";
  // wasm:end
/** The most chars a kernel takes: the size of its array of code units (test/wasm.test.ts checks text.wat's). */
export const kernelText = 4096;
/**
 * Where in the kernels' memory text is copied in and written out (`kernelText` UTF-16 code units): after the bytes of
 * `deserialize`'s reader, which start the memory.
 */
export const transfer = scratchSize;
/**
 * In Chromium the kernels read Latin-1 text longer than `mediumText` up to `kernelLatin1` chars (TextDecoder costs
 * the same for longer text), UTF-16 text longer than `kernelUtf16` (shorter: `decodeShortUtf16`), and write text
 * longer than `kernelWrite` (shorter costs less in JavaScript than a call), except ASCII longer than `kernelAscii`,
 * which `TextEncoder.encodeInto` writes faster.
 */
export const kernelLatin1 = 128, kernelUtf16 = 24, kernelWrite = 64, kernelAscii = 1024;

/** The kernels, if this engine runs them; undefined until first needed. */
let loaded: Kernels | null | undefined;
/** The kernels in use: `loaded`, unless `useWasm(false)` turned them off. */
export let kernels: Kernels | null | undefined;

/** What the kernels need of the WebAssembly API, declared here so the runtime compiles without the DOM's types. */
interface WebAssemblyApi {
  validate(bytes: Uint8Array, options: object): boolean;
  Module: new (bytes: Uint8Array, options: object) => object;
  Instance: new (module: object, imports: object) => { exports: unknown };
}

function loadKernels(): Kernels | null {
  try {
    const api = (globalThis as { WebAssembly?: WebAssemblyApi }).WebAssembly;
    if (api === undefined) return null;
    const bytes = Uint8Array.from(atob(kernelModule), (c) => c.charCodeAt(0)), options = { builtins: ["js-string"] };
    if (!api.validate(bytes, options)) return null;
    // No imports given: an engine without the builtins fails to link here.
    const exports = new api.Instance(new api.Module(bytes, options), {}).exports as Kernels;
    const k: Kernels = { ...exports, bytes: new Uint8Array(exports.memory.buffer) };
    // A check of each kernel on known text.
    const probe = "a\u00e9\u4e2d\ud800";
    if (k.narrow(probe, 4, transfer) !== 2 || k.latin1(transfer, 2) !== "a\u00e9") return null;
    k.widen(4, transfer);
    return k.utf16(transfer, 4) === probe ? k : null;
  } catch {
    return null;
  }
}

/** The kernels in use, loaded on first use. */
export function wasm(): Kernels | null {
  if (kernels === undefined) kernels = loaded = loaded === undefined ? loadKernels() : loaded;
  return kernels;
}

/**
 * Whether text is read and written with the WebAssembly kernels where they are faster than JavaScript (browsers and
 * Deno; see `Kernels`): `useWasm(false)` turns them off, `useWasm(true)` back on where the engine runs them, and
 * `useWasm()` only tells. The bytes are the same either way. Returns whether they are in use.
 */
export function useWasm(enabled?: boolean): boolean {
  if (enabled === false) kernels = null;
  else if (enabled === true) kernels = undefined;
  // With Node's methods, text never needs them.
  return nodeText === null && wasm() !== null;
}

/** The text of UTF-16 code units, unpaired surrogates included (as .NET keeps them). */
export function fromCodes(codes: ArrayLike<number>): string {
  if (codes.length <= chunk) return String.fromCharCode.apply(null, codes as number[]);
  let text = "";
  for (let i = 0; i < codes.length; i += chunk)
    text += String.fromCharCode.apply(null, Array.prototype.slice.call(codes, i, i + chunk) as number[]);
  return text;
}

/** The text of up to 16 Latin-1 bytes at `at`, in one call (a flat string, faster than any native decoder). */
function decodeShortLatin1(d: Uint8Array, at: number, length: number): string {
  const c = String.fromCharCode;
  switch (length) {
    case 0: return "";
    case 1: return c(d[at]);
    case 2: return c(d[at], d[at + 1]);
    case 3: return c(d[at], d[at + 1], d[at + 2]);
    case 4: return c(d[at], d[at + 1], d[at + 2], d[at + 3]);
    case 5: return c(d[at], d[at + 1], d[at + 2], d[at + 3], d[at + 4]);
    case 6: return c(d[at], d[at + 1], d[at + 2], d[at + 3], d[at + 4], d[at + 5]);
    case 7: return c(d[at], d[at + 1], d[at + 2], d[at + 3], d[at + 4], d[at + 5], d[at + 6]);
    case 8: return c(d[at], d[at + 1], d[at + 2], d[at + 3], d[at + 4], d[at + 5], d[at + 6], d[at + 7]);
    case 9: return c(d[at], d[at + 1], d[at + 2], d[at + 3], d[at + 4], d[at + 5], d[at + 6], d[at + 7], d[at + 8]);
    case 10:
      return c(d[at], d[at + 1], d[at + 2], d[at + 3], d[at + 4], d[at + 5], d[at + 6], d[at + 7], d[at + 8], d[at + 9]);
    case 11:
      return c(d[at], d[at + 1], d[at + 2], d[at + 3], d[at + 4], d[at + 5], d[at + 6], d[at + 7], d[at + 8], d[at + 9],
        d[at + 10]);
    case 12:
      return c(d[at], d[at + 1], d[at + 2], d[at + 3], d[at + 4], d[at + 5], d[at + 6], d[at + 7], d[at + 8], d[at + 9],
        d[at + 10], d[at + 11]);
    case 13:
      return c(d[at], d[at + 1], d[at + 2], d[at + 3], d[at + 4], d[at + 5], d[at + 6], d[at + 7], d[at + 8], d[at + 9],
        d[at + 10], d[at + 11], d[at + 12]);
    case 14:
      return c(d[at], d[at + 1], d[at + 2], d[at + 3], d[at + 4], d[at + 5], d[at + 6], d[at + 7], d[at + 8], d[at + 9],
        d[at + 10], d[at + 11], d[at + 12], d[at + 13]);
    case 15:
      return c(d[at], d[at + 1], d[at + 2], d[at + 3], d[at + 4], d[at + 5], d[at + 6], d[at + 7], d[at + 8], d[at + 9],
        d[at + 10], d[at + 11], d[at + 12], d[at + 13], d[at + 14]);
    default:
      return c(d[at], d[at + 1], d[at + 2], d[at + 3], d[at + 4], d[at + 5], d[at + 6], d[at + 7], d[at + 8], d[at + 9],
        d[at + 10], d[at + 11], d[at + 12], d[at + 13], d[at + 14], d[at + 15]);
  }
}

/** The text of `length` Latin-1 bytes at `at`. */
export function decodeLatin1(data: Uint8Array, at: number, length: number): string {
  if (length <= 16) return decodeShortLatin1(data, at, length);
  if (nodeText !== null) return nodeText.latin1Slice.call(data, at, at + length);
  if (length <= mediumText) {
    // In one call: concatenating the text would leave a string to flatten on first use.
    codes.length = length;
    for (let i = 0; i < length; i++) codes[i] = data[at + i];
    return String.fromCharCode.apply(null, codes);
  }
  const bytes = new Uint8Array(data.buffer, data.byteOffset + at, length), text = latin1Decoder.decode(bytes);
  // windows-1252 reads most of 0x80-0x9F as wider chars; those are read one by one instead.
  return wide.test(text) ? fromCodes(bytes) : text;
}

/**
 * The text of up to 24 UTF-16 code units at `at`, in one call: flat, unpaired surrogates kept, and in Chromium and
 * Node cheaper than a native decoder or an array of codes. Each code unit is one DataView read, inline: a helper per
 * unit was past V8's inlining budget in a function this large, and stayed a call.
 */
export function decodeShortUtf16(v: DataView, at: number, length: number): string {
  const c = String.fromCharCode;
  switch (length) {
    case 0: return "";
    case 1: return c(v.getUint16(at, true));
    case 2: return c(v.getUint16(at, true), v.getUint16(at + 2, true));
    case 3: return c(v.getUint16(at, true), v.getUint16(at + 2, true), v.getUint16(at + 4, true));
    case 4:
      return c(v.getUint16(at, true), v.getUint16(at + 2, true), v.getUint16(at + 4, true), v.getUint16(at + 6, true));
    case 5:
      return c(v.getUint16(at, true), v.getUint16(at + 2, true), v.getUint16(at + 4, true), v.getUint16(at + 6, true),
        v.getUint16(at + 8, true));
    case 6:
      return c(v.getUint16(at, true), v.getUint16(at + 2, true), v.getUint16(at + 4, true), v.getUint16(at + 6, true),
        v.getUint16(at + 8, true), v.getUint16(at + 10, true));
    case 7:
      return c(v.getUint16(at, true), v.getUint16(at + 2, true), v.getUint16(at + 4, true), v.getUint16(at + 6, true),
        v.getUint16(at + 8, true), v.getUint16(at + 10, true), v.getUint16(at + 12, true));
    case 8:
      return c(v.getUint16(at, true), v.getUint16(at + 2, true), v.getUint16(at + 4, true), v.getUint16(at + 6, true),
        v.getUint16(at + 8, true), v.getUint16(at + 10, true), v.getUint16(at + 12, true), v.getUint16(at + 14, true));
    case 9:
      return c(v.getUint16(at, true), v.getUint16(at + 2, true), v.getUint16(at + 4, true), v.getUint16(at + 6, true),
        v.getUint16(at + 8, true), v.getUint16(at + 10, true), v.getUint16(at + 12, true), v.getUint16(at + 14, true),
        v.getUint16(at + 16, true));
    case 10:
      return c(v.getUint16(at, true), v.getUint16(at + 2, true), v.getUint16(at + 4, true), v.getUint16(at + 6, true),
        v.getUint16(at + 8, true), v.getUint16(at + 10, true), v.getUint16(at + 12, true), v.getUint16(at + 14, true),
        v.getUint16(at + 16, true), v.getUint16(at + 18, true));
    case 11:
      return c(v.getUint16(at, true), v.getUint16(at + 2, true), v.getUint16(at + 4, true), v.getUint16(at + 6, true),
        v.getUint16(at + 8, true), v.getUint16(at + 10, true), v.getUint16(at + 12, true), v.getUint16(at + 14, true),
        v.getUint16(at + 16, true), v.getUint16(at + 18, true), v.getUint16(at + 20, true));
    case 12:
      return c(v.getUint16(at, true), v.getUint16(at + 2, true), v.getUint16(at + 4, true), v.getUint16(at + 6, true),
        v.getUint16(at + 8, true), v.getUint16(at + 10, true), v.getUint16(at + 12, true), v.getUint16(at + 14, true),
        v.getUint16(at + 16, true), v.getUint16(at + 18, true), v.getUint16(at + 20, true), v.getUint16(at + 22, true));
    case 13:
      return c(v.getUint16(at, true), v.getUint16(at + 2, true), v.getUint16(at + 4, true), v.getUint16(at + 6, true),
        v.getUint16(at + 8, true), v.getUint16(at + 10, true), v.getUint16(at + 12, true), v.getUint16(at + 14, true),
        v.getUint16(at + 16, true), v.getUint16(at + 18, true), v.getUint16(at + 20, true), v.getUint16(at + 22, true),
        v.getUint16(at + 24, true));
    case 14:
      return c(v.getUint16(at, true), v.getUint16(at + 2, true), v.getUint16(at + 4, true), v.getUint16(at + 6, true),
        v.getUint16(at + 8, true), v.getUint16(at + 10, true), v.getUint16(at + 12, true), v.getUint16(at + 14, true),
        v.getUint16(at + 16, true), v.getUint16(at + 18, true), v.getUint16(at + 20, true), v.getUint16(at + 22, true),
        v.getUint16(at + 24, true), v.getUint16(at + 26, true));
    case 15:
      return c(v.getUint16(at, true), v.getUint16(at + 2, true), v.getUint16(at + 4, true), v.getUint16(at + 6, true),
        v.getUint16(at + 8, true), v.getUint16(at + 10, true), v.getUint16(at + 12, true), v.getUint16(at + 14, true),
        v.getUint16(at + 16, true), v.getUint16(at + 18, true), v.getUint16(at + 20, true), v.getUint16(at + 22, true),
        v.getUint16(at + 24, true), v.getUint16(at + 26, true), v.getUint16(at + 28, true));
    case 16:
      return c(v.getUint16(at, true), v.getUint16(at + 2, true), v.getUint16(at + 4, true), v.getUint16(at + 6, true),
        v.getUint16(at + 8, true), v.getUint16(at + 10, true), v.getUint16(at + 12, true), v.getUint16(at + 14, true),
        v.getUint16(at + 16, true), v.getUint16(at + 18, true), v.getUint16(at + 20, true), v.getUint16(at + 22, true),
        v.getUint16(at + 24, true), v.getUint16(at + 26, true), v.getUint16(at + 28, true), v.getUint16(at + 30, true));
    case 17:
      return c(v.getUint16(at, true), v.getUint16(at + 2, true), v.getUint16(at + 4, true), v.getUint16(at + 6, true),
        v.getUint16(at + 8, true), v.getUint16(at + 10, true), v.getUint16(at + 12, true), v.getUint16(at + 14, true),
        v.getUint16(at + 16, true), v.getUint16(at + 18, true), v.getUint16(at + 20, true), v.getUint16(at + 22, true),
        v.getUint16(at + 24, true), v.getUint16(at + 26, true), v.getUint16(at + 28, true), v.getUint16(at + 30, true),
        v.getUint16(at + 32, true));
    case 18:
      return c(v.getUint16(at, true), v.getUint16(at + 2, true), v.getUint16(at + 4, true), v.getUint16(at + 6, true),
        v.getUint16(at + 8, true), v.getUint16(at + 10, true), v.getUint16(at + 12, true), v.getUint16(at + 14, true),
        v.getUint16(at + 16, true), v.getUint16(at + 18, true), v.getUint16(at + 20, true), v.getUint16(at + 22, true),
        v.getUint16(at + 24, true), v.getUint16(at + 26, true), v.getUint16(at + 28, true), v.getUint16(at + 30, true),
        v.getUint16(at + 32, true), v.getUint16(at + 34, true));
    case 19:
      return c(v.getUint16(at, true), v.getUint16(at + 2, true), v.getUint16(at + 4, true), v.getUint16(at + 6, true),
        v.getUint16(at + 8, true), v.getUint16(at + 10, true), v.getUint16(at + 12, true), v.getUint16(at + 14, true),
        v.getUint16(at + 16, true), v.getUint16(at + 18, true), v.getUint16(at + 20, true), v.getUint16(at + 22, true),
        v.getUint16(at + 24, true), v.getUint16(at + 26, true), v.getUint16(at + 28, true), v.getUint16(at + 30, true),
        v.getUint16(at + 32, true), v.getUint16(at + 34, true), v.getUint16(at + 36, true));
    case 20:
      return c(v.getUint16(at, true), v.getUint16(at + 2, true), v.getUint16(at + 4, true), v.getUint16(at + 6, true),
        v.getUint16(at + 8, true), v.getUint16(at + 10, true), v.getUint16(at + 12, true), v.getUint16(at + 14, true),
        v.getUint16(at + 16, true), v.getUint16(at + 18, true), v.getUint16(at + 20, true), v.getUint16(at + 22, true),
        v.getUint16(at + 24, true), v.getUint16(at + 26, true), v.getUint16(at + 28, true), v.getUint16(at + 30, true),
        v.getUint16(at + 32, true), v.getUint16(at + 34, true), v.getUint16(at + 36, true), v.getUint16(at + 38, true));
    case 21:
      return c(v.getUint16(at, true), v.getUint16(at + 2, true), v.getUint16(at + 4, true), v.getUint16(at + 6, true),
        v.getUint16(at + 8, true), v.getUint16(at + 10, true), v.getUint16(at + 12, true), v.getUint16(at + 14, true),
        v.getUint16(at + 16, true), v.getUint16(at + 18, true), v.getUint16(at + 20, true), v.getUint16(at + 22, true),
        v.getUint16(at + 24, true), v.getUint16(at + 26, true), v.getUint16(at + 28, true), v.getUint16(at + 30, true),
        v.getUint16(at + 32, true), v.getUint16(at + 34, true), v.getUint16(at + 36, true), v.getUint16(at + 38, true),
        v.getUint16(at + 40, true));
    case 22:
      return c(v.getUint16(at, true), v.getUint16(at + 2, true), v.getUint16(at + 4, true), v.getUint16(at + 6, true),
        v.getUint16(at + 8, true), v.getUint16(at + 10, true), v.getUint16(at + 12, true), v.getUint16(at + 14, true),
        v.getUint16(at + 16, true), v.getUint16(at + 18, true), v.getUint16(at + 20, true), v.getUint16(at + 22, true),
        v.getUint16(at + 24, true), v.getUint16(at + 26, true), v.getUint16(at + 28, true), v.getUint16(at + 30, true),
        v.getUint16(at + 32, true), v.getUint16(at + 34, true), v.getUint16(at + 36, true), v.getUint16(at + 38, true),
        v.getUint16(at + 40, true), v.getUint16(at + 42, true));
    case 23:
      return c(v.getUint16(at, true), v.getUint16(at + 2, true), v.getUint16(at + 4, true), v.getUint16(at + 6, true),
        v.getUint16(at + 8, true), v.getUint16(at + 10, true), v.getUint16(at + 12, true), v.getUint16(at + 14, true),
        v.getUint16(at + 16, true), v.getUint16(at + 18, true), v.getUint16(at + 20, true), v.getUint16(at + 22, true),
        v.getUint16(at + 24, true), v.getUint16(at + 26, true), v.getUint16(at + 28, true), v.getUint16(at + 30, true),
        v.getUint16(at + 32, true), v.getUint16(at + 34, true), v.getUint16(at + 36, true), v.getUint16(at + 38, true),
        v.getUint16(at + 40, true), v.getUint16(at + 42, true), v.getUint16(at + 44, true));
    default:
      return c(v.getUint16(at, true), v.getUint16(at + 2, true), v.getUint16(at + 4, true), v.getUint16(at + 6, true),
        v.getUint16(at + 8, true), v.getUint16(at + 10, true), v.getUint16(at + 12, true), v.getUint16(at + 14, true),
        v.getUint16(at + 16, true), v.getUint16(at + 18, true), v.getUint16(at + 20, true), v.getUint16(at + 22, true),
        v.getUint16(at + 24, true), v.getUint16(at + 26, true), v.getUint16(at + 28, true), v.getUint16(at + 30, true),
        v.getUint16(at + 32, true), v.getUint16(at + 34, true), v.getUint16(at + 36, true), v.getUint16(at + 38, true),
        v.getUint16(at + 40, true), v.getUint16(at + 42, true), v.getUint16(at + 44, true), v.getUint16(at + 46, true));
  }
}
