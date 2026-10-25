// Nino's generated TypeScript against Protocol Buffers (protobuf-es), MessagePack (msgpackr) and JSON, on data
// written by Nino in .NET (Program.cs), timed by tinybench. Each value must read and write back to exactly the bytes
// .NET wrote before it is timed. Shared by the Node (node.ts) and browser (browser.mjs) runs.
import { equals, fromBinary, toBinary } from "@bufbuild/protobuf";
import { Packr } from "msgpackr";
import { Bench } from "tinybench";
import { deserialize, serialize, type NinoCodec } from "./gen/nino.ts";
import * as b from "./gen/Bench.ts";
import { protobuf } from "./protobuf.ts";

/** The serializers, in the order of the tables. */
export const serializers = ["Nino", "Protobuf", "MessagePack", "JSON"] as const;
export type Serializer = (typeof serializers)[number];

/** One task's result: a pass over all of a scenario's values with one serializer, in one direction. */
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
  /** The encoded size of the scenario's values (bytes). */
  bytes: number;
}

/** tinybench's budget per task (milliseconds). */
export interface Options {
  time?: number;
  warmupTime?: number;
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

// Standard MessagePack (no msgpackr records extension); 64-bit integers read back as bigint, as Nino reads them.
// It writes the values Nino read as they are: classes and Maps as maps, bigints as 64-bit integers.
const packr = new Packr({ useRecords: false, int64AsType: "bigint" });

const size = (list: { length: number }[]) => list.reduce((n, x) => n + x.length, 0);

async function scenario<T>(name: string, file: string, data: Uint8Array, codec: NinoCodec<T>, options: Options):
  Promise<Row[]> {
  const bytes = values(data);
  const list = bytes.map((x, i) => {
    const value = deserialize(codec, x);
    if (!same(serialize(codec, value), x)) throw new Error(`${name}[${i}]: written bytes differ from .NET's`);
    return value;
  });
  const [schema, convert] = protobuf(file);
  const messages = list.map((v) => convert(v as never));
  const proto = messages.map((m) => toBinary(schema, m));
  proto.forEach((x, i) => {
    if (!equals(schema, fromBinary(schema, x), messages[i])) throw new Error(`${name}[${i}]: protobuf round trip`);
  });
  const packed = list.map((v) => packr.pack(v));
  const json = list.map(plain), text = json.map((j) => JSON.stringify(j));
  packed.forEach((x, i) => {
    // Read back: the same data (maps come back as objects, which plain JSON makes of Maps too).
    if (JSON.stringify(plain(packr.unpack(x))) !== text[i]) throw new Error(`${name}[${i}]: MessagePack round trip`);
  });
  // JSON is timed to and from UTF-8 bytes, as every other format is: what goes over a wire or into a file.
  const encoder = new TextEncoder(), decoder = new TextDecoder(), utf8 = text.map((s) => encoder.encode(s));

  const tasks: [Serializer, Row["op"], () => void, number][] = [
    ["Nino", "serialize", () => { for (const v of list) serialize(codec, v); }, size(bytes)],
    ["Protobuf", "serialize", () => { for (const m of messages) toBinary(schema, m); }, size(proto)],
    ["MessagePack", "serialize", () => { for (const v of list) packr.pack(v); }, size(packed)],
    ["JSON", "serialize", () => { for (const j of json) encoder.encode(JSON.stringify(j)); }, size(utf8)],
    ["Nino", "deserialize", () => { for (const x of bytes) deserialize(codec, x); }, size(bytes)],
    ["Protobuf", "deserialize", () => { for (const x of proto) fromBinary(schema, x); }, size(proto)],
    ["MessagePack", "deserialize", () => { for (const x of packed) packr.unpack(x); }, size(packed)],
    ["JSON", "deserialize", () => { for (const x of utf8) JSON.parse(decoder.decode(x)); }, size(utf8)],
  ];
  const bench = new Bench({ name, time: options.time ?? 1000, warmupTime: options.warmupTime ?? 250 });
  for (const [who, op, fn] of tasks) bench.add(`${op} ${who}`, fn);
  await bench.run();
  return tasks.map(([serializer, op, , encoded]) => {
    const result = bench.getTask(`${op} ${serializer}`)!.result;
    if (result.state !== "completed") throw new Error(`${name}: ${op} ${serializer} ${result.state}`);
    const l = result.latency;
    return {
      scenario: name, serializer, op, mean: l.mean, rme: l.rme, p75: l.p75, p99: l.p99, hz: 1000 / l.mean,
      samples: l.samplesCount, bytes: encoded,
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

/** The rows as a Markdown table: per scenario and operation, each serializer's mean ± rme, and the sizes. */
export function table(rows: Row[]): string {
  const ms = (r: Row | undefined) => (r ? `${r.mean.toFixed(3)} ± ${r.rme.toFixed(1)}%` : "");
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
