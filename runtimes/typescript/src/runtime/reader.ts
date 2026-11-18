import type { NinoCodec } from "./api.ts";
import { NinoDeferrals, inlineDepth, maxDepth, tooDeep } from "./deferrals.ts";
import { NinoError } from "./error.ts";
import { maxGridElements, maxGridLength } from "./grid.ts";
import { NinoKeyGuard, guardKey, guardedKeys } from "./keys.ts";
import { littleEndian, numbersBuffer, numbersBytes, numbersPooled, poolNumbers, swapBytes, type NinoNumbers, type NinoNumbersType } from "./numbers.ts";
import { hexHigh, hexLow, maxDateMilliseconds, unixEpochMilliseconds } from "./scalars.ts";
import {
  codes, decodeLatin1, decodeShortUtf16, fromCodes, kernelLatin1, kernelText, kernelUtf16, kernels, mediumText,
  nodeText, nonAscii, scratchSize, textSlice, textWindow, transfer, utf16Decoder, utf8Decoder, wasm, type Kernels,
} from "./text.ts";

export function readValue<T>(codec: NinoCodec<T>, data: Uint8Array, exact: boolean): T {
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
 * What the collections of one read may allocate ahead of their elements (`NinoReader.capacity`): 16 bytes per byte of
 * data plus 16 KB, an element counted as 8 bytes (an array slot), as the Go and Rust readers budget a read.
 */
const presizePerByte = 16;
const presizeFloor = 16384;

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
  // The bytes the collections of this read may still allocate ahead of their elements (see `capacity`).
  private budget: number;

  constructor(data: Uint8Array) {
    this.data = data;
    this.view = new DataView(data.buffer, data.byteOffset, data.byteLength);
    this.end = data.length;
    this.budget = data.length * presizePerByte + presizeFloor;
  }

  // deserialize: copies `data` into this reader's own bytes (scratchSize of them), as a new reader would start.
  private load(data: Uint8Array): void {
    this.data.set(data);
    this.end = data.length;
    this.budget = data.length * presizePerByte + presizeFloor;
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
  /** The presence byte of a nullable at `at`: true when a value follows (0 or 1, as `boolAt`). */
  presentAt(at: number): boolean {
    const b = this.view.getUint8(at);
    if (b > 1) throw new NinoError(`invalid presence byte ${b}.`);
    return b === 1;
  }
  /**
   * The presence of a .NET `NinoNullable<T>` at `at`, its memory: `size` bytes of presence (0 or 1 in the first, zeros
   * above), then `size` bytes of value, zeros when absent. True when it has a value; any other presence, and a value
   * that is not zeros where it has none, is invalid data (as .NET's untrusted mode has it).
   */
  ninoNullableAt(at: number, size: number): boolean {
    const data = this.data;
    const present = this.presentAt(at);
    let set = 0;
    for (let i = at + 1, end = at + (present ? size : 2 * size); i < end; i++) set |= data[i];
    if (set !== 0) throw new NinoError("invalid NinoNullable: a byte set above its presence or in a value it does not have.");
    return present;
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

  /** Reads a rectangular array's dimensions and checked element count. A first dimension of -1 is null. */
  gridShape(rank: number, minSize: number): { lengths: number[]; count: number } | null {
    const first = this.i32();
    if (first === -1) return null;
    if (first < 0 || first > maxGridLength) throw new NinoError(`invalid length ${first}.`);
    const lengths = [first];
    let count = first;
    for (let i = 1; i < rank; i++) {
      const length = this.i32();
      if (length < 0 || length > maxGridLength) throw new NinoError(`invalid length ${length}.`);
      lengths.push(length);
      count *= length;
      if (count > maxGridElements) throw new NinoError("invalid grid dimensions.");
    }
    // A later zero can make the grid empty. Check the remaining bytes once all dimensions are known.
    if (count * Math.max(minSize, 1) > this.end - this.pos) throw new NinoError("invalid grid dimensions.");
    return { lengths, count };
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


  /**
   * How many of `length` elements (a length checked against the bytes left) a collection allocates before reading
   * them; it grows as it reads the rest. All the collections of one read share one budget of 16 bytes per byte of
   * data plus 16 KB, an element counted as 8 bytes, so collections nested in collections that claim more elements than
   * they hold cannot each allocate the bytes left (as Go's `SliceCap` and Rust's `capacity`). Data a read accepts never
   * spends it all: each element starts at a byte of its own.
   */
  capacity(length: number): number {
    const size = length * 8, budget = this.budget;
    if (size <= budget) {
      this.budget = budget - size;
      return length;
    }
    this.budget = 0;
    return Math.floor(budget / 8);
  }

  /** An array, list or other sequence: length, then each element. */
  array<T>(read: (reader: NinoReader) => T): T[] | null {
    const length = this.length();
    if (length < 0) return null;
    const values = new Array<T>(this.capacity(length));
    for (let i = 0; i < length; i++) values[i] = read(this);
    return values;
  }

  /** A set: length, then each element. An element read twice is invalid data, as in .NET (see `duplicateKey`). */
  set<T>(read: (reader: NinoReader) => T): Set<T> | null {
    const length = this.length();
    if (length < 0) return null;
    const values = new Set<T>(), guard = this.keyGuard(length);
    for (let i = 0; i < length; i++) values.add(guard === null ? read(this) : guardKey(guard, read(this)));
    if (values.size !== length) throw this.duplicateKey();
    return values;
  }

  /**
   * The guard against hash flooding (`NinoKeyGuard`) for a map or set of `length` number, bigint or string keys read
   * from the data, or null for one of up to 128 keys, which cannot be flooded far. Its table spends the read's presize
   * budget (`capacity`): past it (data that claims more than it holds) it counts fewer, fuller buckets, so it is only
   * stricter.
   */
  keyGuard(length: number): NinoKeyGuard | null {
    return length > guardedKeys ? new NinoKeyGuard(this.capacity(length)) : null;
  }

  /** A dictionary: length, then each key and value. A key read twice is invalid data, as in .NET (see `duplicateKey`). */
  map<K, V>(readKey: (reader: NinoReader) => K, readValue: (reader: NinoReader) => V): Map<K, V> | null {
    const length = this.length();
    if (length < 0) return null;
    const map = new Map<K, V>(), guard = this.keyGuard(length);
    for (let i = 0; i < length; i++) {
      const key = readKey(this);
      map.set(guard === null ? key : guardKey(guard, key), readValue(this));
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

  /**
   * The error for a C# union (wire-format spec §7.12) holding a type id that is not one of its cases, or a case's id
   * with a null value: .NET writes the empty union (type id 0) for a case given null.
   */
  invalidCase(id: number): NinoError {
    return new NinoError(`type id 0x${(id >>> 0).toString(16)} with this value is not a case of the union.`);
  }

  /** A nullable value, `Tuple` or sealed class: a presence byte, then the value if present. */
  nullable<T>(read: (reader: NinoReader) => T): T | null {
    return this.present() ? read(this) : null;
  }

  /** Reads a value with `codec` into `value` when the codec can (`NinoCodec.readInto`), else a new one. */
  into<T>(codec: NinoCodec<T>, value: T): T {
    return codec.readInto === undefined ? codec.read(this) : codec.readInto(this, value);
  }

  /** The presence byte of a nullable, `Tuple` or sealed class: true when a value follows (0 or 1). */
  present(): boolean {
    const flag = this.u8();
    if (flag > 1) throw new NinoError(`invalid presence byte ${flag}.`);
    return flag === 1;
  }

  /**
   * A version tolerant or keyed body (or a keyed member): its byte length. Returns the offset where its members end
   * (a section of deferred bodies may follow them).
   */
  beginRegion(): number {
    const length = this.i32();
    this.regions++;
    if (length >= 0 && length <= this.end - this.pos) return this.pos + length;
    return this.sectionedRegion(length);
  }

  /**
   * A region whose length has `sectionFlag` set: where its members end, before the section and its offset. Checks the
   * section as .NET, Go and Rust do, before any member is read: only the outermost region holds one (`beginRegion` has
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
  exitBody(depth: number): void {
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
