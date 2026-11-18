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
export const littleEndian = new Uint8Array(new Uint16Array([1]).buffer)[0] === 1;

/** Reverses the bytes of each `size`-byte element in `bytes` from `at` to `end`: Nino's order and a big-endian host's. */
export function swapBytes(bytes: Uint8Array, at: number, end: number, size: number): void {
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
export const numbersPooled = 8192;
const numbersSlab = 64 * 1024;
export let numbersBuffer = new ArrayBuffer(0);
export let numbersBytes = new Uint8Array(0);
let numbersPos = 0;

/** Where `bytes` bytes start in the shared buffer (8-byte aligned, as a view must be aligned to its elements). */
export function poolNumbers(bytes: number): number {
  let at = numbersPos;
  if (bytes > numbersBytes.length - at) {
    numbersBuffer = new ArrayBuffer(numbersSlab);
    numbersBytes = new Uint8Array(numbersBuffer);
    at = 0;
  }
  numbersPos = (at + bytes + 7) & ~7;
  return at;
}
