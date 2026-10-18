// Nino's generated TypeScript against JSON, on data written by Nino in .NET (Program.cs). Each value must read and
// write back to exactly the bytes .NET wrote before it is timed. Shared by the Node (node.ts) and browser
// (browser.mjs) runs.
import { deserialize, serialize, type NinoCodec } from "./gen/nino.ts";
import * as b from "./gen/Bench.ts";

/** A scenario's result: milliseconds per pass over all its values. */
export interface Row {
  name: string;
  serialize: number;
  stringify: number;
  deserialize: number;
  parse: number;
  ninoBytes: number;
  jsonBytes: number;
}

/** The values of a data file: a count, then each value's length and bytes. */
function values(file: Uint8Array): Uint8Array[] {
  const view = new DataView(file.buffer, file.byteOffset, file.byteLength);
  const list: Uint8Array[] = [];
  for (let i = 0, count = view.getInt32(0, true), at = 4; i < count; i++) {
    const length = view.getInt32(at, true);
    list.push(file.slice(at + 4, at + 4 + length));
    at += 4 + length;
  }
  return list;
}

/** The value as plain JSON data: 64-bit integers as strings, maps as objects, typed arrays as arrays. */
function plain(v: unknown): unknown {
  if (typeof v === "bigint") return v.toString();
  if (v instanceof Map) {
    const o: Record<string, unknown> = {};
    for (const [k, x] of v) o[String(k)] = plain(x);
    return o;
  }
  if (v instanceof Set || ArrayBuffer.isView(v)) return [...(v as Iterable<unknown>)].map(plain);
  if (Array.isArray(v)) return v.map(plain);
  if (v instanceof Date || v === null || typeof v !== "object") return v;
  const o: Record<string, unknown> = {};
  for (const k of Object.keys(v)) o[k] = plain((v as Record<string, unknown>)[k]);
  return o;
}

function same(a: Uint8Array, b: Uint8Array): boolean {
  return a.length === b.length && a.every((x, i) => x === b[i]);
}

/** The median milliseconds of one call of `fn`, over 15 samples of at least 40 ms. */
function time(fn: () => void): number {
  let reps = 1;
  for (;;) {
    const start = performance.now();
    for (let i = 0; i < reps; i++) fn();
    if (performance.now() - start > 40) break;
    reps *= 2;
  }
  const samples: number[] = [];
  for (let s = 0; s < 15; s++) {
    const start = performance.now();
    for (let i = 0; i < reps; i++) fn();
    samples.push((performance.now() - start) / reps);
  }
  samples.sort((x, y) => x - y);
  return samples[samples.length >> 1];
}

function scenario<T>(name: string, file: Uint8Array, codec: NinoCodec<T>): Row {
  const bytes = values(file);
  const list = bytes.map((x, i) => {
    const value = deserialize(codec, x);
    if (!same(serialize(codec, value), x)) throw new Error(`${name}[${i}]: written bytes differ from .NET's`);
    return value;
  });
  const json = list.map(plain), text = json.map((j) => JSON.stringify(j));
  return {
    name,
    serialize: time(() => { for (const v of list) serialize(codec, v); }),
    stringify: time(() => { for (const j of json) JSON.stringify(j); }),
    deserialize: time(() => { for (const x of bytes) deserialize(codec, x); }),
    parse: time(() => { for (const s of text) JSON.parse(s); }),
    ninoBytes: bytes.reduce((n, x) => n + x.length, 0),
    jsonBytes: text.reduce((n, s) => n + new TextEncoder().encode(s).length, 0),
  };
}

/** The scenarios: their names, data files and codecs. */
const scenarios: [string, string, NinoCodec<never>][] = [
  ["Player x1000", "player", b.PlayerCodec],
  ["Zoo 50 x 100 animals (type ids)", "zoo", b.ZooCodec],
  ["OrderBook 100 x 50 orders", "orders", b.OrderBookCodec],
  ["Article x200 (text)", "articles", b.ArticleCodec],
  ["Mesh 20 x (2 x 500 Vec3, 1500 int)", "mesh", b.MeshCodec],
  ["Samples 20 x (3 x 1000 numbers)", "samples", b.SamplesCodec],
  ["Lookup 50 x (2 maps of 100)", "lookup", b.LookupCodec],
  ["Event x1000 (long)", "events", b.EventCodec],
] as [string, string, NinoCodec<never>][];

/** Runs the scenarios whose data file starts with `only` (all when empty). */
export async function run(read: (name: string) => Promise<Uint8Array>, only = ""): Promise<Row[]> {
  const rows: Row[] = [];
  for (const [name, file, codec] of scenarios)
    if (file.startsWith(only)) rows.push(scenario(name, await read(file), codec));
  return rows;
}

/** The rows as a Markdown table. */
export function table(rows: Row[]): string {
  const ms = (x: number) => x.toFixed(3), ratio = (json: number, nino: number) => (json / nino).toFixed(1) + "x";
  const kib = (n: number) => (n / 1024).toFixed(0);
  return [
    "| Scenario | Serialize (ms) | JSON.stringify (ms) | | Deserialize (ms) | JSON.parse (ms) | | Size (KiB) | JSON (KiB) |",
    "|---|--:|--:|--:|--:|--:|--:|--:|--:|",
    ...rows.map((r) => `| ${r.name} | ${ms(r.serialize)} | ${ms(r.stringify)} | ${ratio(r.stringify, r.serialize)} | ` +
      `${ms(r.deserialize)} | ${ms(r.parse)} | ${ratio(r.parse, r.deserialize)} | ${kib(r.ninoBytes)} | ${kib(r.jsonBytes)} |`),
  ].join("\n");
}
