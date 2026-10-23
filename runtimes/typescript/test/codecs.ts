// Codecs written as Nino.TypeScript generates them (see TypeScriptEmitter.cs), for the tests of the runtime: a class
// body counts the depth and is deferred past 128 open bodies, a version tolerant body is a region, a struct that can
// hold itself counts its nesting. The .NET tests (tests/Nino.TypeScript.Tests) check the generated code against the
// bytes .NET writes; these check the runtime on its own, malformed data included.
import { NinoReader, NinoWriter, type NinoCodec } from "../src/nino.ts";

/** A sealed class with a presence byte: `Chain { int Value; Chain Next; }`. */
export class Chain {
  value: number = 0;
  next: Chain | null = null;
}

export function readChain(r: NinoReader): Chain {
  const v = new Chain(), depth = r.depth;
  if (depth >= 128) return r.defer(v, readBody$Chain);
  r.depth = depth + 1;
  readBody$Chain(r, v);
  r.exit(depth);
  return v;
}

export function writeChain(w: NinoWriter, v: Chain): void {
  const depth = w.depth;
  if (depth >= 128) {
    w.defer(v, writeBody$Chain);
    return;
  }
  w.depth = depth + 1;
  writeBody$Chain(w, v);
  w.exit(depth);
}

function readBody$Chain(r: NinoReader, v: Chain): void {
  v.value = r.i32();
  v.next = r.notNull() ? readChain(r) : null;
}

function writeBody$Chain(w: NinoWriter, v: Chain): void {
  w.i32(v.value);
  if (v.next === null || v.next === undefined) w.u8(0);
  else {
    w.u8(1);
    writeChain(w, v.next);
  }
}

export const ChainCodec: NinoCodec<Chain | null> = {
  read: (r) => (r.notNull() ? readChain(r) : null),
  write: (w, v) => {
    if (v === null || v === undefined) w.u8(0);
    else {
      w.u8(1);
      writeChain(w, v);
    }
  },
};

/** The same, but built by its constructor in .NET (a record): never deferred, in place up to 512 bodies. */
export class Record {
  value: number = 0;
  next: Record | null = null;
}

function readRecord(r: NinoReader): Record {
  const v = new Record(), depth = r.depth;
  if (depth >= 128) return r.deep(v, readBody$Record);
  r.depth = depth + 1;
  readBody$Record(r, v);
  r.exit(depth);
  return v;
}

function writeRecord(w: NinoWriter, v: Record): void {
  const depth = w.depth;
  if (depth >= 128) {
    w.deep(v, writeBody$Record);
    return;
  }
  w.depth = depth + 1;
  writeBody$Record(w, v);
  w.exit(depth);
}

function readBody$Record(r: NinoReader, v: Record): void {
  v.value = r.i32();
  v.next = r.notNull() ? readRecord(r) : null;
}

function writeBody$Record(w: NinoWriter, v: Record): void {
  w.i32(v.value);
  if (v.next === null) w.u8(0);
  else {
    w.u8(1);
    writeRecord(w, v.next);
  }
}

export const RecordCodec: NinoCodec<Record | null> = {
  read: (r) => (r.notNull() ? readRecord(r) : null),
  write: (w, v) => {
    if (v === null) w.u8(0);
    else {
      w.u8(1);
      writeRecord(w, v);
    }
  },
};

/** A version tolerant class: its members in a region. `Tolerant { int Value; Tolerant Next; }`. */
export class Tolerant {
  value: number = 0;
  next: Tolerant | null = null;
}

function readTolerant(r: NinoReader): Tolerant {
  const v = new Tolerant(), depth = r.depth;
  if (depth >= 128) return r.defer(v, readBody$Tolerant);
  r.depth = depth + 1;
  readBody$Tolerant(r, v);
  r.exit(depth);
  return v;
}

function writeTolerant(w: NinoWriter, v: Tolerant): void {
  const depth = w.depth;
  if (depth >= 128) {
    w.defer(v, writeBody$Tolerant);
    return;
  }
  w.depth = depth + 1;
  writeBody$Tolerant(w, v);
  w.exit(depth);
}

function readBody$Tolerant(r: NinoReader, v: Tolerant): void {
  const end = r.region();
  if (r.pos < end) v.value = r.i32();
  if (r.pos < end) v.next = r.notNull() ? readTolerant(r) : null;
  r.endRegion(end);
}

function writeBody$Tolerant(w: NinoWriter, v: Tolerant): void {
  const start = w.region();
  w.i32(v.value);
  if (v.next === null) w.u8(0);
  else {
    w.u8(1);
    writeTolerant(w, v.next);
  }
  w.endRegion(start);
}

export const TolerantCodec: NinoCodec<Tolerant | null> = {
  read: (r) => (r.notNull() ? readTolerant(r) : null),
  write: (w, v) => {
    if (v === null) w.u8(0);
    else {
      w.u8(1);
      writeTolerant(w, v);
    }
  },
};

/** A struct that holds itself through a list: `Nest { int Value; List<Nest> Inner; }`. */
export class Nest {
  value: number = 0;
  inner: Nest[] | null = null;
}

function readNest(r: NinoReader): Nest {
  r.enterStruct();
  const v = new Nest();
  v.value = r.i32();
  v.inner = r.array(readNest);
  r.exitStruct();
  return v;
}

function writeNest(w: NinoWriter, v: Nest): void {
  w.enterStruct();
  w.i32(v.value);
  w.array(v.inner, writeNest);
  w.exitStruct();
}

export const NestCodec: NinoCodec<Nest> = { read: readNest, write: writeNest };

/** A chain `length` links long, valued 1 to `length`. */
export function chain(length: number): Chain {
  let head: Chain | null = null;
  for (let i = length; i > 0; i--) {
    const link = new Chain();
    link.value = i;
    link.next = head;
    head = link;
  }
  return head!;
}

export function tolerant(length: number): Tolerant {
  let head: Tolerant | null = null;
  for (let i = length; i > 0; i--) {
    const link = new Tolerant();
    link.value = i;
    link.next = head;
    head = link;
  }
  return head!;
}

/** The values of a chain, in order, without recursion. */
export function values(head: { value: number; next: unknown } | null): number[] {
  const list: number[] = [];
  for (let link = head; link !== null; link = link.next as typeof link) list.push(link.value);
  return list;
}

/** Bytes from hex digits (spaces ignored). */
export function hex(text: string): Uint8Array {
  return Uint8Array.from(text.replace(/\s+/g, "").match(/../g) ?? [], (b) => parseInt(b, 16));
}

export function toHex(bytes: Uint8Array): string {
  return Array.from(bytes, (b) => b.toString(16).padStart(2, "0")).join("");
}

/** Little-endian int32 bytes. */
export function int32(value: number): number[] {
  return [value & 0xff, (value >> 8) & 0xff, (value >> 16) & 0xff, (value >>> 24) & 0xff];
}
