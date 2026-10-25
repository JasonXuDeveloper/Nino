// Measurement only (A/B of Map against null-prototype objects for the Lookup case): the codec nino-gen writes for
// Bench.Lookup, with its dictionaries read into objects made by Object.create(null) instead of Maps.
import { NinoError, type NinoCodec, type NinoReader, type NinoWriter } from "./gen/nino.ts";

type Dict<V> = { [key: string]: V };
export class LookupObj { counts: Dict<number> | null = null; names: Dict<string | null> | null = null; }
export class LookupInt { counts: Map<string | null, number> | null = null; names: Dict<string | null> | null = null; }

function readCounts(r: NinoReader): Dict<number> | null {
  const length = r.length();
  if (length < 0) return null;
  const o: Dict<number> = Object.create(null);
  for (let i = 0; i < length; i++) {
    const key = r.string();
    if (key === null) throw new NinoError("a null key.");
    if (o[key] !== undefined) throw r.duplicateKey();
    o[key] = r.i32();
  }
  return o;
}
function readNames(r: NinoReader): Dict<string | null> | null {
  const length = r.length();
  if (length < 0) return null;
  const o: Dict<string | null> = Object.create(null);
  for (let i = 0; i < length; i++) {
    const key = r.i32();
    if (o[key] !== undefined) throw r.duplicateKey();
    o[key] = r.string();
  }
  return o;
}
function readCountsMap(r: NinoReader): Map<string | null, number> | null {
  const length = r.length();
  if (length < 0) return null;
  const map = new Map<string | null, number>();
  for (let i = 0; i < length; i++) {
    const key = r.string();
    map.set(key, r.i32());
  }
  if (map.size !== length) throw r.duplicateKey();
  return map;
}
function writeCounts(w: NinoWriter, o: Dict<number> | null): void {
  if (o === null || o === undefined) { w.i32(-1); return; }
  const keys = Object.keys(o);
  w.i32(keys.length);
  for (let i = 0; i < keys.length; i++) { const key = keys[i]; w.string(key); w.i32(o[key]); }
}
function writeNames(w: NinoWriter, o: Dict<string | null> | null): void {
  if (o === null || o === undefined) { w.i32(-1); return; }
  const keys = Object.keys(o);
  w.i32(keys.length);
  for (let i = 0; i < keys.length; i++) {
    const key = keys[i], n = +key | 0;
    if (String(n) !== key) throw new Error("not an int key");
    w.i32(n);
    w.string(o[key]);
  }
}
function writeCountsMap(w: NinoWriter, map: ReadonlyMap<string | null, number> | null): void {
  if (map === null || map === undefined) { w.i32(-1); return; }
  w.i32(map.size);
  for (const [key, value] of map) { w.string(key); w.i32(value); }
}

export const LookupObjCodec: NinoCodec<LookupObj | null> = {
  read: (r) => {
    if (!r.notNull()) return null;
    const v = new LookupObj();
    v.counts = readCounts(r);
    v.names = readNames(r);
    return v;
  },
  write: (w, v) => {
    if (v === null || v === undefined) { w.u8(0); return; }
    w.u8(1);
    writeCounts(w, v.counts);
    writeNames(w, v.names);
  },
};
export const LookupIntCodec: NinoCodec<LookupInt | null> = {
  read: (r) => {
    if (!r.notNull()) return null;
    const v = new LookupInt();
    v.counts = readCountsMap(r);
    v.names = readNames(r);
    return v;
  },
  write: (w, v) => {
    if (v === null || v === undefined) { w.u8(0); return; }
    w.u8(1);
    writeCountsMap(w, v.counts);
    writeNames(w, v.names);
  },
};
