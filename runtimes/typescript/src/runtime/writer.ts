import { NinoDeferrals, inlineDepth, maxDepth, sectionFlag, tooDeep, type NinoDeferred } from "./deferrals.ts";
import { NinoError } from "./error.ts";
import { maxGridElements, maxGridLength } from "./grid.ts";
import { littleEndian, swapBytes, type NinoNumbers } from "./numbers.ts";
import { hexValues, maxDateMilliseconds, unixEpochMilliseconds } from "./scalars.ts";
import {
  headerSize, kernelAscii, kernelText, kernelWrite, kernels, loopText, nodeText, nonAscii, shortText, transfer,
  utf8Encoder, wasm, wide,
} from "./text.ts";

/** The smallest buffer `serialize` writes its results into (see `NinoWriter.next`). */
const slabSize = 64 * 1024;

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
    this.begin();
  }

  /**
   * Empties the writer, keeping its buffer: the next value is written from offset 0, and allocates nothing while it
   * fits (see `serializeInto`).
   */
  reset(): void {
    this.start = this.pos = 0;
    this.begin();
  }

  /** Starts a value: closes what a value that failed may have left open, and forgets the last value's references. */
  private begin(): void {
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
    this.i32(values.length);
    this.numberBlock(values);
  }

  /** Writes a block of fixed-size numbers without a collection length. */
  numberBlock(values: NinoNumbers): void {
    const bytes = values.byteLength;
    const at = this.reserve(bytes);
    this.buffer.set(new Uint8Array(values.buffer, values.byteOffset, bytes), at);
    if (!littleEndian) swapBytes(this.buffer, at, at + bytes, values.BYTES_PER_ELEMENT);
  }

  /** Writes exactly `rank` dimensions whose product equals the size of the rectangular array's flat data. */
  gridShape(rank: number, lengths: readonly number[], count: number): void {
    if (lengths.length !== rank) throw new NinoError("invalid grid rank.");
    let total = 1;
    for (const length of lengths) {
      if (!Number.isInteger(length) || length < 0 || length > maxGridLength)
        throw new NinoError(`invalid length ${length}.`);
      total *= length;
      if (total > maxGridElements) throw new NinoError("invalid grid dimensions.");
    }
    if (total !== count) throw new NinoError("grid dimensions do not match the data length.");
    for (const length of lengths) this.i32(length);
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
  beginRegion(): number {
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
  exitBody(depth: number): void {
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
