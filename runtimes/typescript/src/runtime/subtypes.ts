import type { NinoCodec } from "./api.ts";
import { NinoError } from "./error.ts";
import { NinoReader } from "./reader.ts";
import { NinoWriter } from "./writer.ts";

interface NinoEntry {
  id: number;
  read(reader: NinoReader): unknown;
  write(writer: NinoWriter, value: unknown): void;
}

/**
 * The [NinoType]s written behind a type id (non-sealed classes, interfaces, `object`): the generated code adds every
 * type it declares, by its `NinoTypeId`.
 */
export class NinoSubtypes {
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
