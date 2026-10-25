// Measurement only (A/B of ways to read the Lookup case's dictionaries into Maps): the codec nino-gen writes for
// Bench.Lookup (string -> int, int -> string), with the dictionaries built in other ways. Not for use.
import { NinoError, type NinoCodec, type NinoReader, type NinoWriter } from "./gen/nino.ts";

export class LookupM { counts: Map<string | null, number> | null = null; names: Map<number, string | null> | null = null; }

function writeCounts(w: NinoWriter, map: ReadonlyMap<string | null, number> | null): void {
  if (map === null || map === undefined) { w.i32(-1); return; }
  w.i32(map.size);
  for (const [key, value] of map) { w.string(key); w.i32(value); }
}
function writeNames(w: NinoWriter, map: ReadonlyMap<number, string | null> | null): void {
  if (map === null || map === undefined) { w.i32(-1); return; }
  w.i32(map.size);
  for (const [key, value] of map) { w.i32(key); w.string(value); }
}
function codec(readCounts: (r: NinoReader) => Map<string | null, number> | null,
  readNames: (r: NinoReader) => Map<number, string | null> | null, touch = false): NinoCodec<LookupM | null> {
  return {
    read: (r) => {
      if (!r.notNull()) return null;
      const v = new LookupM();
      v.counts = readCounts(r);
      v.names = readNames(r);
      // Use each map once (a lookup), as an application would: a lazy map builds its table here.
      if (touch) { v.counts?.has("x"); v.names?.has(-1); }
      return v;
    },
    write: (w, v) => {
      if (v === null || v === undefined) { w.u8(0); return; }
      w.u8(1);
      writeCounts(w, v.counts);
      writeNames(w, v.names);
    },
  };
}

// ---------------------------------------------------------------- has() then set(): two hash operations per entry
export const HasSetCodec = codec(
  (r) => {
    const length = r.length(); if (length < 0) return null;
    const map = new Map<string | null, number>();
    for (let i = 0; i < length; i++) { const key = r.string(); if (map.has(key)) throw r.duplicateKey(); map.set(key, r.i32()); }
    return map;
  },
  (r) => {
    const length = r.length(); if (length < 0) return null;
    const map = new Map<number, string | null>();
    for (let i = 0; i < length; i++) { const key = r.i32(); if (map.has(key)) throw r.duplicateKey(); map.set(key, r.string()); }
    return map;
  });

// ---------------------------------------------------------------- pairs, then new Map(pairs): V8 sizes the table from the array
export const PairsCodec = codec(
  (r) => {
    const length = r.length(); if (length < 0) return null;
    const pairs = new Array<[string | null, number]>(length);
    for (let i = 0; i < length; i++) { const key = r.string(); pairs[i] = [key, r.i32()]; }
    const map = new Map(pairs);
    if (map.size !== length) throw r.duplicateKey();
    return map;
  },
  (r) => {
    const length = r.length(); if (length < 0) return null;
    const pairs = new Array<[number, string | null]>(length);
    for (let i = 0; i < length; i++) { const key = r.i32(); pairs[i] = [key, r.string()]; }
    const map = new Map(pairs);
    if (map.size !== length) throw r.duplicateKey();
    return map;
  });

// ---------------------------------------------------------------- a cache of key strings by their Latin-1 bytes (as msgpackr's)
const slots = 4096;
const cacheBytes: (Uint8Array | null)[] = new Array(slots).fill(null);
const cacheText: string[] = new Array(slots).fill("");
function cachedKey(r: NinoReader): string | null {
  const start = r.pos;
  const header = r.view.getInt32(start, true);
  const length = ~header;
  if (header >= -1 || length > 16 || length > r.data.length - start - 4) return r.string();
  const data = r.data, at = start + 4;
  let h = length;
  for (let i = 0; i < length; i++) h = Math.imul(h ^ data[at + i], 0x01000193);
  const slot = (h ^ (h >>> 15)) & (slots - 1);
  const bytes = cacheBytes[slot];
  if (bytes !== null && bytes.length === length) {
    let i = 0;
    while (i < length && bytes[i] === data[at + i]) i++;
    if (i === length) { r.pos = at + length; return cacheText[slot]; }
  }
  const text = r.string()!;
  cacheBytes[slot] = data.slice(at, at + length);
  // Flat, so the cache keeps no larger string alive.
  cacheText[slot] = (" " + text).slice(1);
  return text;
}
export const KeyCacheCodec = codec(
  (r) => {
    const length = r.length(); if (length < 0) return null;
    const map = new Map<string | null, number>();
    for (let i = 0; i < length; i++) { const key = cachedKey(r); map.set(key, r.i32()); }
    if (map.size !== length) throw r.duplicateKey();
    return map;
  },
  (r) => {
    const length = r.length(); if (length < 0) return null;
    const map = new Map<number, string | null>();
    for (let i = 0; i < length; i++) { const key = r.i32(); map.set(key, r.string()); }
    if (map.size !== length) throw r.duplicateKey();
    return map;
  });

// ---------------------------------------------------------------- a Map that builds its table on first use
class LazyMap<K, V> extends Map<K, V> {
  private k: K[] | null;
  private v: V[] | null;
  constructor(k: K[], v: V[]) { super(); this.k = k; this.v = v; }
  private build(): void {
    const k = this.k!, v = this.v!;
    this.k = this.v = null;
    for (let i = 0; i < k.length; i++) super.set(k[i], v[i]);
  }
  override get size(): number { return this.k !== null ? this.k.length : super.size; }
  override get(key: K): V | undefined { if (this.k !== null) this.build(); return super.get(key); }
  override has(key: K): boolean { if (this.k !== null) this.build(); return super.has(key); }
  override set(key: K, value: V): this { if (this.k !== null) this.build(); return super.set(key, value); }
  override delete(key: K): boolean { if (this.k !== null) this.build(); return super.delete(key); }
  override clear(): void { this.k = this.v = null; super.clear(); }
  override forEach(f: (value: V, key: K, map: Map<K, V>) => void, self?: unknown): void { if (this.k !== null) this.build(); super.forEach(f, self); }
  override entries(): MapIterator<[K, V]> { if (this.k !== null) this.build(); return super.entries(); }
  override keys(): MapIterator<K> { if (this.k !== null) this.build(); return super.keys(); }
  override values(): MapIterator<V> { if (this.k !== null) this.build(); return super.values(); }
  override [Symbol.iterator](): MapIterator<[K, V]> { if (this.k !== null) this.build(); return super[Symbol.iterator](); }
}
// Duplicates found eagerly: an open-addressing table of indices (Int32Array), keys compared on a hit.
let table = new Int32Array(256);
function prepare(length: number): Int32Array {
  let size = 16;
  while (size < length * 2) size *= 2;
  if (table.length < size) table = new Int32Array(size);
  table.fill(0, 0, size);
  return table.length === size ? table : table.subarray(0, size);
}
function stringHash(s: string): number {
  let h = s.length;
  for (let i = 0; i < s.length; i++) h = Math.imul(h ^ s.charCodeAt(i), 0x01000193);
  return h ^ (h >>> 15);
}
function lazyCounts(r: NinoReader): Map<string | null, number> | null {
  const length = r.length(); if (length < 0) return null;
  const keys = new Array<string | null>(length), values = new Array<number>(length);
  const t = prepare(length), mask = t.length - 1;
  for (let i = 0; i < length; i++) {
    const key = r.string();
    if (key === null) throw new NinoError("a null key.");
    for (let s = stringHash(key) & mask; ; s = (s + 1) & mask) {
      const at = t[s];
      if (at === 0) { t[s] = i + 1; break; }
      if (keys[at - 1] === key) throw r.duplicateKey();
    }
    keys[i] = key;
    values[i] = r.i32();
  }
  return new LazyMap(keys, values);
}
function lazyNames(r: NinoReader): Map<number, string | null> | null {
  const length = r.length(); if (length < 0) return null;
  const keys = new Array<number>(length), values = new Array<string | null>(length);
  const t = prepare(length), mask = t.length - 1;
  for (let i = 0; i < length; i++) {
    const key = r.i32();
    let h = Math.imul(key, 0x9e3779b1);
    for (let s = (h ^ (h >>> 16)) & mask; ; s = (s + 1) & mask) {
      const at = t[s];
      if (at === 0) { t[s] = i + 1; break; }
      if (keys[at - 1] === key) throw r.duplicateKey();
    }
    keys[i] = key;
    values[i] = r.string();
  }
  return new LazyMap(keys, values);
}
export const LazyCodec = codec(lazyCounts, lazyNames);
export const LazyTouchedCodec = codec(lazyCounts, lazyNames, true);
// The generated Map read, touched the same way (the reference for LazyTouched).
export const MapTouchedCodec = codec(
  (r) => {
    const length = r.length(); if (length < 0) return null;
    const map = new Map<string | null, number>();
    for (let i = 0; i < length; i++) { const key = r.string(); map.set(key, r.i32()); }
    if (map.size !== length) throw r.duplicateKey();
    return map;
  },
  (r) => {
    const length = r.length(); if (length < 0) return null;
    const map = new Map<number, string | null>();
    for (let i = 0; i < length; i++) { const key = r.i32(); map.set(key, r.string()); }
    if (map.size !== length) throw r.duplicateKey();
    return map;
  }, true);

// ---------------------------------------------------------------- Node: transcoded into V8's serialization format, built by v8.deserialize
// (ValueDeserializer builds the Map in C++). Elsewhere (Chromium) the generated loop.
const v8 = (globalThis as { process?: { getBuiltinModule?: (id: string) => { deserialize(b: Uint8Array): unknown } } })
  .process?.getBuiltinModule?.("node:v8") ?? null;
let out = new Uint8Array(1 << 16);
function room(at: number, n: number): void {
  if (at + n <= out.length) return;
  const bigger = new Uint8Array(Math.max(out.length * 2, at + n));
  bigger.set(out);
  out = bigger;
}
function varint(at: number, v: number): number {
  while (v >= 0x80) { out[at++] = (v & 0x7f) | 0x80; v >>>= 7; }
  out[at++] = v;
  return at;
}
/** Copies the Nino string at the reader into `out` as a V8 string tag: '"' Latin-1, 'c' UTF-16, '_' null... */
function v8String(r: NinoReader, at: number): number {
  const header = r.i32();
  if (header === -1) { out[at++] = 0x30; return at; } // '0' null
  if (header < 0) {
    const length = ~header, from = r.take(length);
    room(at, length + 6);
    out[at++] = 0x22; // '"' one-byte string
    at = varint(at, length);
    out.set(r.data.subarray(from, from + length), at);
    return at + length;
  }
  if (header > (r.data.length - r.pos) >> 1) throw new NinoError(`invalid length ${header}.`);
  const from = r.take(header * 2);
  room(at, header * 2 + 7);
  // V8 aligns two-byte strings with padding tags ('\0').
  if (((at + 1 + (header * 2 < 0x80 ? 1 : header * 2 < 0x4000 ? 2 : 3)) & 1) !== 0) out[at++] = 0;
  out[at++] = 0x63; // 'c' two-byte string
  at = varint(at, header * 2);
  out.set(r.data.subarray(from, from + header * 2), at);
  return at + header * 2;
}
function v8Int(at: number, v: number): number {
  out[at++] = 0x49; // 'I' int32, zigzag
  return varint(at, ((v << 1) ^ (v >> 31)) >>> 0);
}
function v8Map<M>(r: NinoReader, length: number, entry: (r: NinoReader, at: number) => number): M {
  room(0, 16);
  let at = 0;
  out[at++] = 0xff; out[at++] = 0x0f; // version 15
  out[at++] = 0x3b; // ';' begin Map
  for (let i = 0; i < length; i++) { room(at, 16); at = entry(r, at); }
  room(at, 8);
  out[at++] = 0x3a; // ':' end Map, then the number of keys and values
  at = varint(at, length * 2);
  const map = v8!.deserialize(out.subarray(0, at)) as Map<unknown, unknown>;
  if (map.size !== length) throw r.duplicateKey();
  return map as M;
}
export const V8Codec = v8 === null ? codec((r) => { const n = r.length(); return n < 0 ? null : loopCounts(r, n); }, (r) => loopNames(r)) : codec(
  (r) => {
    const length = r.length(); if (length < 0) return null;
    return v8Map(r, length, (r, at) => { at = v8String(r, at); return v8Int(at, r.i32()); });
  },
  (r) => {
    const length = r.length(); if (length < 0) return null;
    return v8Map(r, length, (r, at) => { at = v8Int(at, r.i32()); return v8String(r, at); });
  });

// ---------------------------------------------------------------- string -> int as JSON text (one TextDecoder call), JSON.parse,
// then a Map built lazily from the object (never shown to the caller). Falls back to the loop for UTF-16 keys and
// keys that are array indices (an object would put those first).
const decoder = new TextDecoder();
class ObjectMap extends LazyMap<string | null, number> {
  constructor(o: Record<string, number>, keys: string[]) {
    const values = new Array<number>(keys.length);
    for (let i = 0; i < keys.length; i++) values[i] = o[keys[i]];
    super(keys, values);
  }
}
let json = new Uint8Array(1 << 16);
function jsonCounts(r: NinoReader): Map<string | null, number> | null {
  const length = r.length(); if (length < 0) return null;
  const start = r.pos;
  let at = 0;
  json[at++] = 0x7b;
  for (let i = 0; i < length; i++) {
    const header = r.i32();
    if (header >= -1) { r.pos = start; return loopCounts(r, length); }
    const n = ~header, from = r.take(n), d = r.data;
    if (at + n * 6 + 20 > json.length) { const b = new Uint8Array(Math.max(json.length * 2, at + n * 6 + 20)); b.set(json); json = b; }
    if (i > 0) json[at++] = 0x2c;
    json[at++] = 0x22;
    for (let j = 0; j < n; j++) {
      const c = d[from + j];
      if (c >= 0x20 && c < 0x80 && c !== 0x22 && c !== 0x5c) json[at++] = c;
      else if (c >= 0x80) { json[at++] = 0xc0 | (c >> 6); json[at++] = 0x80 | (c & 0x3f); }
      else { json[at++] = 0x5c; json[at++] = 0x75; json[at++] = 0x30; json[at++] = 0x30; json[at++] = hexd(c >> 4); json[at++] = hexd(c & 15); }
    }
    json[at++] = 0x22;
    json[at++] = 0x3a;
    let v = r.i32();
    if (v < 0) { json[at++] = 0x2d; v = -v; }
    const s = at;
    do { json[at++] = 0x30 + (v % 10); v = Math.floor(v / 10); } while (v > 0);
    json.subarray(s, at).reverse();
  }
  json[at++] = 0x7d;
  const o = JSON.parse(decoder.decode(json.subarray(0, at))) as Record<string, number>;
  const keys = Object.keys(o);
  if (keys.length !== length) throw r.duplicateKey();
  // An array index key would be first in the object, out of the written order: read again with the loop.
  for (let i = 0; i < keys.length; i++) {
    const c = keys[i].charCodeAt(0);
    if (c >= 0x30 && c <= 0x39) { r.pos = start; return loopCounts(r, length); }
  }
  return new ObjectMap(o, keys);
}
function hexd(x: number): number { return x < 10 ? 0x30 + x : 0x57 + x; }
function loopCounts(r: NinoReader, length: number): Map<string | null, number> {
  const map = new Map<string | null, number>();
  for (let i = 0; i < length; i++) { const key = r.string(); map.set(key, r.i32()); }
  if (map.size !== length) throw r.duplicateKey();
  return map;
}
function loopNames(r: NinoReader): Map<number, string | null> | null {
  const length = r.length(); if (length < 0) return null;
  const map = new Map<number, string | null>();
  for (let i = 0; i < length; i++) { const key = r.i32(); map.set(key, r.string()); }
  if (map.size !== length) throw r.duplicateKey();
  return map;
}
export const JsonCodec = codec(jsonCounts, loopNames);
export const JsonTouchedCodec = codec(jsonCounts, loopNames, true);
