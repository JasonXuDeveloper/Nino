// Nino runtime for TypeScript (5.7 or later, for the typed arrays' buffer types), written by Nino.TypeScript next to the generated types: reads and writes the bytes
// Nino writes in .NET. Little-endian and unaligned; lengths are int32, -1 = null. Do not edit: it is overwritten on
// every build.

/** The Nino.TypeScript version that wrote this file; the generated types expect the same one. */
export const ninoVersion = "__NINO_VERSION__";

/** Reads and writes one type's values. Generated for every [NinoType]. */
export interface NinoCodec<T> {
  read(reader: NinoReader): T;
  write(writer: NinoWriter, value: T): void;
}

let shared: NinoWriter | null = null;
/** The size of the buffers `serialize` writes its results into. */
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

/** Deserializes one value written by `NinoSerializer.Serialize<T>` (or `serialize`). */
export function deserialize<T>(codec: NinoCodec<T>, data: Uint8Array): T {
  return codec.read(new NinoReader(data));
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
/** The char codes of each byte's high and low hex digit. */
const hexHigh = Uint8Array.from({ length: 256 }, (_, i) => "0123456789abcdef".charCodeAt(i >> 4));
const hexLow = Uint8Array.from({ length: 256 }, (_, i) => "0123456789abcdef".charCodeAt(i & 15));
/** The value of each hex digit by its char code, else -1. */
const hexValues = Int8Array.from({ length: 128 }, (_, c) =>
  c >= 48 && c <= 57 ? c - 48 : (c | 32) >= 97 && (c | 32) <= 102 ? (c | 32) - 87 : -1);
/** Chars per `String.fromCharCode` call, well under any engine's argument limit. */
const chunk = 4096;
/** Text longer than this is read by a native decoder, whose call costs more than reading a short text in JavaScript. */
const shortText = 8;
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

/** The text of `length` UTF-16 code units at `at`. */
function decodeUtf16(data: Uint8Array, at: number, length: number): string {
  if (length <= shortText) {
    let text = "";
    for (let i = 0; i < length; i++) text += String.fromCharCode(data[at + 2 * i] | (data[at + 2 * i + 1] << 8));
    return text;
  }
  if (nodeText !== null) return nodeText.ucs2Slice.call(data, at, at + 2 * length);
  if (length <= mediumText) {
    codes.length = length;
    for (let i = 0; i < length; i++) codes[i] = data[at + 2 * i] | (data[at + 2 * i + 1] << 8);
    return String.fromCharCode.apply(null, codes);
  }
  // The decoder writes U+FFFD for an unpaired surrogate: text with one is read code by code instead.
  const text = utf16Decoder.decode(new Uint8Array(data.buffer, data.byteOffset + at, 2 * length));
  if (!text.includes("\ufffd")) return text;
  const units = new Uint16Array(length);
  for (let i = 0; i < length; i++) units[i] = data[at + 2 * i] | (data[at + 2 * i + 1] << 8);
  return fromCodes(units);
}

/**
 * Reads values from bytes written by Nino. Generated code reads a run of fixed-size values with one `take`, then
 * each value from `view` at its offset.
 */
export class NinoReader {
  readonly data: Uint8Array;
  /** `data` as a DataView. */
  readonly view: DataView;
  /** The offset of the next byte. */
  pos = 0;
  // data.length, as a field: a typed array's length costs more to read.
  private readonly end: number;
  // With Node's methods: the data from textStart to textEnd as Latin-1 text, decoded in one native call. Short
  // strings are sliced from it, which costs less than a call each (as msgpackr slices its strings from one decode).
  // It grows to textWindow bytes, so a payload with few strings decodes little.
  private text = "";
  private textStart = 0;
  private textEnd = 0;
  private textSize = 256;

  constructor(data: Uint8Array) {
    this.data = data;
    this.view = new DataView(data.buffer, data.byteOffset, data.byteLength);
    this.end = data.length;
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
  bool(): boolean { return this.view.getUint8(this.take(1)) !== 0; }
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
    return new Date(hi * 429496 + Math.floor((hi * 7296 + lo) / 10000) - unixEpochMilliseconds);
  }

  /** `System.Decimal` at `at`, as its exact decimal text. */
  decimalAt(at: number): string {
    const view = this.view, flags = view.getInt32(at, true), hi = view.getUint32(at + 4, true);
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

  /** A string (the default encoding): -1 null, `~n` then n Latin-1 bytes, or n then n UTF-16 code units. */
  string(): string | null {
    const header = this.i32();
    if (header < 0) {
      if (header === -1) return null;
      const length = ~header;
      return this.latin1(this.take(length), length);
    }
    if (header === 0) return "";
    if (header > (this.end - this.pos) >> 1) throw new NinoError(`invalid length ${header}.`);
    return decodeUtf16(this.data, this.take(header * 2), header);
  }

  /** The text of `length` Latin-1 bytes at `at`. */
  private latin1(at: number, length: number): string {
    if (nodeText === null || length > textSlice) return decodeLatin1(this.data, at, length);
    if (at < this.textStart || at + length > this.textEnd) {
      const end = Math.min(this.end, at + this.textSize);
      if (this.textSize < textWindow) this.textSize *= 2;
      this.text = nodeText.latin1Slice.call(this.data, at, end);
      this.textStart = at;
      this.textEnd = end;
    }
    return this.text.substring(at - this.textStart, at - this.textStart + length);
  }

  /** A string written as UTF-8 (`[NinoUtf8]`): char count, then (if not empty) byte count and bytes. */
  utf8(): string | null {
    const chars = this.i32();
    if (chars === -1) return null;
    if (chars === 0) return "";
    if (chars < 0) throw new NinoError(`invalid length ${chars}.`);
    const bytes = this.i32();
    if (bytes < chars || bytes > 3 * chars) throw new NinoError(`invalid length ${bytes}.`);
    const at = this.take(bytes);
    // As many bytes as chars: ASCII, the same as Latin-1 (if it is valid UTF-8).
    if (bytes === chars) {
      const text = this.latin1(at, bytes);
      if (!nonAscii.test(text)) return text;
    }
    const value = utf8Decoder.decode(new Uint8Array(this.data.buffer, this.data.byteOffset + at, bytes));
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

  /** An array, list or other sequence: length, then each element. */
  array<T>(read: (reader: NinoReader) => T): T[] | null {
    const length = this.length();
    if (length < 0) return null;
    const values = new Array<T>(length);
    for (let i = 0; i < length; i++) values[i] = read(this);
    return values;
  }

  /** A set: length, then each element. */
  set<T>(read: (reader: NinoReader) => T): Set<T> | null {
    const values = this.array(read);
    return values === null ? null : new Set(values);
  }

  /** A dictionary: length, then each key and value. */
  map<K, V>(readKey: (reader: NinoReader) => K, readValue: (reader: NinoReader) => V): Map<K, V> | null {
    const length = this.length();
    if (length < 0) return null;
    const map = new Map<K, V>();
    for (let i = 0; i < length; i++) {
      const key = readKey(this);
      map.set(key, readValue(this));
    }
    return map;
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

  /** A version tolerant or keyed body (or a keyed member): its byte length. Returns the offset where it ends. */
  region(): number {
    const length = this.i32();
    if (length < 0 || length > this.end - this.pos) throw new NinoError(`invalid length ${length}.`);
    return this.pos + length;
  }

  /** Moves to the end of a region, checking that its members stayed inside it. */
  endRegion(end: number): void {
    if (this.pos > end) throw new NinoError("a member runs past the end of its object.");
    this.pos = end;
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

  /** The bytes written so far, copied. */
  toBytes(): Uint8Array<ArrayBuffer> { return this.buffer.slice(this.start, this.pos); }

  /**
   * Starts a value after the last one `detach` returned, 8-byte aligned (so it can be viewed as wider typed arrays),
   * in a new buffer when little room is left.
   */
  next(): void {
    let at = (this.pos + 7) & ~7;
    if (this.end - at < slabSize >> 4) {
      this.buffer = new Uint8Array(slabSize);
      this.view = new DataView(this.buffer.buffer);
      this.end = slabSize;
      at = 0;
    }
    this.start = this.pos = at;
  }

  /** The value started by `next`, as a view of the buffer, which the writer does not write to again. */
  detach(): Uint8Array<ArrayBuffer> { return new Uint8Array(this.buffer.buffer, this.start, this.pos - this.start); }

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
  i32(value: number): void { const at = this.reserve(4); this.view.setInt32(at, value, true); }
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
    if (!(ms >= 0 && ms < 315537897600000)) throw new NinoError(`date out of range: ${value.getTime()}.`);
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

  /** A string (the default encoding): one byte per char when every char is at most U+00FF, else UTF-16. */
  string(value: string | null): void {
    if (value === null || value === undefined) { this.i32(-1); return; }
    const length = value.length;
    if (length <= (nodeText === null ? loopText : shortText)) {
      // Narrowed in one pass while every char fits in a byte; from the first wider char on, UTF-16 instead.
      const at = this.reserve(4 + length), buffer = this.buffer;
      let i = 0;
      for (; i < length; i++) {
        const c = value.charCodeAt(i);
        if (c > 0xff) break;
        buffer[at + 4 + i] = c;
      }
      if (i === length) {
        this.view.setInt32(at, length === 0 ? 0 : ~length, true);
        return;
      }
      this.pos = at;
    } else if (!wide.test(value)) {
      const at = this.reserve(4 + length);
      this.view.setInt32(at, ~length, true);
      this.encodeLatin1(value, at + 4);
      return;
    }
    const at = this.reserve(4 + 2 * length);
    this.view.setInt32(at, length, true);
    if (nodeText !== null) {
      nodeText.ucs2Write.call(this.buffer, value, at + 4, 2 * length);
      return;
    }
    const view = this.view;
    for (let i = 0; i < length; i++) view.setUint16(at + 4 + 2 * i, value.charCodeAt(i), true);
  }

  /** A string as UTF-8 (`[NinoUtf8]`). Unpaired surrogates are written as U+FFFD, as in .NET. */
  utf8(value: string | null): void {
    if (value === null || value === undefined) { this.i32(-1); return; }
    const length = value.length;
    if (length === 0) { this.i32(0); return; }
    if (!nonAscii.test(value)) {
      // ASCII: as many bytes as chars, the same as Latin-1.
      const at = this.reserve(8 + length);
      this.view.setInt32(at, length, true);
      this.view.setInt32(at + 4, length, true);
      this.encodeLatin1(value, at + 8);
      return;
    }
    // At most 3 bytes per UTF-16 code unit; the bytes not written are given back.
    const at = this.reserve(8 + 3 * length);
    const { written } = utf8Encoder.encodeInto(value, new Uint8Array(this.buffer.buffer, at + 8, 3 * length));
    this.view.setInt32(at, length, true);
    this.view.setInt32(at + 4, written, true);
    this.pos = at + 8 + written;
  }

  /** Text whose every char is at most U+00FF, one byte per char at `at` (reserved). */
  private encodeLatin1(value: string, at: number): void {
    const length = value.length, buffer = this.buffer;
    if (nodeText !== null && length > shortText) {
      nodeText.latin1Write.call(buffer, value, at, length);
      return;
    }
    if (length > loopText) {
      // ASCII is the same in UTF-8, written in one native call; other text char by char below.
      const { read, written } = utf8Encoder.encodeInto(value, new Uint8Array(buffer.buffer, at, length));
      if (read === length && written === length) return;
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
    for (const [key, value] of map) {
      writeKey(this, key);
      writeValue(this, value);
    }
  }

  nullable<T>(value: T | null, write: (writer: NinoWriter, value: T) => void): void {
    if (value === null || value === undefined) { this.u8(0); return; }
    this.u8(1);
    write(this, value);
  }

  /** Starts a version tolerant or keyed body (or a keyed member): returns where its length goes. */
  region(): number { return this.reserve(4); }

  /** Ends the region started at `start`: writes its byte length. */
  endRegion(start: number): void { this.view.setInt32(start, this.pos - start - 4, true); }
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
