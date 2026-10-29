// Nino's generated TypeScript against Protocol Buffers (protobuf-es), MessagePack (msgpackr) and JSON, on data
// written by Nino in .NET (Program.cs), timed by tinybench. Each value must read and write back to exactly the bytes
// .NET wrote before it is timed. Shared by the Node (node.ts) and browser (browser.mjs) runs.
//
// Inputs rotate: .NET writes each scenario in 64 variants (the same shapes and sizes, other content), every
// serializer gets all of them ready before the timing, and each pass of every task takes the next variant in turn.
// Repeating one input lets V8 reuse work: a repeated JSON text parses along the hidden-class transitions and
// internalized keys of the previous parse (a Lookup-like text took 0.21 ms a parse repeated, 7.8 to 8.4 ms with 200
// different texts in turn), which made JSON look faster than it is in deserialize.
import { equals, fromBinary, toBinary } from "@bufbuild/protobuf";
import { Packr } from "msgpackr";
import { Bench } from "tinybench";
import { deserialize, serialize, NinoWriter, type NinoCodec } from "./gen/nino.ts";
import * as b from "./gen/Bench.ts";
import { protobuf } from "./protobuf.ts";

/** The serializers, in the order of the tables. */
export const serializers = ["Nino", "Protobuf", "MessagePack", "JSON", "Touch", "Gather", "Ahead"] as const;
export type Serializer = (typeof serializers)[number];

/** One task's result: a pass over all of a scenario's values (one variant) with one serializer, in one direction. */
export interface Row {
  scenario: string;
  serializer: Serializer;
  op: "serialize" | "deserialize";
  /** Mean milliseconds per pass, and its relative margin of error (%, 95% confidence). */
  mean: number;
  rme: number;
  /** Percentiles of the passes (milliseconds). */
  p75: number;
  p99: number;
  /** Passes per second (1 / mean). */
  hz: number;
  samples: number;
  /** The encoded size of a pass's values (bytes, the mean over the variants). */
  bytes: number;
  /** How many variants of the values the passes took in turn. */
  variants: number;
}

/** tinybench's budget per task (milliseconds). */
export interface Options {
  time?: number;
  warmupTime?: number;
}

/**
 * The variants of a data file, each a list of values: -K for K variants, then per variant a count, then each value's
 * length and bytes. A file that starts with a count instead has one variant: the data of a commit before the rotation,
 * which an A/B (runtime-ab.yml) reads with this file's bench on its base side.
 */
function variants(file: Uint8Array): Uint8Array[][] {
  const view = new DataView(file.buffer, file.byteOffset, file.byteLength);
  let at = 0;
  const int = () => {
    at += 4;
    return view.getInt32(at - 4, true);
  };
  const first = int();
  if (first >= 0) at = 0; // the count of the only variant
  return Array.from({ length: Math.max(-first, 1) }, () => Array.from({ length: int() }, () => {
    const length = int();
    at += length;
    return file.slice(at - length, at);
  }));
}

/** A task that passes over the next variant on each call, round robin. */
function rotate<T>(variants: T[], pass: (variant: T) => void): () => void {
  let next = 0;
  return () => {
    pass(variants[next]);
    next = next + 1 === variants.length ? 0 : next + 1;
  };
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

// Standard MessagePack (no msgpackr records extension); 64-bit integers read back as bigint, as Nino reads them.
// It writes the values Nino read as they are: classes and Maps as maps, bigints as 64-bit integers.
const packr = new Packr({ useRecords: false, int64AsType: "bigint" });

/** The mean encoded size of a variant (bytes). */
const size = (variants: { length: number }[][]) =>
  Math.round(variants.reduce((n, list) => list.reduce((m, x) => m + x.length, n), 0) / variants.length);


// DIAGNOSTIC (experiment only): what reading the graph costs without writing, and two other write loops for Mesh.
let sink = 0;
function touchMesh(m: b.Mesh): number {
  let s = m.name!.length;
  for (const v of m.vertices!) s += v.x + v.y + v.z;
  for (const v of m.normals!) s += v.x + v.y + v.z;
  for (const i of m.indices!) s += i;
  return s;
}
function touchPlayer(p: b.Player): number {
  let s = p.level + p.score + (p.online ? 1 : 0) + p.name!.length + p.name!.charCodeAt(0) + (p.id > 0n ? 1 : 0);
  s += p.position.x + p.position.y + p.position.z;
  for (const u of p.unlocked!) s += u;
  for (const [k, v] of p.inventory!) s += k!.length + v;
  s += p.pet!.name!.length;
  return s;
}
function touchOrders(o: b.OrderBook): number {
  let s = 0;
  for (const x of o.orders!) s += x!.id + x!.quantity + x!.price + x!.status + (x!.paid ? 1 : 0) + x!.sku!.charCodeAt(0) + x!.customer!.charCodeAt(0);
  return s;
}
let scratch = new Float32Array(3 * 1024);
function gatherVec3(w: NinoWriter, values: b.Vec3[]): void {
  const n = values.length;
  w.i32(n);
  const at = w.reserve(n * 12);
  if (scratch.length < 3 * n) scratch = new Float32Array(3 * n);
  const s = scratch;
  for (let i = 0, j = 0; i < n; i++, j += 3) { const v = values[i]; s[j] = v.x; s[j + 1] = v.y; s[j + 2] = v.z; }
  new Uint8Array(w.view.buffer).set(new Uint8Array(s.buffer, 0, n * 12), at);
}
function aheadVec3(w: NinoWriter, values: b.Vec3[]): void {
  const n = values.length;
  w.i32(n);
  let at = w.reserve(n * 12), t = 0;
  const view = w.view;
  for (let i = 0; i < n; i++) {
    const v = values[i];
    t += values[i + 8 < n ? i + 8 : i].x;
    view.setFloat32(at, v.x, true); view.setFloat32(at + 4, v.y, true); view.setFloat32(at + 8, v.z, true);
    at += 12;
  }
  sink += t;
}
function ints(w: NinoWriter, values: number[]): void {
  w.i32(values.length);
  let at = w.reserve(values.length * 4);
  const view = w.view;
  for (let i = 0; i < values.length; i++, at += 4) view.setInt32(at, values[i], true);
}
const meshWith = (vec3: (w: NinoWriter, v: b.Vec3[]) => void): NinoCodec<b.Mesh> => ({
  read: () => { throw new Error("write only"); },
  write: (w, m) => { w.u8(1); w.string(m.name); vec3(w, m.vertices!); vec3(w, m.normals!); ints(w, m.indices!); },
});
const gatherCodec = meshWith(gatherVec3), aheadCodec = meshWith(aheadVec3);

async function scenario<T>(name: string, file: string, data: Uint8Array, codec: NinoCodec<T>, options: Options):
  Promise<Row[]> {
  // Every variant's values for every serializer, made and checked before the timing.
  const bytes = variants(data);
  const lists = bytes.map((variant, v) => variant.map((x, i) => {
    const value = deserialize(codec, x);
    if (!same(serialize(codec, value), x)) throw new Error(`${name}[${v}][${i}]: written bytes differ from .NET's`);
    return value;
  }));
  const [schema, convert] = protobuf(file);
  const messages = lists.map((list) => list.map((v) => convert(v as never)));
  const proto = messages.map((list) => list.map((m) => toBinary(schema, m)));
  proto.forEach((list, v) => list.forEach((x, i) => {
    if (!equals(schema, fromBinary(schema, x), messages[v][i]))
      throw new Error(`${name}[${v}][${i}]: protobuf round trip`);
  }));
  const packed = lists.map((list) => list.map((v) => packr.pack(v)));
  const json = lists.map((list) => list.map(plain)), text = json.map((list) => list.map((j) => JSON.stringify(j)));
  packed.forEach((list, v) => list.forEach((x, i) => {
    // Read back: the same data (maps come back as objects, which plain JSON makes of Maps too).
    if (JSON.stringify(plain(packr.unpack(x))) !== text[v][i])
      throw new Error(`${name}[${v}][${i}]: MessagePack round trip`);
  }));
  // JSON is timed to and from UTF-8 bytes, as every other format is: what goes over a wire or into a file.
  const encoder = new TextEncoder(), decoder = new TextDecoder();
  const utf8 = text.map((list) => list.map((s) => encoder.encode(s)));

  const tasks: [Serializer, Row["op"], () => void, number][] = [
    ["Nino", "serialize", rotate(lists, (l) => { for (const v of l) serialize(codec, v); }), size(bytes)],
    ["Protobuf", "serialize", rotate(messages, (l) => { for (const m of l) toBinary(schema, m); }), size(proto)],
    ["MessagePack", "serialize", rotate(lists, (l) => { for (const v of l) packr.pack(v); }), size(packed)],
    ["JSON", "serialize", rotate(json, (l) => { for (const j of l) encoder.encode(JSON.stringify(j)); }), size(utf8)],
    ["Nino", "deserialize", rotate(bytes, (l) => { for (const x of l) deserialize(codec, x); }), size(bytes)],
    ["Protobuf", "deserialize", rotate(proto, (l) => { for (const x of l) fromBinary(schema, x); }), size(proto)],
    ["MessagePack", "deserialize", rotate(packed, (l) => { for (const x of l) packr.unpack(x); }), size(packed)],
    ["JSON", "deserialize", rotate(utf8, (l) => { for (const x of l) JSON.parse(decoder.decode(x)); }), size(utf8)],
  ];
  const touch = file === "mesh" ? touchMesh : file === "player" ? touchPlayer : file === "orders" ? touchOrders : null;
  if (touch) tasks.push(["Touch", "serialize", rotate(lists, (l) => { let t = 0; for (const v of l) t += touch(v as never); sink += t; }), 0]);
  if (file === "mesh") {
    for (const [who, c] of [["Gather", gatherCodec], ["Ahead", aheadCodec]] as const) {
      lists.forEach((list) => list.forEach((v) => {
        if (!same(serialize(c, v as never), serialize(codec, v))) throw new Error(`${who}: bytes differ`);
      }));
      tasks.push([who, "serialize", rotate(lists, (l) => { for (const v of l) serialize(c, v as never); }), size(bytes)]);
    }
  }
  const bench = new Bench({ name, time: options.time ?? 1000, warmupTime: options.warmupTime ?? 250 });
  for (const [who, op, fn] of tasks) bench.add(`${op} ${who}`, fn);
  await bench.run();
  if (sink === 0.5) console.log(sink);
  return tasks.map(([serializer, op, , encoded]) => {
    const result = bench.getTask(`${op} ${serializer}`)!.result;
    if (result.state !== "completed") throw new Error(`${name}: ${op} ${serializer} ${result.state}`);
    const l = result.latency;
    return {
      scenario: name, serializer, op, mean: l.mean, rme: l.rme, p75: l.p75, p99: l.p99, hz: 1000 / l.mean,
      samples: l.samplesCount, bytes: encoded, variants: bytes.length,
    };
  });
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
  ["Strings 1000 (ASCII)", "strings", b.StringsCodec],
  ["Strings 1000 (ASCII, Latin-1, other)", "strings-mixed", b.StringsCodec],
] as [string, string, NinoCodec<never>][];

/** Runs the scenarios whose data file starts with `only` (all when empty). */
export async function run(read: (name: string) => Promise<Uint8Array>, only = "", options: Options = {}):
  Promise<Row[]> {
  const rows: Row[] = [];
  for (const [name, file, codec] of scenarios)
    if (file.startsWith(only)) rows.push(...await scenario(name, file, await read(file), codec, options));
  return rows;
}

/**
 * The rows as a Markdown table: per scenario and operation, each serializer's mean ± rme, and the sizes. Means have
 * four significant digits: the string scenarios take a few hundredths of a millisecond, where three decimals would
 * round away differences of several percent.
 */
export function table(rows: Row[]): string {
  const ms = (r: Row | undefined) => (r ? `${r.mean.toPrecision(4)} ± ${r.rme.toFixed(1)}%` : "");
  const lines = [
    `| Scenario | Operation | ${serializers.map((s) => s + " (ms)").join(" | ")} |`,
    `|---|---|${serializers.map(() => "--:|").join("")}`,
  ];
  for (const name of [...new Set(rows.map((r) => r.scenario))]) {
    const find = (op: string, s: string) => rows.find((r) => r.scenario === name && r.op === op && r.serializer === s);
    for (const op of ["serialize", "deserialize"])
      lines.push(`| ${name} | ${op} | ${serializers.map((s) => ms(find(op, s))).join(" | ")} |`);
    lines.push(`| ${name} | size (KiB) | ${serializers.map((s) => ((find("serialize", s)?.bytes ?? 0) / 1024).toFixed(1)).join(" | ")} |`);
  }
  return lines.join("\n");
}
