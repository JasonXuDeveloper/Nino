// Nino runtime for TypeScript (5.7 or later, for the typed arrays' buffer types), written by nino-gen (Nino.Codegen) next to the generated types: reads and writes the bytes
// Nino writes in .NET. Little-endian and unaligned; lengths are int32, -1 = null; string headers take 1, 3 or 5 bytes. Do not edit: it is overwritten on
// every build.

/**
 * The Nino version of this runtime: nino-gen's for the copy it writes next to the generated types (which expect the
 * same one), the package's for the npm package (scripts/version.mjs).
 */
export const ninoVersion = "__NINO_VERSION__";

/** Reads and writes one type's values. Generated for every [NinoType]. */
export interface NinoCodec<T> {
  read(reader: NinoReader): T;
  write(writer: NinoWriter, value: T): void;
}

let shared: NinoWriter | null = null;
/** The smallest buffer `serialize` writes its results into (see `NinoWriter.next`). */
const slabSize = 64 * 1024;

/**
 * Serializes `value` with `codec` (what `NinoSerializer.Serialize<T>` writes in .NET) into a new array. Results are
 * written one after another into a shared buffer and returned as views of it, never copied or overwritten (as
 * msgpackr does, and as Node's `Buffer`s share a pool): a new `ArrayBuffer` for every result costs more than most
 * serializations. Read a result by its `byteOffset` and `length`, not through `.buffer`.
 */
export function serialize<T>(codec: NinoCodec<T>, value: T): Uint8Array<ArrayBuffer> {
  // A codec that serializes (a custom one) while this runs gets a writer of its own.
  const writer = shared ?? new NinoWriter();
  shared = null;
  try {
    writer.next();
    codec.write(writer, value);
    return writer.detach();
  } finally {
    if (writer.capacity <= 1 << 20) shared = writer;
  }
}

/**
 * Deserializes one value written by `NinoSerializer.Serialize<T>` (or `serialize`). Nothing it returns refers to
 * `data`, which it does not change. Bytes after the value are not read (see `deserializeExact`).
 */
export function deserialize<T>(codec: NinoCodec<T>, data: Uint8Array): T {
  return readValue(codec, data, false);
}

/** `deserialize` that also throws a `NinoError` when bytes follow the value. */
export function deserializeExact<T>(codec: NinoCodec<T>, data: Uint8Array): T {
  return readValue(codec, data, true);
}

function readValue<T>(codec: NinoCodec<T>, data: Uint8Array, exact: boolean): T {
  let reader = scratchReader;
  if (reader === null || data.length > scratchSize) {
    if (reader !== null || scratchMade || data.length > scratchSize) {
      return readAll(codec, new NinoReader(data), exact ? data.length : -1);
    }
    reader = makeScratch();
  }
  // A codec that deserializes (a custom one) while this runs gets a reader of its own.
  scratchReader = null;
  try {
    reader["load"](data);
    return readAll(codec, reader, exact ? data.length : -1);
  } finally {
    scratchReader = reader;
  }
}

/** One value from `reader`, which must then be at `end` (the length of the data), unless `end` is -1. */
function readAll<T>(codec: NinoCodec<T>, reader: NinoReader, end: number): T {
  const value = codec.read(reader);
  if (end >= 0 && reader.pos !== end) throw new NinoError(`${end - reader.pos} bytes after the value.`);
  return value;
}

/**
 * Values up to this size are copied into the bytes of one reader that `deserialize` reuses: a copy costs less than
 * the `DataView` a new reader makes (about 75 ns in Node 22, 50 in Chromium), which is most of the time of a small
 * value.
 */
const scratchSize = 4096;
/** The reader `deserialize` reuses: null while it is in use, or before the first call made it. */
let scratchReader: NinoReader | null = null;
let scratchMade = false;

/** The reader `deserialize` reuses: its bytes are in the kernels' memory when they are used, so text needs no copy. */
function makeScratch(): NinoReader {
  scratchMade = true;
  const k = nodeText === null ? wasm() : null;
  return new NinoReader(k !== null ? new Uint8Array(k.memory.buffer, 0, scratchSize) : new Uint8Array(scratchSize));
}

/**
 * A typed array of a fixed-size number type: how generated code holds an array or list of `sbyte`, `byte`, `short`,
 * `ushort`, `int`, `uint`, `long`, `ulong`, `float` or `double` (`NinoReader.numbers`, `NinoWriter.numbers`).
 */
export type NinoNumbers = Int8Array | Uint8Array | Int16Array | Uint16Array | Int32Array | Uint32Array |
  BigInt64Array | BigUint64Array | Float32Array | Float64Array;

/** The constructor of a `NinoNumbers` type (`Int32Array`, ...). */
export interface NinoNumbersType<T extends NinoNumbers> {
  readonly BYTES_PER_ELEMENT: number;
  new (length: number): T;
  new (buffer: ArrayBuffer, byteOffset: number, length: number): T;
}

/** Whether this host stores numbers little-endian, as Nino writes them: typed arrays are then copied as bytes. */
const littleEndian = new Uint8Array(new Uint16Array([1]).buffer)[0] === 1;

/** Reverses the bytes of each `size`-byte element in `bytes` from `at` to `end`: Nino's order and a big-endian host's. */
function swapBytes(bytes: Uint8Array, at: number, end: number, size: number): void {
  for (; at < end; at += size) {
    for (let i = at, j = at + size - 1; i < j; i++, j--) {
      const b = bytes[i];
      bytes[i] = bytes[j];
      bytes[j] = b;
    }
  }
}

/**
 * Typed arrays `NinoReader.numbers` reads up to this many bytes are views of a shared buffer (numbersSlab bytes), as
 * Node's small `Buffer`s share a pool: one over 64 bytes has its memory outside V8's heap, and allocating that costs
 * more than the copy (in Node 22 about 2 µs for 1,000 int32s against 0.6 µs for a pool view). Generated code reads
 * those of up to 64 bytes one element at a time into a typed array of their own, which V8 keeps on its heap: viewing
 * such an array's bytes (`.buffer`) allocates outside memory for it, which made reading 4 int64s 5 times slower than
 * reading them into an array (test branch 97deeca).
 */
const numbersPooled = 8192;
const numbersSlab = 64 * 1024;
let numbersBuffer = new ArrayBuffer(0);
let numbersBytes = new Uint8Array(0);
let numbersPos = 0;

/** Where `bytes` bytes start in the shared buffer (8-byte aligned, as a view must be aligned to its elements). */
function poolNumbers(bytes: number): number {
  let at = numbersPos;
  if (bytes > numbersBytes.length - at) {
    numbersBuffer = new ArrayBuffer(numbersSlab);
    numbersBytes = new Uint8Array(numbersBuffer);
    at = 0;
  }
  numbersPos = (at + bytes + 7) & ~7;
  return at;
}

/** Thrown for data that is not a valid value of the type read, or a value that cannot be written. */
export class NinoError extends Error {
  constructor(message: string) {
    super("Nino: " + message);
    this.name = "NinoError";
  }
}

// ignoreBOM keeps a leading U+FEFF, which is text in Nino.
const utf8Decoder = new TextDecoder("utf-8", { fatal: true, ignoreBOM: true });
const utf8Encoder = new TextEncoder();

/**
 * The bytes a string header (below 2^32, wire-format spec §2.3.1) takes: 1 below 0x80 (the value itself), 3 up to
 * 0xFFFF (0x80 and a uint16), else 5 (0x81 and a uint32).
 */
function headerSize(value: number): number {
  return value < 0x80 ? 1 : value <= 0xffff ? 3 : 5;
}
/** Browsers decode "latin1" as windows-1252, which differs from Latin-1 in 0x80-0x9F (see `decodeLatin1`). */
const latin1Decoder = new TextDecoder("latin1");
/** Replaces unpaired surrogates with U+FFFD, which .NET keeps (see `decodeUtf16`). */
const utf16Decoder = new TextDecoder("utf-16le", { ignoreBOM: true });
/** A char wider than a byte. A string without one is one byte per char in Nino. */
const wide = /[^\x00-\xff]/;
/** A char outside ASCII. */
const nonAscii = /[^\x00-\x7f]/;
/** Milliseconds between 0001-01-01 and 1970-01-01. */
const unixEpochMilliseconds = 62135596800000;
/** Milliseconds between 0001-01-01 and 10000-01-01: past `DateTime.MaxValue`. */
const maxDateMilliseconds = 315537897600000;
/** The char codes of each byte's high and low hex digit. */
const hexHigh = Uint8Array.from({ length: 256 }, (_, i) => "0123456789abcdef".charCodeAt(i >> 4));
const hexLow = Uint8Array.from({ length: 256 }, (_, i) => "0123456789abcdef".charCodeAt(i & 15));
/** The value of each hex digit by its char code, else -1. */
const hexValues = Int8Array.from({ length: 128 }, (_, c) =>
  c >= 48 && c <= 57 ? c - 48 : (c | 32) >= 97 && (c | 32) <= 102 ? (c | 32) - 87 : -1);
/** Chars per `String.fromCharCode` call, well under any engine's argument limit. */
const chunk = 4096;
/**
 * With Node's methods, text longer than this is written by a native call (`latin1Write`, `ucs2Write`, after a regex
 * for chars past U+00FF); shorter text is narrowed char by char in JavaScript, which costs less up to about 48 chars
 * on x64 and Arm64 (32 and 64 measured the same, 8 took twice as long on short text).
 */
const shortText = 48;
/** Without Node's methods, the longest text read and written in JavaScript: TextDecoder and TextEncoder cost more. */
const mediumText = 32;
/**
 * Without Node's methods, the longest text written in JavaScript: a `TextEncoder.encodeInto` call costs about as
 * much as writing 300 chars in Chromium.
 */
const loopText = 256;
/**
 * With Node's methods, the longest text sliced from the reader's decoded window. V8 copies a substring this short
 * into a plain string; a longer one would be a slice of the window, slower to write or test with a regex later.
 */
const textSlice = 12;
/** The window's largest size. */
const textWindow = 4096;
/** The char codes of a text being read, passed to `String.fromCharCode` at once. */
const codes: number[] = [];

/**
 * Open class bodies after which the next one is deferred, as `NinoDepth.Inline` in .NET: its slot gets the instance
 * now and its body is written and read later, from a shallow point (see "Deep nesting" in docs/v4-design.md), so a
 * long list or deep tree, or data forged to nest deeply, costs a bounded stack.
 */
const inlineDepth = 128;
/** Open bodies after which a body that cannot be deferred fails, as `NinoDepth.Max` in .NET. */
const maxDepth = 512;
/** Set in the length of a region followed by a section of the bodies deferred inside it. */
const sectionFlag = -0x80000000;

/** A body deferred until the stack is shallow again. */
interface NinoDeferred<S> {
  value: unknown;
  body: (state: S, value: never) => void;
  /** Where it was deferred: keys its entry when it is in a region's section. */
  position: number;
  // Writer only, to find cycles (see NinoWriter.defer).
  anchor: unknown;
  generation: number;
}

/** The deferred bodies of one call: outside any region, and inside the outermost region open. */
class NinoDeferrals<S> {
  top: NinoDeferred<S>[] = [];
  topHead = 0;
  region: NinoDeferred<S>[] = [];
  regionHead = 0;
  /** Whether `top` is being written or read. */
  topActive = false;
  // Writer only: the deferred object whose body is being written, for cycle detection.
  current: unknown = null;
  currentAnchor: unknown = null;
  currentGeneration = 0;

  add(item: NinoDeferred<S>, regions: number): void {
    (regions === 0 ? this.top : this.region).push(item);
  }
}

function tooDeep(): NinoError {
  return new NinoError(`data nested deeper than ${maxDepth} bodies.`);
}

/** The native Latin-1 and UTF-16 text methods of Node's `Buffer` (Bun's and Deno's too), called on a Uint8Array. */
interface NodeText {
  latin1Slice(this: Uint8Array, start: number, end: number): string;
  ucs2Slice(this: Uint8Array, start: number, end: number): string;
  latin1Write(this: Uint8Array, text: string, offset: number, length: number): number;
  ucs2Write(this: Uint8Array, text: string, offset: number, length: number): number;
}

/** `Buffer.prototype` when it reads and writes Latin-1 and UTF-16 (unpaired surrogates included) as Nino does. */
const nodeText = ((): NodeText | null => {
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
interface Kernels {
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
const kernelText = 4096;
/**
 * Where in the kernels' memory text is copied in and written out (`kernelText` UTF-16 code units): after the bytes of
 * `deserialize`'s reader, which start the memory.
 */
const transfer = scratchSize;
/**
 * In Chromium the kernels read Latin-1 text longer than `mediumText` up to `kernelLatin1` chars (TextDecoder costs
 * the same for longer text), UTF-16 text longer than `kernelUtf16` (shorter: `decodeShortUtf16`), and write text
 * longer than `kernelWrite` (shorter costs less in JavaScript than a call), except ASCII longer than `kernelAscii`,
 * which `TextEncoder.encodeInto` writes faster.
 */
const kernelLatin1 = 128, kernelUtf16 = 24, kernelWrite = 64, kernelAscii = 1024;

/** The kernels, if this engine runs them; undefined until first needed. */
let loaded: Kernels | null | undefined;
/** The kernels in use: `loaded`, unless `useWasm(false)` turned them off. */
let kernels: Kernels | null | undefined;

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
function wasm(): Kernels | null {
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
function fromCodes(codes: ArrayLike<number>): string {
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
function decodeLatin1(data: Uint8Array, at: number, length: number): string {
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
function decodeShortUtf16(v: DataView, at: number, length: number): string {
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

/**
 * Reads values from bytes written by Nino. Generated code reads a run of fixed-size values with one `take`, then
 * each value from `view` at its offset.
 */
export class NinoReader {
  /**
   * The bytes read, up to the value's end. For a value `deserialize` reads, a copy in bytes the next call reuses
   * (longer than the value): read it through the reader, during `read`.
   */
  readonly data: Uint8Array;
  /** `data` as a DataView. */
  readonly view: DataView;
  /** The offset of the next byte. */
  pos = 0;
  // data.length, as a field: a typed array's length costs more to read. A section's entry is read up to its end.
  private end: number;
  // With Node's methods: the data from textStart to textEnd as Latin-1 text, decoded in one native call. Short
  // strings are sliced from it, which costs less than a call each (as msgpackr slices its strings from one decode).
  // It grows to textWindow bytes, so a payload with few strings decodes little.
  private text = "";
  private textStart = 0;
  private textEnd = 0;
  private textSize = 256;
  /** Open class bodies, counted by generated code (see `defer`). */
  depth = 0;
  // Open regions, open struct bodies that can hold themselves, and the deferred bodies (made on the first one).
  private regions = 0;
  private structs = 0;
  private deferrals: NinoDeferrals<NinoReader> | null = null;
  // Where the outermost region ends when a section of deferred bodies follows its members (only the outermost region
  // holds one), else 0.
  private sectionEnd = 0;
  // Reference tracking: the objects of tracked types read so far, in the order of their headers (made on the first).
  private refs: unknown[] | null = null;

  constructor(data: Uint8Array) {
    this.data = data;
    this.view = new DataView(data.buffer, data.byteOffset, data.byteLength);
    this.end = data.length;
  }

  // deserialize: copies `data` into this reader's own bytes (scratchSize of them), as a new reader would start.
  private load(data: Uint8Array): void {
    this.data.set(data);
    this.end = data.length;
    this.pos = this.depth = this.regions = this.structs = this.sectionEnd = this.textStart = this.textEnd = 0;
    this.text = "";
    this.textSize = 256;
    this.deferrals = null;
    this.refs = null;
  }

  /** Moves past `size` bytes: returns where they start. */
  take(size: number): number {
    const at = this.pos;
    if (size > this.end - at) throw new NinoError("unexpected end of data.");
    this.pos = at + size;
    return at;
  }

  /** Skips a value copied as raw memory: returns where it starts. */
  raw(size: number): number { return this.take(size); }
  bool(): boolean { return this.boolAt(this.take(1)); }
  /** A `bool` at `at`: 0 or 1, as every writer writes it; any other byte is invalid data. */
  boolAt(at: number): boolean {
    const b = this.view.getUint8(at);
    if (b > 1) throw new NinoError(`invalid bool byte ${b}.`);
    return b === 1;
  }
  /** The has-value flag of a nullable at `at` (0 or 1, as `boolAt`). */
  flagAt(at: number): boolean {
    const b = this.view.getUint8(at);
    if (b > 1) throw new NinoError(`invalid presence byte ${b}.`);
    return b === 1;
  }
  u8(): number { return this.view.getUint8(this.take(1)); }
  i8(): number { return this.view.getInt8(this.take(1)); }
  u16(): number { return this.view.getUint16(this.take(2), true); }
  i16(): number { return this.view.getInt16(this.take(2), true); }
  u32(): number { return this.view.getUint32(this.take(4), true); }
  i32(): number { return this.view.getInt32(this.take(4), true); }
  u64(): bigint { return this.view.getBigUint64(this.take(8), true); }
  i64(): bigint { return this.view.getBigInt64(this.take(8), true); }
  f32(): number { return this.view.getFloat32(this.take(4), true); }
  f64(): number { return this.view.getFloat64(this.take(8), true); }
  char(): string { return String.fromCharCode(this.u16()); }
  guid(): string { return this.guidAt(this.take(16)); }
  dateTime(): Date { return this.dateTimeAt(this.take(8)); }
  /** `System.TimeSpan`, in ticks (100 ns). */
  timeSpan(): bigint { return this.i64(); }
  decimal(): string { return this.decimalAt(this.take(16)); }

  /** `System.Guid` at `at`, as its usual text form. */
  guidAt(at: number): string {
    // Bytes 0-3, 4-5 and 6-7 are little-endian integers; 8-15 are bytes in order. One call makes the text flat,
    // where concatenating it would leave a string to flatten on first use.
    const d = this.data, h = hexHigh, l = hexLow;
    return String.fromCharCode(
      h[d[at + 3]], l[d[at + 3]], h[d[at + 2]], l[d[at + 2]], h[d[at + 1]], l[d[at + 1]], h[d[at]], l[d[at]], 45,
      h[d[at + 5]], l[d[at + 5]], h[d[at + 4]], l[d[at + 4]], 45,
      h[d[at + 7]], l[d[at + 7]], h[d[at + 6]], l[d[at + 6]], 45,
      h[d[at + 8]], l[d[at + 8]], h[d[at + 9]], l[d[at + 9]], 45,
      h[d[at + 10]], l[d[at + 10]], h[d[at + 11]], l[d[at + 11]], h[d[at + 12]], l[d[at + 12]],
      h[d[at + 13]], l[d[at + 13]], h[d[at + 14]], l[d[at + 14]], h[d[at + 15]], l[d[at + 15]]);
  }

  /** `System.DateTime` at `at`, as a `Date` (to the millisecond; the kind is not kept). */
  dateTimeAt(at: number): Date {
    // ticks / 10000 in exact Number arithmetic (no BigInt): 2^32 = 429496 * 10000 + 7296.
    const lo = this.view.getUint32(at, true), hi = this.view.getUint32(at + 4, true) & 0x3fffffff;
    const ms = hi * 429496 + Math.floor((hi * 7296 + lo) / 10000);
    // Ticks past DateTime.MaxValue's (§9.10), as the Go and Rust readers check and the writer requires.
    if (ms >= maxDateMilliseconds) throw new NinoError("invalid DateTime.");
    return new Date(ms - unixEpochMilliseconds);
  }

  /** `System.Decimal` at `at`, as its exact decimal text. */
  decimalAt(at: number): string {
    const view = this.view, flags = view.getInt32(at, true), hi = view.getUint32(at + 4, true);
    // Only the sign and a scale of at most 28 (§9.10), as the Go and Rust readers check and the writer requires.
    if ((flags & 0x7f00ffff) !== 0 || ((flags >> 16) & 0xff) > 28) throw new NinoError("invalid decimal.");
    const lo = view.getUint32(at + 8, true), mid = view.getUint32(at + 12, true);
    // The 96-bit integer: below 2^53 in exact Number arithmetic, else as a BigInt.
    let digits = hi === 0 && mid < 0x200000 ? (mid * 4294967296 + lo).toString()
      : ((BigInt(hi) << 64n) | (BigInt(mid) << 32n) | BigInt(lo)).toString();
    const scale = (flags >> 16) & 0xff;
    if (scale > 0) {
      digits = digits.padStart(scale + 1, "0");
      digits = digits.slice(0, digits.length - scale) + "." + digits.slice(digits.length - scale);
    }
    return flags < 0 ? "-" + digits : digits;
  }

  /** A collection length: -1 for null. */
  length(): number {
    const length = this.i32();
    if (length < -1 || length > this.end - this.pos) throw new NinoError(`invalid length ${length}.`);
    return length;
  }

  /**
   * A string header (wire-format spec §2.3.1): its first byte gives its size, as MessagePack's string markers do:
   * the value itself below 0x80, else 0x80 and a uint16 or 0x81 and a uint32 (little-endian), minimal. One byte
   * inline (up to 63 chars), longer ones in `longHeader`.
   */
  header(): number {
    const pos = this.pos;
    if (pos < this.end) {
      const first = this.data[pos];
      if (first < 0x80) {
        this.pos = pos + 1;
        return first;
      }
    }
    return this.longHeader();
  }

  /**
   * A header of three or five bytes, or none: rejects one cut, one with another first byte or one that a shorter
   * form holds (not minimal).
   */
  private longHeader(): number {
    const data = this.data, at = this.pos, left = this.end - at;
    if (left <= 0) throw new NinoError("unexpected end of data.");
    const first = data[at];
    if (first === 0x80) {
      if (left < 3) throw new NinoError("unexpected end of data.");
      const value = this.view.getUint16(at + 1, true);
      if (value < 0x80) throw new NinoError("string header not minimal.");
      this.pos = at + 3;
      return value;
    }
    if (first !== 0x81) throw new NinoError("string header out of range.");
    if (left < 5) throw new NinoError("unexpected end of data.");
    const value = this.view.getUint32(at + 1, true);
    if (value <= 0xffff) throw new NinoError("string header not minimal.");
    this.pos = at + 5;
    return value;
  }

  /** A string (the default encoding): 0 null, 2n + 1 then n Latin-1 bytes, or 2n then n UTF-16 code units. */
  string(): string | null {
    // Inline: text of up to 63 one-byte chars, whose one-byte header is odd and below 0x80, checked with its text.
    const at = this.pos;
    if (at < this.end) {
      const first = this.data[at];
      if ((first & 0x81) === 1) {
        const length = first >>> 1, start = at + 1;
        if (length > this.end - start) throw new NinoError("unexpected end of data.");
        this.pos = start + length;
        return length === 0 ? "" : this.latin1(start, length);
      }
    }
    return this.otherString();
  }

  /**
   * `string` for every other header: null, UTF-16 text, or one-byte text of 64 chars or more. The header, the bounds
   * checks and the UTF-16 decoding are written out here, not called, so the function stays well over V8's limit for
   * inlining (460 bytes of bytecode; 574 in Node 24) and never competes with the short-text path for the inlining
   * budget of the functions that read strings.
   */
  private otherString(): string | null {
    const data = this.data, view = this.view, pos = this.pos;
    let header: number;
    if (pos < this.end && data[pos] < 0x80) {
      header = data[pos];
      this.pos = pos + 1;
    } else {
      header = this.longHeader();
    }
    const length = header >>> 1, start = this.pos;
    if ((header & 1) !== 0) {
      if (length > this.end - start) throw new NinoError("unexpected end of data.");
      this.pos = start + length;
      return length === 0 ? "" : this.latin1(start, length);
    }
    if (header === 0) return null;
    if (length > (this.end - start) >>> 1) throw new NinoError(`invalid length ${length}.`);
    const at = start;
    this.pos = at + 2 * length;
    if (length <= 24) return decodeShortUtf16(view, at, length);
    if (nodeText !== null) return nodeText.ucs2Slice.call(data, at, at + 2 * length);
    if (length > kernelUtf16 && length <= kernelText) {
      const k = kernels === undefined ? wasm() : kernels;
      if (k !== null) return k.utf16(this.inKernels(k, at, length * 2), length);
    }
    if (length <= mediumText) {
      codes.length = length;
      for (let i = 0; i < length; i++) codes[i] = view.getUint16(at + 2 * i, true);
      return String.fromCharCode.apply(null, codes);
    }
    // The decoder writes U+FFFD for an unpaired surrogate: text with one is read code by code instead.
    const text = utf16Decoder.decode(new Uint8Array(data.buffer, data.byteOffset + at, 2 * length));
    if (!text.includes("\ufffd")) return text;
    const units = new Uint16Array(length);
    for (let i = 0; i < units.length; i++) units[i] = view.getUint16(at + 2 * i, true);
    return fromCodes(units);
  }

  /** Where the `size` bytes at `at` (taken, at most 2 * kernelText) are in the kernels' memory: copied if not there. */
  private inKernels(k: Kernels, at: number, size: number): number {
    const data = this.data;
    if (data.buffer === k.memory.buffer) return data.byteOffset + at;
    k.bytes.set(data.subarray(at, at + size), transfer);
    return transfer;
  }

  /** The text of `length` Latin-1 bytes at `at`. */
  private latin1(at: number, length: number): string {
    if (nodeText === null) {
      if (length > mediumText && length <= kernelLatin1) {
        const k = kernels === undefined ? wasm() : kernels;
        if (k !== null) return k.latin1(this.inKernels(k, at, length), length);
      }
      return decodeLatin1(this.data, at, length);
    }
    if (length > textSlice) return decodeLatin1(this.data, at, length);
    if (at < this.textStart || at + length > this.textEnd) {
      const end = Math.min(this.end, at + this.textSize);
      if (this.textSize < textWindow) this.textSize *= 2;
      this.text = nodeText.latin1Slice.call(this.data, at, end);
      this.textStart = at;
      this.textEnd = end;
    }
    return this.text.substring(at - this.textStart, at - this.textStart + length);
  }

  /** A string written as UTF-8 (`[NinoUtf8]`): char count plus one (0 null), then (if not empty) byte count and bytes. */
  utf8(): string | null {
    const header = this.header();
    if (header === 0) return null;
    if (header === 1) return "";
    const chars = header - 1;
    if (chars > 0x7fffffff) throw new NinoError(`invalid length ${chars}.`);
    const bytes = this.header();
    if (bytes < chars || bytes > 3 * chars) throw new NinoError(`invalid length ${bytes}.`);
    const at = this.take(bytes);
    // As many bytes as chars: ASCII, the same as Latin-1 (if it is valid UTF-8).
    if (bytes === chars) {
      const text = this.latin1(at, bytes);
      if (!nonAscii.test(text)) return text;
    }
    let value: string;
    try {
      value = utf8Decoder.decode(new Uint8Array(this.data.buffer, this.data.byteOffset + at, bytes));
    } catch {
      // The decoder is fatal: it throws a TypeError for bytes that are not UTF-8.
      throw new NinoError("invalid UTF-8 string.");
    }
    if (value.length !== chars) throw new NinoError("invalid UTF-8 string.");
    return value;
  }

  /** A `byte[]`: length, then the bytes (a copy). */
  bytes(): Uint8Array<ArrayBuffer> | null {
    const length = this.length();
    if (length < 0) return null;
    const at = this.take(length);
    // A copy, and a Uint8Array even when the data is a Node Buffer.
    return new Uint8Array(this.data.subarray(at, at + length));
  }

  /**
   * An array or list of a fixed-size number type (`int[]`, `List<double>`...) as a typed array of `type`: its length
   * (unless given, when the caller read it), then the elements copied in one block (from a view of the data when the
   * data is aligned to the elements, else from its bytes). Those of up to 8 KB are views of a buffer they share: read
   * one by its `byteOffset` and `length`.
   */
  numbers<T extends NinoNumbers>(type: NinoNumbersType<T>, length: number = this.length()): T | null {
    if (length < 0) return null;
    const size = type.BYTES_PER_ELEMENT, bytes = length * size, at = this.take(bytes), data = this.data;
    if (bytes > numbersPooled) {
      const values = new type(length), start = data.byteOffset + at;
      if (start % size === 0) (values as Uint8Array).set(new type(data.buffer as ArrayBuffer, start, length) as Uint8Array);
      else new Uint8Array(values.buffer).set(data.subarray(at, at + bytes));
      if (!littleEndian) swapBytes(new Uint8Array(values.buffer), 0, bytes, size);
      return values;
    }
    const to = poolNumbers(bytes), pool = numbersBytes;
    pool.set(data.subarray(at, at + bytes), to);
    if (!littleEndian) swapBytes(pool, to, to + bytes, size);
    return new type(numbersBuffer, to, length);
  }


  /** An array, list or other sequence: length, then each element. */
  array<T>(read: (reader: NinoReader) => T): T[] | null {
    const length = this.length();
    if (length < 0) return null;
    const values = new Array<T>(length);
    for (let i = 0; i < length; i++) values[i] = read(this);
    return values;
  }

  /** A set: length, then each element. An element read twice is invalid data, as in .NET (see `duplicateKey`). */
  set<T>(read: (reader: NinoReader) => T): Set<T> | null {
    const length = this.length();
    if (length < 0) return null;
    const values = new Set<T>();
    for (let i = 0; i < length; i++) values.add(read(this));
    if (values.size !== length) throw this.duplicateKey();
    return values;
  }

  /** A dictionary: length, then each key and value. A key read twice is invalid data, as in .NET (see `duplicateKey`). */
  map<K, V>(readKey: (reader: NinoReader) => K, readValue: (reader: NinoReader) => V): Map<K, V> | null {
    const length = this.length();
    if (length < 0) return null;
    const map = new Map<K, V>();
    for (let i = 0; i < length; i++) {
      const key = readKey(this);
      map.set(key, readValue(this));
    }
    if (map.size !== length) throw this.duplicateKey();
    return map;
  }

  /**
   * The error for a map or set read with fewer entries than its length: a key or element came twice, which .NET's
   * dictionaries and sets reject (Nino writes none). Generated code checks the size once, after the loop. Keys are
   * compared as `Map` and `Set` compare them: numbers, bigints, strings, booleans and null by value; keys read as
   * objects (`Date`, structs, tuples) by identity, so those are not checked.
   */
  duplicateKey(): NinoError {
    return new NinoError("a key or element is repeated in a map or set.");
  }

  /** A nullable value, `Tuple` or sealed class: a presence byte, then the value if present. */
  nullable<T>(read: (reader: NinoReader) => T): T | null {
    return this.notNull() ? read(this) : null;
  }

  /** A presence byte: true when a value follows. */
  notNull(): boolean {
    const flag = this.u8();
    if (flag > 1) throw new NinoError(`invalid presence byte ${flag}.`);
    return flag === 1;
  }

  /**
   * A version tolerant or keyed body (or a keyed member): its byte length. Returns the offset where its members end
   * (a section of deferred bodies may follow them).
   */
  region(): number {
    const length = this.i32();
    this.regions++;
    if (length >= 0 && length <= this.end - this.pos) return this.pos + length;
    return this.sectionedRegion(length);
  }

  /**
   * A region whose length has `sectionFlag` set: where its members end, before the section and its offset. Checks the
   * section as .NET, Go and Rust do, before any member is read: only the outermost region holds one (`region` has
   * counted it already), and its entries `(int32 key, int32 length, body)` fill it exactly, at least one, keyed in
   * increasing order by positions no later than the entry itself (where the body deferred to it would have begun: in
   * the members, at most at their end, or in the body of an earlier entry).
   */
  private sectionedRegion(length: number): number {
    const total = length & 0x7fffffff, at = this.pos;
    if (length >= 0 || total < 4 || total > this.end - at) throw new NinoError(`invalid length ${length}.`);
    if (this.regions !== 1) throw new NinoError("deferred bodies in a region inside another region.");
    const view = this.view, end = total - 4, members = view.getInt32(at + end, true);
    // At least one entry: a writer flags a region only when it holds a deferred body.
    if (members < 0 || members >= end) throw new NinoError("invalid offset of deferred bodies.");
    let last = -1;
    for (let p = members; p !== end;) {
      if (end - p < 8) throw new NinoError("invalid section of deferred bodies.");
      const key = view.getInt32(at + p, true), size = view.getInt32(at + p + 4, true);
      if (key <= last || key > p || size < 0 || size > end - p - 8)
        throw new NinoError("invalid section of deferred bodies.");
      last = key;
      p += 8 + size;
    }
    this.sectionEnd = at + total;
    return at + members;
  }

  /**
   * Moves to the end of a region, checking that its members stayed inside it. At the end of the outermost region,
   * moves past its section, if any, and reads the bodies deferred inside it from there.
   */
  endRegion(end: number): void {
    if (this.pos > end) throw new NinoError("a member runs past the end of its object.");
    if (--this.regions === 0) {
      const after = this.sectionEnd;
      this.sectionEnd = 0;
      this.pos = after === 0 ? end : after;
      if (this.deferrals !== null) this.readSection(end);
    } else {
      this.pos = end;
    }
  }

  /**
   * Generated code: `endRegion` for a keyed member that was read, whose value must fill its entry exactly. Bytes left
   * over are invalid data, as in .NET, Go and Rust (no writer leaves any); entries of unknown keys are skipped whole
   * by `endRegion`.
   */
  endMember(end: number): void {
    if (this.pos !== end) throw this.memberSize(end);
    this.endRegion(end);
  }

  private memberSize(end: number): NinoError {
    return new NinoError(this.pos > end ? "a member runs past the end of its object."
      : "bytes left over at the end of a keyed member.");
  }

  /**
   * Generated code, for a class body opened when `depth` bodies are open already (128, `NinoDepth.Inline`): queues
   * `value`, whose body `body` reads later. Returns `value`.
   */
  defer<T>(value: T, body: (reader: NinoReader, value: T) => void): T {
    (this.deferrals ??= new NinoDeferrals()).add(
      { value, body: body as (reader: NinoReader, value: never) => void, position: this.pos, anchor: null, generation: 0 },
      this.regions);
    return value;
  }

  /** Generated code: the body of a class that cannot be deferred (built by its constructor in .NET), read in place. */
  deep<T>(value: T, body: (reader: NinoReader, value: T) => void): T {
    const depth = this.depth;
    if (depth + this.structs >= maxDepth) throw tooDeep();
    this.depth = depth + 1;
    body(this, value);
    this.depth = depth;
    return value;
  }

  /** Generated code: closes a class body opened when `depth` bodies were open; at 0, reads the deferred bodies. */
  exit(depth: number): void {
    this.depth = depth;
    if (depth === 0 && this.deferrals !== null) this.readTop();
  }

  /** Generated code: opens the body of a struct that can hold itself, which no class body counts. */
  enterStruct(): void {
    if (++this.structs + this.depth > maxDepth) throw tooDeep();
  }

  /** Generated code: closes a body opened by `enterStruct`. */
  exitStruct(): void { this.structs--; }

  /**
   * Generated code: a reference header (wire-format spec §7.9): -1 for a new object, which the caller reads (and
   * records with `track` as soon as it exists), 0 for null, or n > 0 for the n-th object read before.
   */
  ref(): number {
    const header = this.i32();
    if (header < -1 || header > (this.refs?.length ?? 0)) throw new NinoError(`invalid object reference ${header}.`);
    return header;
  }

  /** Generated code: the n-th object read (`ref`), which must be a `type`. */
  backRef<T>(index: number, type: abstract new (...args: never[]) => T): T {
    const value = this.refs![index - 1];
    if (!(value instanceof type)) throw new NinoError(`object reference ${index} is not a ${type.name}.`);
    return value;
  }

  /** Generated code: the n-th object read (`ref`), which must be one of `types` (those an interface allows). */
  backRefOf<T>(index: number, types: readonly (abstract new (...args: never[]) => unknown)[]): T {
    const value = this.refs![index - 1];
    if (!types.some((type) => value instanceof type)) throw new NinoError(`object reference ${index} is not a type allowed here.`);
    return value as T;
  }

  /**
   * Generated code: the elements of a [NinoType] collection, which are never null (the collection is the object
   * holding them).
   */
  elements<T>(value: T | null): T {
    if (value === null) throw new NinoError("a [NinoType] collection's elements are null.");
    return value;
  }

  /**
   * Generated code: records a new object of a tracked type when it is created, before its members are read: later
   * references to it (from inside it, too) are back references.
   */
  track(value: unknown): void {
    (this.refs ??= []).push(value);
  }

  /** Generated code: a tracked sealed class, its reference header then its body when new; null, or an earlier object. */
  tracked<T>(type: abstract new (...args: never[]) => T, read: (reader: NinoReader) => T): T | null {
    const header = this.ref();
    return header === -1 ? read(this) : header === 0 ? null : this.backRef(header, type);
  }

  /**
   * Generated code: a tracked object behind a type id its declared type (`object`, a base or interface not tracked)
   * does not track: its reference header, then its body when new. A null object is a type id of 0, never a header
   * of 0 here.
   */
  trackedOf<T>(type: abstract new (...args: never[]) => T, read: (reader: NinoReader) => T): T {
    const header = this.ref();
    if (header === 0) throw new NinoError("a null object reference after a type id.");
    return header === -1 ? read(this) : this.backRef(header, type);
  }

  /** Every body is closed: reads the bodies deferred outside regions, in order, each from depth 1. */
  private readTop(): void {
    const d = this.deferrals!;
    if (d.topActive || d.topHead === d.top.length) return;
    d.topActive = true;
    while (d.topHead < d.top.length) {
      const item = d.top[d.topHead++];
      this.depth = 1;
      item.body(this, item.value as never);
    }
    this.depth = 0;
    d.top.length = d.topHead = 0;
    d.topActive = false;
  }

  /**
   * After the outermost region (its members end at `regionEnd`; the reader is past its section and the section's
   * offset): reads each body deferred inside it from its entry, `(int32 key, int32 length, body)`, matched in order by
   * the key (where it was deferred, relative to the region's start). Entries of objects this reader skipped (members
   * it does not know) are skipped. Bodies deferred while reading them are appended.
   */
  private readSection(regionEnd: number): void {
    const d = this.deferrals!, queue = d.region;
    if (d.regionHead === queue.length) return;
    const after = this.pos, sectionEnd = after - 4;
    if (sectionEnd <= regionEnd) throw new NinoError("missing deferred bodies.");
    const view = this.view, start = regionEnd - view.getInt32(sectionEnd, true);
    const end = this.end, depth = this.depth, entryDepth = Math.min(depth, inlineDepth - 1) + 1;
    let at = regionEnd;
    this.regions = 1;
    while (d.regionHead < queue.length) {
      const item = queue[d.regionHead++];
      for (;;) {
        if (sectionEnd - at < 8) throw new NinoError("unexpected end of data.");
        const key = view.getInt32(at, true), length = view.getInt32(at + 4, true);
        at += 8;
        if (length < 0 || length > sectionEnd - at) throw new NinoError(`invalid length ${length}.`);
        const entry = at;
        at += length;
        if (start + key !== item.position) continue;
        this.pos = entry;
        this.end = entry + length;
        this.depth = entryDepth;
        item.body(this, item.value as never);
        break;
      }
    }
    this.regions = 0;
    this.end = end;
    this.depth = depth;
    this.pos = after;
    queue.length = d.regionHead = 0;
  }
}

/**
 * Writes values in Nino's format. Generated code writes a run of fixed-size values with one `reserve`, then each
 * value into `view` at its offset.
 */
export class NinoWriter {
  private buffer: Uint8Array<ArrayBuffer> = new Uint8Array(256);
  /** The buffer as a DataView. Growing replaces it, so read it after `reserve`. */
  view = new DataView(this.buffer.buffer);
  /** The offset of the next byte: the number of bytes written, unless `serialize` uses the writer. */
  pos = 0;
  // buffer.length, as a field: a typed array's length costs more to read.
  private end = 256;
  // Where the value being written starts: 0, unless `serialize` uses the writer (see `next`).
  private start = 0;
  /** Open class bodies, counted by generated code (see `defer`). */
  depth = 0;
  // Open regions, open struct bodies that can hold themselves, and the deferred bodies (made on the first one).
  private regions = 0;
  private structs = 0;
  private deferrals: NinoDeferrals<NinoWriter> | null = null;
  // The size of the last value `detach` returned.
  private last = 0;
  // Reference tracking: the number of each object of a tracked type written so far (made on the first).
  private refs: Map<object, number> | null = null;

  /** Moves past `size` bytes, growing the buffer if needed: returns where they start. */
  reserve(size: number): number {
    const at = this.pos;
    if (size > this.end - at) {
      // Offsets handed out stay valid: the value's bytes keep their place in the new buffer.
      const grown = new Uint8Array(Math.max(this.end * 2, at + size));
      grown.set(this.buffer.subarray(this.start, at), this.start);
      this.buffer = grown;
      this.view = new DataView(grown.buffer);
      this.end = grown.length;
    }
    this.pos = at + size;
    return at;
  }

  /**
   * The int32 `value` at `at` (reserved), as four byte stores: in Chromium cheaper than the view's setter at an
   * unaligned offset, and the same in Node.
   */
  private int32At(at: number, value: number): void {
    const buffer = this.buffer;
    buffer[at] = value;
    buffer[at + 1] = value >> 8;
    buffer[at + 2] = value >> 16;
    buffer[at + 3] = value >> 24;
  }

  /** The bytes written so far, copied. */
  toBytes(): Uint8Array<ArrayBuffer> { return this.buffer.slice(this.start, this.pos); }

  /**
   * Starts a value after the last one `detach` returned, 8-byte aligned (so it can be viewed as wider typed arrays),
   * in a new buffer when less room is left than the last value took (room for 16 such values, up to 1 MiB).
   */
  next(): void {
    let at = (this.pos + 7) & ~7;
    // A new buffer when the room left is less than the last value took: values of one type are about one size, and
    // growing a buffer copies what the value wrote so far.
    const last = this.last;
    if (this.end - at < Math.max(slabSize >> 4, last)) {
      const size = Math.max(slabSize, Math.min(16 * last, 1 << 20), 2 * last);
      this.buffer = new Uint8Array(size);
      this.view = new DataView(this.buffer.buffer);
      this.end = size;
      at = 0;
    }
    this.start = this.pos = at;
    // A value that failed may have left bodies open.
    this.depth = this.regions = this.structs = 0;
    this.deferrals = null;
    this.refs = null;
  }

  /** The value started by `next`, as a view of the buffer, which the writer does not write to again. */
  detach(): Uint8Array<ArrayBuffer> {
    const size = this.pos - this.start;
    this.last = size;
    return new Uint8Array(this.buffer.buffer, this.start, size);
  }

  /** The bytes written so far, without a copy: valid until the writer writes again. */
  written(): Uint8Array { return this.buffer.subarray(this.start, this.pos); }

  /** The size of the buffer. */
  get capacity(): number { return this.end; }

  /** Reserves a value copied as raw memory, zeroed (as Nino zeroes padding): returns where it starts. */
  raw(size: number): number {
    const at = this.reserve(size);
    this.buffer.fill(0, at, at + size);
    return at;
  }

  bool(value: boolean): void { const at = this.reserve(1); this.view.setUint8(at, value ? 1 : 0); }
  u8(value: number): void { const at = this.reserve(1); this.view.setUint8(at, value); }
  i8(value: number): void { const at = this.reserve(1); this.view.setInt8(at, value); }
  u16(value: number): void { const at = this.reserve(2); this.view.setUint16(at, value, true); }
  i16(value: number): void { const at = this.reserve(2); this.view.setInt16(at, value, true); }
  u32(value: number): void { const at = this.reserve(4); this.view.setUint32(at, value, true); }
  i32(value: number): void { this.int32At(this.reserve(4), value | 0); }
  // A bigint out of range wraps to 64 bits, as setBig(U)Int64 converts it.
  u64(value: bigint): void { const at = this.reserve(8); this.view.setBigUint64(at, value, true); }
  i64(value: bigint): void { const at = this.reserve(8); this.view.setBigInt64(at, value, true); }
  f32(value: number): void { const at = this.reserve(4); this.view.setFloat32(at, value, true); }
  f64(value: number): void { const at = this.reserve(8); this.view.setFloat64(at, value, true); }
  // "" has no char code (NaN), which a Uint16 converts to 0.
  char(value: string): void { this.u16(value.charCodeAt(0)); }
  guid(value: string): void { this.guidAt(this.reserve(16), value); }
  dateTime(value: Date): void { this.dateTimeAt(this.reserve(8), value); }
  timeSpan(ticks: bigint): void { this.i64(ticks); }
  decimal(value: string): void { this.decimalAt(this.reserve(16), value); }

  /** `System.Guid` from its text form (`D`, `N` or `B`), at `at`. */
  guidAt(at: number, value: string): void {
    const buffer = this.buffer;
    let digit = 0;
    for (let i = 0; i < value.length; i++) {
      const c = value.charCodeAt(i), nibble = c < 128 ? hexValues[c] : -1;
      if (nibble < 0) {
        if (c === 45 || c === 123 || c === 125) continue; // - { }
        digit = -1;
        break;
      }
      if (digit === 32) { digit = -1; break; }
      // Bytes 0-3, 4-5 and 6-7 are little-endian integers; 8-15 are bytes in order.
      const byte = digit >> 1, index = byte < 4 ? 3 - byte : byte < 6 ? 9 - byte : byte < 8 ? 13 - byte : byte;
      buffer[at + index] = digit & 1 ? buffer[at + index] | nibble : nibble << 4;
      digit++;
    }
    if (digit !== 32) throw new NinoError(`invalid Guid "${value}".`);
  }

  /** A UTC `System.DateTime` at `at`. */
  dateTimeAt(at: number, value: Date): void {
    // (milliseconds since 0001) * 10000 as two 32-bit halves, exact in Number arithmetic (no BigInt).
    const ms = value.getTime() + unixEpochMilliseconds;
    if (!(ms >= 0 && ms < maxDateMilliseconds)) throw new NinoError(`date out of range: ${value.getTime()}.`);
    const a = Math.floor(ms / 65536) * 10000, b = (ms % 65536) * 10000;
    const low = (a % 65536) * 65536 + b;
    this.view.setUint32(at, low % 4294967296, true);
    this.view.setUint32(at + 4, (Math.floor(a / 65536) + Math.floor(low / 4294967296)) | 0x40000000, true); // UTC
  }

  /** `System.Decimal` from its decimal text, at `at`. */
  decimalAt(at: number, value: string): void {
    const view = this.view;
    if (value === "0") {
      view.setBigUint64(at, 0n);
      view.setBigUint64(at + 8, 0n);
      return;
    }
    const match = /^([+-])?(\d*)(?:\.(\d*))?$/.exec(value.trim());
    if (match === null || (match[2] + (match[3] ?? "")).length === 0) throw new NinoError(`invalid decimal "${value}".`);
    const fraction = match[3] ?? "", text = match[2] + fraction || "0";
    if (fraction.length > 28) throw new NinoError(`decimal out of range "${value}".`);
    let negative = match[1] === "-";
    // Up to 15 digits in exact Number arithmetic, else as a BigInt.
    if (text.length <= 15) {
      const units = Number(text);
      negative &&= units !== 0;
      view.setUint32(at + 4, 0, true);
      view.setUint32(at + 8, units % 4294967296, true);
      view.setUint32(at + 12, Math.floor(units / 4294967296), true);
    } else {
      const units = BigInt(text);
      if (units >= 1n << 96n) throw new NinoError(`decimal out of range "${value}".`);
      negative &&= units !== 0n;
      view.setUint32(at + 4, Number(units >> 64n), true);
      view.setBigUint64(at + 8, units, true);
    }
    view.setInt32(at, (fraction.length << 16) | (negative ? 0x80000000 : 0), true);
  }

  /**
   * Writes `value` (below 2^32) at `at` as a string header (wire-format spec §2.3.1): the value itself below 0x80,
   * else 0x80 and a uint16 up to 0xFFFF, else 0x81 and a uint32. Returns the bytes it took (`headerSize(value)`).
   */
  private headerAt(at: number, value: number): number {
    const buffer = this.buffer;
    if (value < 0x80) {
      buffer[at] = value;
      return 1;
    }
    if (value <= 0xffff) {
      buffer[at] = 0x80;
      buffer[at + 1] = value;
      buffer[at + 2] = value >>> 8;
      return 3;
    }
    buffer[at] = 0x81;
    buffer[at + 1] = value;
    buffer[at + 2] = value >>> 8;
    buffer[at + 3] = value >>> 16;
    buffer[at + 4] = value >>> 24;
    return 5;
  }

  /** A header on its own (null, empty, a UTF-8 string's counts). */
  header(value: number): void {
    this.headerAt(this.reserve(headerSize(value)), value);
  }

  /** A string (the default encoding): one byte per char when every char is at most U+00FF, else UTF-16. */
  string(value: string | null): void {
    if (value === null || value === undefined) { this.u8(0); return; }
    const length = value.length;
    if (length <= (nodeText === null ? kernelWrite : shortText) && length < 64) {
      // `narrow`, written out here: the common path stays one function (calls to `narrow` and `utf16` measured
      // 8-12% slower in Chromium on short text, x64 and Arm64). Up to 63 chars: a one-byte header.
      const at = this.reserve(1 + length), buffer = this.buffer, view = this.view;
      // Four chars a store: one bounds check where a byte each took four. The first char is tested on its own, so
      // text that starts above U+00FF reads one char, not four, before it is written as UTF-16.
      let i = 0;
      for (; i + 3 < length; i += 4) {
        const a = value.charCodeAt(i);
        if (a > 0xff) break;
        const b = value.charCodeAt(i + 1), c = value.charCodeAt(i + 2), d = value.charCodeAt(i + 3);
        if ((b | c | d) > 0xff) break;
        view.setUint32(at + 1 + i, a | (b << 8) | (c << 16) | (d << 24), true);
      }
      for (; i < length; i++) {
        const c = value.charCodeAt(i);
        if (c > 0xff) break;
        buffer[at + 1 + i] = c;
      }
      if (i === length) {
        buffer[at] = 2 * length + 1;
        return;
      }
      this.pos = at;
    } else if (nodeText === null) {
      // Out of line, so the short text above compiles as it did without the kernels.
      this.longString(value, length);
      return;
    } else if (!wide.test(value)) {
      this.latin1(value, length);
      return;
    }
    this.wideText(value, length);
  }

  /**
   * The `length` chars of `value`, some above U+00FF, as UTF-16 code units after their header. Out of `string`, whose
   * common path V8 then still inlines into its callers (with this in it, it was too large to: 5% on short ASCII).
   */
  private wideText(value: string, length: number): void {
    // Up to 63 chars a one-byte header, written here: through headerSize and headerAt it cost 5-10% on short text.
    let start = 1, at: number;
    if (length < 64) {
      at = this.reserve(1 + 2 * length);
      this.buffer[at] = 2 * length;
    } else {
      start = headerSize(2 * length);
      at = this.reserve(start + 2 * length);
      this.headerAt(at, 2 * length);
    }
    const o = at + start;
    if (nodeText !== null && length >= 32) {
      nodeText.ucs2Write.call(this.buffer, value, o, 2 * length);
      return;
    }
    // Two chars a store. Up to 31 chars this costs less than Node's ucs2Write, whose call outweighs short text and
    // which writes the chars twice at an odd offset (where a one-byte header leaves them): all but the last into an
    // aligned buffer, then moved. A third faster on 1,000 strings of 16 to 20 chars.
    const view = this.view;
    let i = 0;
    for (; i + 1 < length; i += 2) view.setUint32(o + 2 * i, value.charCodeAt(i) | (value.charCodeAt(i + 1) << 16), true);
    if (i < length) view.setUint16(o + 2 * i, value.charCodeAt(i), true);
  }

  /**
   * The `length` chars of `value` one byte each, after their header, in one pass while every char fits in a byte.
   * Returns false, writing nothing, at the first wider char.
   */
  private narrow(value: string, length: number): boolean {
    const start = headerSize(2 * length), at = this.reserve(start + length), buffer = this.buffer;
    let i = 0;
    for (; i < length; i++) {
      const c = value.charCodeAt(i);
      if (c > 0xff) break;
      buffer[at + start + i] = c;
    }
    if (i === length) {
      this.headerAt(at, 2 * length + 1);
      return true;
    }
    this.pos = at;
    return false;
  }

  /** The `length` chars of `value`, every one at most U+00FF, one byte each after their header. */
  private latin1(value: string, length: number): void {
    const start = headerSize(2 * length), at = this.reserve(start + length);
    this.headerAt(at, 2 * length + 1);
    this.encodeLatin1(value, at + start);
  }

  /** Without Node's methods: the `length` chars of `value` as UTF-16 code units, after their header. */
  private utf16(value: string, length: number): void {
    const start = headerSize(2 * length), at = this.reserve(start + 2 * length);
    this.headerAt(at, 2 * length);
    const view = this.view;
    for (let i = 0; i < length; i++) view.setUint16(at + start + 2 * i, value.charCodeAt(i), true);
  }

  /** `string` without Node's methods, for text longer than `kernelWrite`: the kernels, else JavaScript. */
  private longString(value: string, length: number): void {
    if (length <= kernelText && (length <= kernelAscii || wide.test(value)) && this.stringKernels(value, length)) return;
    if (length <= loopText) {
      if (this.narrow(value, length)) return;
    } else if (!wide.test(value)) {
      this.latin1(value, length);
      return;
    }
    this.utf16(value, length);
  }

  /**
   * `string` with the kernels (text of at most `kernelText` chars): one byte per char if every char fits, else UTF-16.
   * Returns false, writing nothing, without them.
   */
  private stringKernels(value: string, length: number): boolean {
    const k = kernels === undefined ? wasm() : kernels;
    if (k === null) return false;
    const start = headerSize(2 * length);
    if (k.narrow(value, length, transfer) === length) {
      const at = this.reserve(start + length);
      this.headerAt(at, 2 * length + 1);
      this.buffer.set(k.bytes.subarray(transfer, transfer + length), at + start);
      return true;
    }
    k.widen(length, transfer);
    const at = this.reserve(start + 2 * length);
    this.headerAt(at, 2 * length);
    this.buffer.set(k.bytes.subarray(transfer, transfer + 2 * length), at + start);
    return true;
  }

  /** A string as UTF-8 (`[NinoUtf8]`). Unpaired surrogates are written as U+FFFD, as in .NET. */
  utf8(value: string | null): void {
    if (value === null || value === undefined) { this.u8(0); return; }
    const length = value.length;
    if (length === 0) { this.u8(1); return; }
    // The char count plus one, then the byte count: string headers.
    const countSize = headerSize(length + 1), sizeSize = headerSize(length);
    if (!nonAscii.test(value)) {
      // ASCII: as many bytes as chars, the same as Latin-1.
      const at = this.reserve(countSize + sizeSize + length);
      this.headerAt(at, length + 1);
      this.headerAt(at + countSize, length);
      this.encodeLatin1(value, at + countSize + sizeSize);
      return;
    }
    // At most 3 bytes per UTF-16 code unit; the bytes not written are given back. They go after a byte count as
    // long as the char count's (there are at least as many bytes as chars), and move up when it is longer.
    const at = this.reserve(countSize + 5 + 3 * length), text = at + countSize + sizeSize;
    const { written } = utf8Encoder.encodeInto(value, new Uint8Array(this.buffer.buffer, this.buffer.byteOffset + text, 3 * length));
    this.headerAt(at, length + 1);
    const actual = headerSize(written);
    if (actual !== sizeSize) this.buffer.copyWithin(at + countSize + actual, text, text + written);
    this.headerAt(at + countSize, written);
    this.pos = at + countSize + actual + written;
  }

  /** Text whose every char is at most U+00FF, one byte per char at `at` (reserved). */
  private encodeLatin1(value: string, at: number): void {
    const length = value.length, buffer = this.buffer;
    if (nodeText !== null && length > shortText) {
      nodeText.latin1Write.call(buffer, value, at, length);
      return;
    }
    // Here nodeText is null, or the text is short.
    const k = length > kernelWrite && length <= kernelText ? (kernels === undefined ? wasm() : kernels) : null;
    if (length > (k !== null ? kernelAscii : loopText)) {
      // ASCII is the same in UTF-8, written in one native call; other text by the kernels or char by char below.
      const { read, written } = utf8Encoder.encodeInto(value, new Uint8Array(buffer.buffer, at, length));
      if (read === length && written === length) return;
    }
    if (k !== null) {
      k.narrow(value, length, transfer);
      buffer.set(k.bytes.subarray(transfer, transfer + length), at);
      return;
    }
    for (let i = 0; i < length; i++) buffer[at + i] = value.charCodeAt(i);
  }

  /** A `byte[]`: length, then the bytes. */
  bytes(value: Uint8Array | null): void {
    if (value === null || value === undefined) { this.i32(-1); return; }
    this.i32(value.length);
    const at = this.reserve(value.length);
    this.buffer.set(value, at);
  }

  /** An array or list of a fixed-size number type (`int[]`, `List<double>`...): length, then its bytes as one block. */
  numbers(values: NinoNumbers | null): void {
    if (values === null || values === undefined) { this.i32(-1); return; }
    const length = values.length, bytes = values.byteLength;
    this.i32(length);
    const at = this.reserve(bytes);
    this.buffer.set(new Uint8Array(values.buffer, values.byteOffset, bytes), at);
    if (!littleEndian) swapBytes(this.buffer, at, at + bytes, values.BYTES_PER_ELEMENT);
  }


  array<T>(values: readonly T[] | null, write: (writer: NinoWriter, value: T) => void): void {
    if (values === null || values === undefined) { this.i32(-1); return; }
    this.i32(values.length);
    for (let i = 0; i < values.length; i++) write(this, values[i]);
  }

  set<T>(values: ReadonlySet<T> | null, write: (writer: NinoWriter, value: T) => void): void {
    if (values === null || values === undefined) { this.i32(-1); return; }
    this.i32(values.size);
    for (const value of values) write(this, value);
  }

  map<K, V>(map: ReadonlyMap<K, V> | null, writeKey: (writer: NinoWriter, key: K) => void,
    writeValue: (writer: NinoWriter, value: V) => void): void {
    if (map === null || map === undefined) { this.i32(-1); return; }
    this.i32(map.size);
    // The keys and the values side by side: for-of over the entries makes an array an entry.
    for (let keys = map.keys(), values = map.values(), i = map.size; i > 0; i--) {
      writeKey(this, keys.next().value as K);
      writeValue(this, values.next().value as V);
    }
  }

  nullable<T>(value: T | null, write: (writer: NinoWriter, value: T) => void): void {
    if (value === null || value === undefined) { this.u8(0); return; }
    this.u8(1);
    write(this, value);
  }

  /** Starts a version tolerant or keyed body (or a keyed member): returns where its length goes. */
  region(): number {
    this.regions++;
    return this.reserve(4);
  }

  /**
   * Ends the region started at `start`: writes its byte length. The outermost region is followed by the bodies
   * deferred inside it, if any.
   */
  endRegion(start: number): void {
    if (--this.regions === 0 && this.deferrals !== null && this.deferrals.regionHead !== this.deferrals.region.length) {
      this.writeSection(start);
      return;
    }
    this.view.setInt32(start, this.pos - start - 4, true);
  }

  /**
   * Generated code, for a class body opened when `depth` bodies are open already (128, `NinoDepth.Inline`): queues
   * `value`, whose body `body` writes later. Throws on a cycle: the deferred objects form chains (each deferred while
   * writing the one before), and an object that comes back in its own chain would be written forever. It is compared
   * with the chain's object at the last power-of-two length (Brent), as in .NET.
   */
  defer<T>(value: T, body: (writer: NinoWriter, value: T) => void): void {
    const d = this.deferrals ??= new NinoDeferrals();
    const generation = d.currentGeneration;
    const anchor = generation !== 0 && (generation & (generation - 1)) === 0 ? d.current : d.currentAnchor;
    if (value === d.current || value === anchor)
      throw new NinoError(`${(value as object).constructor.name} is part of a cycle, which cannot be written.`);
    d.add({ value, body: body as (writer: NinoWriter, value: never) => void, position: this.pos, anchor,
      generation: generation + 1 }, this.regions);
  }

  /** Generated code: the body of a class that cannot be deferred (built by its constructor in .NET), in place. */
  deep<T>(value: T, body: (writer: NinoWriter, value: T) => void): void {
    const depth = this.depth;
    if (depth >= maxDepth) throw tooDeep();
    this.depth = depth + 1;
    body(this, value);
    this.depth = depth;
  }

  /** Generated code: closes a class body opened when `depth` bodies were open; at 0, writes the deferred bodies. */
  exit(depth: number): void {
    this.depth = depth;
    if (depth === 0 && this.deferrals !== null) this.writeTop();
  }

  /**
   * Generated code: opens the body of a struct that can hold itself, which no class body counts (a value .NET could
   * not read, or a cycle, throws).
   */
  enterStruct(): void {
    if (++this.structs + this.depth > maxDepth) throw tooDeep();
  }

  /** Generated code: closes a body opened by `enterStruct`. */
  exitStruct(): void { this.structs--; }

  /**
   * Generated code: the reference header of an object of a tracked type (wire-format spec §7.9): 0 for null, its
   * number for an object written before, or -1 for a new one: then it returns true, and the caller writes its body.
   */
  ref<T extends object>(value: T | null | undefined): value is T {
    if (value === null || value === undefined) {
      this.i32(0);
      return false;
    }
    const refs = this.refs ??= new Map();
    const index = refs.get(value);
    if (index !== undefined) {
      this.i32(index);
      return false;
    }
    refs.set(value, refs.size + 1);
    this.i32(-1);
    return true;
  }

  /** Every body is closed and no region is open: writes the bodies deferred outside regions, in order. */
  private writeTop(): void {
    const d = this.deferrals!;
    if (d.topActive || d.topHead === d.top.length) return;
    d.topActive = true;
    while (d.topHead < d.top.length) this.writeDeferred(d, d.top[d.topHead++], 1);
    this.depth = 0;
    d.top.length = d.topHead = 0;
    d.topActive = false;
    d.current = d.currentAnchor = null;
    d.currentGeneration = 0;
  }

  private writeDeferred(d: NinoDeferrals<NinoWriter>, item: NinoDeferred<NinoWriter>, depth: number): void {
    d.current = item.value;
    d.currentAnchor = item.anchor;
    d.currentGeneration = item.generation;
    this.depth = depth;
    item.body(this, item.value as never);
  }

  /**
   * Ends the outermost region (its length at `lengthAt`): writes the bodies deferred inside it as a section of
   * `(int32 key, int32 length, body)` entries, then the section's offset, and sets `sectionFlag` in its length.
   */
  private writeSection(lengthAt: number): void {
    const d = this.deferrals!, queue = d.region, start = lengthAt + 4, section = this.pos, depth = this.depth;
    const current = d.current, anchor = d.currentAnchor, generation = d.currentGeneration;
    const entryDepth = Math.min(depth, inlineDepth - 1) + 1;
    this.regions = 1;
    while (d.regionHead < queue.length) {
      const item = queue[d.regionHead++], at = this.reserve(8);
      this.view.setInt32(at, item.position - start, true);
      this.writeDeferred(d, item, entryDepth);
      this.view.setInt32(at + 4, this.pos - at - 8, true);
    }
    this.regions = 0;
    this.depth = depth;
    d.current = current;
    d.currentAnchor = anchor;
    d.currentGeneration = generation;
    queue.length = d.regionHead = 0;
    const at = this.reserve(4);
    this.view.setInt32(at, section - start, true);
    this.view.setInt32(lengthAt, (this.pos - start) | sectionFlag, true);
  }
}

interface NinoEntry {
  id: number;
  read(reader: NinoReader): unknown;
  write(writer: NinoWriter, value: unknown): void;
}

/**
 * The [NinoType]s written behind a type id (non-sealed classes, interfaces, `object`): the generated code adds every
 * type it declares, by its `NinoTypeId`.
 */
export class NinoRegistry {
  private readonly byId = new Map<number, NinoEntry>();
  private readonly byType = new Map<Function, NinoEntry>();

  /** Adds a type: its id, its class and how its body is read and written. */
  add<T>(id: number, type: Function, read: (reader: NinoReader) => T,
    write: (writer: NinoWriter, value: T) => void): void {
    const entry: NinoEntry = { id, read, write: write as (writer: NinoWriter, value: unknown) => void };
    this.byId.set(id, entry);
    this.byType.set(type, entry);
  }

  /** Reads a type id (0 for null) and the body of that type, which must be one of `ids` (any type when null). */
  read(reader: NinoReader, ids: readonly number[] | null): unknown {
    const id = reader.i32();
    if (id === 0) return null;
    const entry = this.byId.get(id);
    if (entry === undefined || (ids !== null && !ids.includes(id))) throw this.unreadable(id);
    return entry.read(reader);
  }

  /** Writes the type id of `value`'s class (0 for null), which must be one of `ids` (any type when null), and its body. */
  write(writer: NinoWriter, value: unknown, ids: readonly number[] | null): void {
    if (value === null || value === undefined) { writer.i32(0); return; }
    const entry = this.byType.get((value as object).constructor);
    if (entry === undefined || (ids !== null && !ids.includes(entry.id))) throw this.unwritable(value as object);
    writer.i32(entry.id);
    entry.write(writer, value);
  }

  /** The error for a type id that is not a type that can be read where it was read. */
  unreadable(id: number): NinoError {
    return new NinoError(`type id 0x${(id >>> 0).toString(16)} is not a type that can be read here.`);
  }

  /** The error for a value whose class is not a type that can be written where it was written. */
  unwritable(value: object): NinoError {
    return new NinoError(`${value.constructor.name} is not a type that can be written here.`);
  }

  /** The codec of a type written behind a type id: any of `ids`, the ids of the types assignable to it. */
  codec<T>(ids: readonly number[]): NinoCodec<T | null> {
    return {
      read: (reader) => this.read(reader, ids) as T | null,
      write: (writer, value) => this.write(writer, value, ids),
    };
  }
}

