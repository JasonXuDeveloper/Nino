// Nino runtime for TypeScript, written by Nino.TypeScript next to the generated types: reads and writes the bytes
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
const slabSize = 64 * 1024;
let slab = new ArrayBuffer(slabSize);
let slabUsed = 0;

/**
 * Serializes `value` with `codec` (what `NinoSerializer.Serialize<T>` writes in .NET) into a new array. The bytes
 * are written into one buffer kept between calls, so each call allocates only the array it returns.
 */
export function serialize<T>(codec: NinoCodec<T>, value: T): Uint8Array {
  // A codec that serializes (a custom one) while this runs gets a writer of its own.
  const writer = shared ?? new NinoWriter();
  shared = null;
  try {
    writer.pos = 0;
    codec.write(writer, value);
    return writer.toBytes();
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

const utf8Decoder = new TextDecoder("utf-8", { fatal: true });
const utf8Encoder = new TextEncoder();
/** Milliseconds between 0001-01-01 and 1970-01-01. */
const unixEpochMilliseconds = 62135596800000;
/** Each byte as two hex digits. */
const hexBytes = Array.from({ length: 256 }, (_, i) => i.toString(16).padStart(2, "0"));
/** Chars per `String.fromCharCode` call, well under any engine's argument limit. */
const chunk = 4096;

/** The text of UTF-16 code units, unpaired surrogates included (as .NET keeps them). */
function fromCodes(codes: ArrayLike<number>): string {
  if (codes.length <= chunk) return String.fromCharCode.apply(null, codes as number[]);
  let text = "";
  for (let i = 0; i < codes.length; i += chunk)
    text += String.fromCharCode.apply(null, Array.prototype.slice.call(codes, i, i + chunk) as number[]);
  return text;
}

/** Reads values from bytes written by Nino. */
export class NinoReader {
  readonly data: Uint8Array;
  private readonly view: DataView;
  /** The offset of the next byte. */
  pos = 0;

  constructor(data: Uint8Array) {
    this.data = data;
    this.view = new DataView(data.buffer, data.byteOffset, data.byteLength);
  }

  private take(size: number): number {
    const at = this.pos;
    if (size > this.data.length - at) throw new NinoError("unexpected end of data.");
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

  /** `System.Guid`, as its usual text form. */
  guid(): string {
    const at = this.take(16), d = this.data, h = hexBytes;
    return h[d[at + 3]] + h[d[at + 2]] + h[d[at + 1]] + h[d[at]] + "-" + h[d[at + 5]] + h[d[at + 4]] + "-" +
      h[d[at + 7]] + h[d[at + 6]] + "-" + h[d[at + 8]] + h[d[at + 9]] + "-" + h[d[at + 10]] + h[d[at + 11]] +
      h[d[at + 12]] + h[d[at + 13]] + h[d[at + 14]] + h[d[at + 15]];
  }

  /** `System.DateTime` as a `Date` (to the millisecond; the kind is not kept). */
  dateTime(): Date {
    // ticks / 10000 in exact Number arithmetic (no BigInt): 2^32 = 429496 * 10000 + 7296.
    const lo = this.u32(), hi = this.u32() & 0x3fffffff;
    return new Date(hi * 429496 + Math.floor((hi * 7296 + lo) / 10000) - unixEpochMilliseconds);
  }

  /** `System.TimeSpan`, in ticks (100 ns). */
  timeSpan(): bigint { return this.i64(); }

  /** `System.Decimal`, as its exact decimal text. */
  decimal(): string {
    const flags = this.i32(), hi = BigInt(this.u32()), lo = this.u64();
    const scale = (flags >> 16) & 0xff;
    let digits = ((hi << 64n) | lo).toString();
    if (scale > 0) {
      digits = digits.padStart(scale + 1, "0");
      digits = digits.slice(0, digits.length - scale) + "." + digits.slice(digits.length - scale);
    }
    return flags < 0 ? "-" + digits : digits;
  }

  /** A collection length: -1 for null. */
  length(): number {
    const length = this.i32();
    if (length < -1 || length > this.data.length - this.pos) throw new NinoError(`invalid length ${length}.`);
    return length;
  }

  /** A string (the default encoding): -1 null, `~n` then n Latin-1 bytes, or n then n UTF-16 code units. */
  string(): string | null {
    const header = this.i32();
    if (header === -1) return null;
    if (header === 0) return "";
    if (header < 0) {
      const length = ~header;
      const at = this.take(length), data = this.data;
      // Short text char by char; longer text in one native call.
      if (length > 24) return fromCodes(data.subarray(at, at + length));
      let text = "";
      for (let i = 0; i < length; i++) text += String.fromCharCode(data[at + i]);
      return text;
    }
    if (header > (this.data.length - this.pos) >> 1) throw new NinoError(`invalid length ${header}.`);
    const at = this.take(header * 2);
    if (header <= 24) {
      let text = "";
      for (let i = 0; i < header; i++) text += String.fromCharCode(this.view.getUint16(at + i * 2, true));
      return text;
    }
    const codes = new Uint16Array(header);
    for (let i = 0; i < header; i++) codes[i] = this.view.getUint16(at + i * 2, true);
    return fromCodes(codes);
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
    const value = utf8Decoder.decode(this.data.subarray(at, at + bytes));
    if (value.length !== chars) throw new NinoError("invalid UTF-8 string.");
    return value;
  }

  /** A `byte[]`: length, then the bytes (a copy). */
  bytes(): Uint8Array | null {
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
    if (length < 0 || length > this.data.length - this.pos) throw new NinoError(`invalid length ${length}.`);
    return this.pos + length;
  }

  /** Moves to the end of a region, checking that its members stayed inside it. */
  endRegion(end: number): void {
    if (this.pos > end) throw new NinoError("a member runs past the end of its object.");
    this.pos = end;
  }
}

/** Writes values in Nino's format. */
export class NinoWriter {
  private buffer = new Uint8Array(256);
  private view = new DataView(this.buffer.buffer);
  /** The number of bytes written. */
  pos = 0;

  // Callers take the offset before touching `buffer` or `view`, which growing replaces.
  private reserve(size: number): number {
    const at = this.pos;
    if (size > this.buffer.length - at) {
      const grown = new Uint8Array(Math.max(this.buffer.length * 2, at + size));
      grown.set(this.buffer.subarray(0, at));
      this.buffer = grown;
      this.view = new DataView(grown.buffer);
    }
    this.pos = at + size;
    return at;
  }

  /**
   * The bytes written so far, copied. Small results share a pooled `ArrayBuffer` (as Node's `Buffer`s do): a new
   * backing store for every result costs more than the serialization, so read them by `byteOffset` and `length`,
   * not through `.buffer`.
   */
  toBytes(): Uint8Array {
    const length = this.pos;
    if (length > slabSize >> 3) return this.buffer.slice(0, length);
    if (length > slabSize - slabUsed) {
      slab = new ArrayBuffer(slabSize);
      slabUsed = 0;
    }
    const bytes = new Uint8Array(slab, slabUsed, length);
    bytes.set(this.buffer.subarray(0, length));
    // Each result starts 8-byte aligned, so it can be viewed as wider typed arrays.
    slabUsed = (slabUsed + length + 7) & ~7;
    return bytes;
  }

  /** The bytes written so far, without a copy: valid until the writer writes again. */
  written(): Uint8Array { return this.buffer.subarray(0, this.pos); }

  /** The size of the buffer. */
  get capacity(): number { return this.buffer.length; }

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
  u64(value: bigint): void { const at = this.reserve(8); this.view.setBigUint64(at, BigInt.asUintN(64, value), true); }
  i64(value: bigint): void { const at = this.reserve(8); this.view.setBigInt64(at, BigInt.asIntN(64, value), true); }
  f32(value: number): void { const at = this.reserve(4); this.view.setFloat32(at, value, true); }
  f64(value: number): void { const at = this.reserve(8); this.view.setFloat64(at, value, true); }
  char(value: string): void { this.u16(value.length === 0 ? 0 : value.charCodeAt(0)); }

  /** `System.Guid` from its text form (`D`, `N` or `B`). */
  guid(value: string): void {
    const at = this.reserve(16), buffer = this.buffer;
    let digit = 0;
    for (let i = 0; i < value.length; i++) {
      const c = value.charCodeAt(i);
      const nibble = c >= 48 && c <= 57 ? c - 48 : (c | 32) >= 97 && (c | 32) <= 102 ? (c | 32) - 87 : -1;
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

  /** Written as a UTC `System.DateTime`. */
  dateTime(value: Date): void {
    // (milliseconds since 0001) * 10000 as two 32-bit halves, exact in Number arithmetic (no BigInt).
    const ms = value.getTime() + unixEpochMilliseconds;
    if (!(ms >= 0 && ms < 315537897600000)) throw new NinoError(`date out of range: ${value.getTime()}.`);
    const a = Math.floor(ms / 65536) * 10000, b = (ms % 65536) * 10000;
    const low = (a % 65536) * 65536 + b;
    this.u32(low % 4294967296);
    this.u32((Math.floor(a / 65536) + Math.floor(low / 4294967296)) | 0x40000000); // kind: UTC
  }

  timeSpan(ticks: bigint): void { this.i64(ticks); }

  decimal(value: string): void {
    if (value === "0") { this.raw(16); return; }
    const match = /^([+-])?(\d*)(?:\.(\d*))?$/.exec(value.trim());
    if (match === null || (match[2] + (match[3] ?? "")).length === 0) throw new NinoError(`invalid decimal "${value}".`);
    const fraction = match[3] ?? "";
    const units = BigInt(match[2] + fraction || "0");
    if (fraction.length > 28 || units >= 1n << 96n) throw new NinoError(`decimal out of range "${value}".`);
    const negative = match[1] === "-" && units !== 0n;
    this.i32((fraction.length << 16) | (negative ? 0x80000000 : 0));
    this.u32(Number(units >> 64n));
    this.u64(units & 0xffffffffffffffffn);
  }

  /** A string (the default encoding): one byte per char when every char is at most U+00FF, else UTF-16. */
  string(value: string | null): void {
    if (value === null || value === undefined) { this.i32(-1); return; }
    const length = value.length;
    // Narrowed in one pass while every char fits in a byte; from the first wider char on, UTF-16 instead.
    const header = this.reserve(4 + length), buffer = this.buffer;
    let i = 0;
    for (; i < length; i++) {
      const c = value.charCodeAt(i);
      if (c > 0xff) break;
      buffer[header + 4 + i] = c;
    }
    if (i === length) {
      this.view.setInt32(header, length === 0 ? 0 : ~length, true);
      return;
    }
    this.pos = header;
    this.i32(length);
    const at = this.reserve(length * 2), view = this.view;
    for (i = 0; i < length; i++) view.setUint16(at + i * 2, value.charCodeAt(i), true);
  }

  /** A string as UTF-8 (`[NinoUtf8]`). Unpaired surrogates are written as U+FFFD, as in .NET. */
  utf8(value: string | null): void {
    if (value === null || value === undefined) { this.i32(-1); return; }
    this.i32(value.length);
    if (value.length === 0) return;
    const bytes = utf8Encoder.encode(value);
    this.i32(bytes.length);
    const at = this.reserve(bytes.length);
    this.buffer.set(bytes, at);
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
    for (const value of values) write(this, value);
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
    if (entry === undefined || (ids !== null && !ids.includes(id)))
      throw new NinoError(`type id 0x${(id >>> 0).toString(16)} is not a type that can be read here.`);
    return entry.read(reader);
  }

  /** Writes the type id of `value`'s class (0 for null), which must be one of `ids` (any type when null), and its body. */
  write(writer: NinoWriter, value: unknown, ids: readonly number[] | null): void {
    if (value === null || value === undefined) { writer.i32(0); return; }
    const entry = this.byType.get((value as object).constructor);
    if (entry === undefined || (ids !== null && !ids.includes(entry.id)))
      throw new NinoError(`${(value as object).constructor.name} is not a type that can be written here.`);
    writer.i32(entry.id);
    entry.write(writer, value);
  }

  /** The codec of a type written behind a type id: any of `ids`, the ids of the types assignable to it. */
  codec<T>(ids: readonly number[]): NinoCodec<T | null> {
    return {
      read: (reader) => this.read(reader, ids) as T | null,
      write: (writer, value) => this.write(writer, value, ids),
    };
  }
}
