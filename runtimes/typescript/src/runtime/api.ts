import { readValue, type NinoReader } from "./reader.ts";
import { NinoWriter } from "./writer.ts";

/**
 * The Nino version of this runtime: nino-gen's for the copy it writes next to the generated types (which expect the
 * same one), the package's for the npm package (scripts/version.mjs).
 */
export const ninoVersion = "__NINO_VERSION__";

/** Reads and writes one type's values. Generated for every [NinoType]. */
export interface NinoCodec<T> {
  read(reader: NinoReader): T;
  write(writer: NinoWriter, value: T): void;
  /**
   * Reads a value into `value` where it can (see `deserializeInto`) and returns the value read: `value` itself when it
   * was reused. Optional: a codec without it reads a new value.
   */
  readInto?(reader: NinoReader, value: T): T;
}

let shared: NinoWriter | null = null;

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

/** The writer `serializeInto` writes a `Uint8Array`'s value with: null while it is in use, or before the first call. */
let scratchWriter: NinoWriter | null = null;

/**
 * Serializes `value` with `codec` into memory the caller owns, and returns the value's size in bytes:
 *
 * - into a `NinoWriter`, after what it holds (what `NinoSerializer.Serialize(value, bufferWriter)` does in .NET),
 *   growing it as needed; `written()` views the bytes until the writer writes again. A writer reused with `reset` keeps
 *   its buffer, so once it is large enough writing allocates no memory at all: no new `ArrayBuffer` per value, which
 *   V8 counts toward a full garbage collection (about every 64 MB of them in Node 24) until a scavenge frees them. If
 *   `codec` throws, the writer is left as it was before the call.
 * - into a `Uint8Array`, from its start, when the value fits. A size larger than `target.length` means it did not:
 *   `target` is then unchanged, and a call with that many bytes succeeds.
 *
 * Not for a writer that is writing a value (from a custom codec): references and depth are counted per call.
 */
export function serializeInto<T>(codec: NinoCodec<T>, value: T, target: NinoWriter | Uint8Array): number {
  if (target instanceof NinoWriter) {
    const start = target.pos;
    target["begin"]();
    try {
      codec.write(target, value);
    } catch (e) {
      target.pos = start;
      throw e;
    }
    return target.pos - start;
  }
  // A codec that serializes (a custom one) while this runs gets a writer of its own.
  const writer = scratchWriter ?? new NinoWriter();
  scratchWriter = null;
  try {
    writer.reset();
    codec.write(writer, value);
    const size = writer.pos;
    if (size <= target.length) target.set(writer.written());
    return size;
  } finally {
    if (writer.capacity <= 1 << 20) scratchWriter = writer;
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

/**
 * Deserializes one value into `value`, as `NinoDeserializer.Deserialize(data, ref value)` does in .NET, and returns
 * the value read: the members of objects are read into the objects `value` holds (of the same class), arrays keep
 * their storage and read their elements into the old ones by index, maps and sets are cleared and refilled, and a
 * polymorphic member is read into only when the data holds the same class (tracked objects are read new). Bytes after
 * the value are not read. If it throws, `value` holds what was read before the error, the rest as it was.
 */
export function deserializeInto<T>(codec: NinoCodec<T>, data: Uint8Array, value: T): T {
  return readValue({ read: (r) => r.into(codec, value), write: codec.write }, data, false);
}

/**
 * A member that is .NET's `NinoNullable<T>` (`Nino.Core`): the value or `null`, as a `T?` member is (`T | null`), under
 * the name that says which bytes it has: its memory wherever it is, a presence and a value of a `T` each
 * (`NinoReader.ninoNullableAt`), where a `T?` is a presence byte and the value, or a slot in a sequence
 * (`NullableSlot`). An alias only: no wrapper object and nothing at run time.
 */
export type NinoNullable<T> = T | null;

/**
 * A .NET `T?` of a fixed-size `T` (a number, a date, a raw struct) as the element of a sequence: the value or `null`,
 * under the name that says which bytes it has: a slot of `T`'s alignment plus its size in every sequence, the presence
 * byte first, zeros up to the alignment, then the value, zeros when absent. `NullableSlot<number>[]` is an `int?[]`, a
 * `List<int?>` or a `Queue<int?>`, `Set<NullableSlot<number>>` a `HashSet<int?>`. A single `T?` (a member, a map's
 * key or value, a tuple's item) is not these bytes: it is a presence byte and the value only when present, declared
 * `T | null`. An alias only: no wrapper object and nothing at run time.
 */
export type NullableSlot<T> = T | null;
