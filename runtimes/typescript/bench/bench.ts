// Nino's generated TypeScript against Protocol Buffers (protobuf-es), MessagePack (msgpackr) and JSON, on data
// written by Nino in .NET (Program.cs), timed by tinybench. Each value must read and write back to exactly the bytes
// .NET wrote before it is timed. Shared by the Node (node.ts) and browser (browser.mjs) runs.
//
// Inputs rotate: .NET writes each scenario in 64 variants (the same shapes and sizes, other content), every
// serializer gets all of them ready before the timing, and each pass of every task takes the next variant in turn.
// Repeating one input lets V8 reuse work: a repeated JSON text parses along the hidden-class transitions and
// internalized keys of the previous parse (a Lookup-like text took 0.21 ms a parse repeated, 7.8 to 8.4 ms with 200
// different texts in turn), which made JSON look faster than it is in deserialize.
//
// Each serializer runs on its own: its data is made from the variants, it runs its two tasks, and its data is dropped
// before the next one's is made (scenario says why).
import { equals, fromBinary, toBinary } from "@bufbuild/protobuf";
import { Packr } from "msgpackr";
import { Bench } from "tinybench";
import { deserialize, serialize, type NinoCodec } from "./gen/nino.ts";
import * as b from "./gen/Bench.ts";
import { protobuf } from "./protobuf.ts";

/** The serializers, in the order of the tables. */
export const serializers = ["Nino", "Protobuf", "MessagePack", "JSON"] as const;
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

/** A serializer's two tasks on its own data: what each pass runs, and the mean encoded size of a variant. */
type Tasks = { serialize: () => void; deserialize: () => void; bytes: number };

async function scenario<T>(name: string, file: string, data: Uint8Array, codec: NinoCodec<T>, options: Options):
  Promise<Row[]> {
  const bytes = variants(data);
  // Nino's values of every variant, checked to write back to .NET's bytes: each serializer's data is made from them.
  const values = () => bytes.map((variant, v) => variant.map((x, i) => {
    const value = deserialize(codec, x);
    if (!same(serialize(codec, value), x)) throw new Error(`${name}[${v}][${i}]: written bytes differ from .NET's`);
    return value;
  }));
  const [schema, convert] = protobuf(file);
  // JSON is timed to and from UTF-8 bytes, as every other format is: what goes over a wire or into a file.
  const encoder = new TextEncoder(), decoder = new TextDecoder();
  // Each serializer's data is made just before its tasks and dropped after them: while a serializer runs, only its
  // own data is live, as in an application that keeps its data in one form. With every serializer's 64 variants
  // live at once, each full GC marks four copies of the data, and output in ArrayBuffers starts full GCs: in Node 24
  // (V8 13.6) every 64 MB of new ArrayBuffers that no scavenge frees in between starts one (the external memory soft
  // limit), so a serializer that allocates little else on the heap paid for marking the other serializers' data.
  const make: Record<Serializer, () => Tasks> = {
    Nino: () => {
      const lists = values();
      return {
        serialize: rotate(lists, (l) => { for (const v of l) serialize(codec, v); }),
        deserialize: rotate(bytes, (l) => { for (const x of l) deserialize(codec, x); }), bytes: size(bytes),
      };
    },
    Protobuf: () => {
      const messages = values().map((list) => list.map((v) => convert(v as never)));
      const proto = messages.map((list) => list.map((m) => toBinary(schema, m)));
      proto.forEach((list, v) => list.forEach((x, i) => {
        if (!equals(schema, fromBinary(schema, x), messages[v][i]))
          throw new Error(`${name}[${v}][${i}]: protobuf round trip`);
      }));
      return {
        serialize: rotate(messages, (l) => { for (const m of l) toBinary(schema, m); }),
        deserialize: rotate(proto, (l) => { for (const x of l) fromBinary(schema, x); }), bytes: size(proto),
      };
    },
    MessagePack: () => {
      const lists = values(), packed = lists.map((list) => list.map((v) => packr.pack(v)));
      packed.forEach((list, v) => list.forEach((x, i) => {
        // Read back: the same data (maps come back as objects, which plain JSON makes of Maps too).
        if (JSON.stringify(plain(packr.unpack(x))) !== JSON.stringify(plain(lists[v][i])))
          throw new Error(`${name}[${v}][${i}]: MessagePack round trip`);
      }));
      return {
        serialize: rotate(lists, (l) => { for (const v of l) packr.pack(v); }),
        deserialize: rotate(packed, (l) => { for (const x of l) packr.unpack(x); }), bytes: size(packed),
      };
    },
    JSON: () => {
      const json = values().map((list) => list.map(plain));
      const utf8 = json.map((list) => list.map((j) => encoder.encode(JSON.stringify(j))));
      return {
        serialize: rotate(json, (l) => { for (const j of l) encoder.encode(JSON.stringify(j)); }),
        deserialize: rotate(utf8, (l) => { for (const x of l) JSON.parse(decoder.decode(x)); }), bytes: size(utf8),
      };
    },
  };

  const rows: Row[] = [];
  for (const serializer of serializers) {
    const tasks = make[serializer]();
    const bench = new Bench({ name, time: options.time ?? 1000, warmupTime: options.warmupTime ?? 250 });
    for (const op of ["serialize", "deserialize"] as const) bench.add(op, tasks[op]);
    await bench.run();
    for (const op of ["serialize", "deserialize"] as const) {
      const result = bench.getTask(op)!.result;
      if (result.state !== "completed") throw new Error(`${name}: ${op} ${serializer} ${result.state}`);
      const l = result.latency;
      rows.push({
        scenario: name, serializer, op, mean: l.mean, rme: l.rme, p75: l.p75, p99: l.p99, hz: 1000 / l.mean,
        samples: l.samplesCount, bytes: tasks.bytes, variants: bytes.length,
      });
    }
  }
  return rows;
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
