// The cases of the .NET suite (runtimes/csharp/benchmarks) in TypeScript, on the bytes it wrote (generate.sh): for
// each, the code nino-gen generates (gen/model.ts) reads .NET's Nino bytes and must write them back, then every
// serializer that can express the case is timed by tinybench: Nino's generated code, Protocol Buffers (protobuf-es on
// the messages of the suite's .proto files, read from protobuf-net's bytes), MessagePack (msgpackr) and JSON. A case
// is found by its .NET type (ninoCodecOf), so a new C# case needs no edit here. Shared by the Node (node.ts) and
// browser (browser.mjs) runs.
//
// Inputs rotate: each case's value is made in up to 64 variants (the same shapes and sizes, other text: scramble),
// every serializer gets all of them ready before the timing, and each pass of every task takes the next variant in
// turn. Repeating one input lets V8 reuse work: a repeated JSON text parses along the hidden-class transitions and
// internalized keys of the previous parse (a Lookup-like text took 0.21 ms a parse repeated, 7.8 to 8.4 ms with 200
// different texts in turn), which made JSON look faster than it is in deserialize.
//
// Each serializer runs on its own: its data is made from the variants, it runs its tasks, and its data is dropped
// before the next one's is made (scenario says why).
import { equals, fromBinary, toBinary, type DescMessage, type Message } from "@bufbuild/protobuf";
import { Packr } from "msgpackr";
import { Bench } from "tinybench";
import { blockCopyProbe } from "./copy.ts";
import * as nino from "./gen/nino.ts";
import { deserialize, NinoWriter, serialize, type NinoCodec } from "./gen/nino.ts";
import { ninoCodecOf } from "./gen/model.ts";
import * as cases from "./gen/pb/cases/cases_pb.js";
import * as workloads from "./gen/pb/workloads/workloads_pb.js";

/** The serializers, in the order of the tables. */
export const serializers = ["Nino", "Protobuf", "MessagePack", "JSON"] as const;
export type Serializer = (typeof serializers)[number];
/**
 * The operations, in the order of the tables. `serializeInto` is Nino's serialize into one `NinoWriter` reset for each
 * value: no new `ArrayBuffer` per value (see `make`).
 */
export const operations = ["serialize", "serializeInto", "deserialize"] as const;
export type Operation = (typeof operations)[number];

/** One task's result: a pass over one of a case's variants with one serializer, in one direction. */
export interface Row {
  /** The case's name (the .NET suite's). */
  scenario: string;
  serializer: Serializer;
  op: Operation;
  /** Mean milliseconds per pass, and its relative margin of error (%, 95% confidence). */
  mean: number;
  rme: number;
  /** Percentiles of the passes (milliseconds). */
  p75: number;
  p99: number;
  /** Passes per second (1 / mean). */
  hz: number;
  samples: number;
  /** The encoded size of a variant (bytes, the mean over the variants). */
  bytes: number;
  /** How many variants of the value the passes took in turn. */
  variants: number;
}

/** tinybench's budget per task (milliseconds), and the cases to run. */
export interface Options {
  time?: number;
  warmupTime?: number;
  /** Only the cases cases.json marks quick (NINO_BENCH_QUICK=1). */
  quick?: boolean;
  /** Only the cases whose name matches (NINO_BENCH_CASES: names separated by `|`, `*` for any run of characters). */
  only?: string;
  /** Where a case that cannot run, or a serializer that cannot express a case, is reported. */
  log?: (line: string) => void;
}

/** A case of the .NET suite, as Nino.Benchmark --write-data lists it in cases.json. */
interface Case {
  case: string;
  /** The .NET type's runtime-form name, which the model looks its codec up by. */
  type: string;
  /** The Protocol Buffers message protobuf-net's bytes follow (cases.proto, workloads.proto), or null. */
  proto: string | null;
  quick: boolean;
}

/** The suite's messages by name, nino.cases' first where both packages have one (as the Go and Rust benches). */
const messages = new Map<string, DescMessage>();
for (const module of [cases, workloads])
  for (const value of Object.values(module) as { kind?: string; typeName?: string; name?: string }[])
    if (value?.kind === "message" && !messages.has(value.name!)) messages.set(value.name!, value as DescMessage);

/** The most variants a case is made in, and the bytes of Nino's variants that bound it (large cases take fewer). */
const maxVariants = 64, variantBudget = 16 << 20;

/** A task that passes over the next variant on each call, round robin. */
function rotate<T>(variants: T[], pass: (variant: T) => void): () => void {
  let next = 0;
  return () => {
    pass(variants[next]);
    next = next + 1 === variants.length ? 0 : next + 1;
  };
}

/** Rotates the letters of every string by `shift` within a-z and A-Z: the same lengths and encodings, other text. */
function rotateText(s: string, shift: number): string {
  let out = "";
  for (let i = 0; i < s.length; i++) {
    const c = s.charCodeAt(i);
    out += c >= 97 && c <= 122 ? String.fromCharCode(97 + (c - 97 + shift) % 26)
      : c >= 65 && c <= 90 ? String.fromCharCode(65 + (c - 65 + shift) % 26) : s[i];
  }
  return out;
}

/**
 * A copy of the value with its text rotated (rotateText): strings, Map keys and Set members, never object keys or the
 * `skip` members' values (protobuf-es's `$typeName` and a oneof's `case`). Classes keep their prototypes; numbers,
 * bigints, dates and typed arrays stay as they are. Rotating every string alike keeps distinct keys distinct.
 */
function scramble(v: unknown, shift: number, skip: Set<string>): unknown {
  if (typeof v === "string") return rotateText(v, shift);
  if (v === null || typeof v !== "object" || v instanceof Date || ArrayBuffer.isView(v)) return v;
  if (Array.isArray(v)) return v.map((x) => scramble(x, shift, skip));
  if (v instanceof Map) return new Map([...v].map(([k, x]) => [scramble(k, shift, skip), scramble(x, shift, skip)]));
  if (v instanceof Set) return new Set([...v].map((x) => scramble(x, shift, skip)));
  const o = Object.create(Object.getPrototypeOf(v));
  for (const k of Object.keys(v))
    o[k] = skip.has(k) ? (v as Record<string, unknown>)[k] : scramble((v as Record<string, unknown>)[k], shift, skip);
  return o;
}

/** The variants of a value: itself, then rotated copies that `valid` accepts (none when the first one fails). */
function variantsOf<T>(value: T, size: number, skip: Set<string>, valid: (v: T) => boolean): T[] {
  const count = Math.max(1, Math.min(maxVariants, Math.floor(variantBudget / Math.max(size, 1))));
  const out = [value];
  for (let i = 1; i < count; i++) {
    const v = scramble(value, i % 25 + 1, skip) as T;
    try {
      if (!valid(v)) break;
    } catch {
      break;
    }
    out.push(v);
  }
  return out;
}

/** The value as plain JSON data: 64-bit integers as strings, maps as objects, sets and typed arrays as arrays. */
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

/** The text a read-back is compared by: plain data, with NaN and the infinities kept (JSON writes them as null). */
const canonical = (v: unknown) =>
  JSON.stringify(plain(v), (_, x) => (typeof x === "number" && !Number.isFinite(x) ? String(x) : x));

/**
 * The value with its typed arrays (Nino's `int[]`, `double[]`...) as arrays and its sets as arrays: msgpackr writes a
 * typed array as the bytes of its buffer, not as standard MessagePack numbers, and MessagePack has no set. Byte
 * arrays stay.
 */
function untyped(v: unknown): unknown {
  if (ArrayBuffer.isView(v)) return v instanceof Uint8Array ? v : Array.from(v as unknown as ArrayLike<unknown>);
  if (v instanceof Set) return [...v].map(untyped);
  if (Array.isArray(v)) {
    const a = v.map(untyped);
    return a.some((x, i) => x !== v[i]) ? a : v;
  }
  if (v instanceof Map) {
    const entries = [...v], changed = entries.map(([k, x]) => [k, untyped(x)] as const);
    return changed.some(([, x], i) => x !== entries[i][1]) ? new Map(changed) : v;
  }
  if (v instanceof Date || v === null || typeof v !== "object") return v;
  const entries = Object.entries(v), changed = entries.map(([k, x]) => [k, untyped(x)] as const);
  return changed.some(([, x], i) => x !== entries[i][1])
    ? Object.assign(Object.create(Object.getPrototypeOf(v)), Object.fromEntries(changed)) : v;
}

function same(a: Uint8Array, b: Uint8Array): boolean {
  return a.length === b.length && a.every((x, i) => x === b[i]);
}

// Standard MessagePack (no msgpackr records extension); 64-bit integers read back as bigint, as Nino reads them.
// It writes the values Nino read as they are: classes and Maps as maps, bigints as 64-bit integers.
const packr = new Packr({ useRecords: false, int64AsType: "bigint" });

/** The mean encoded size of a variant (bytes). */
const size = (variants: { length: number }[]) =>
  Math.round(variants.reduce((n, x) => n + x.length, 0) / variants.length);

/** A serializer's tasks on its own data: what each pass runs, the mean encoded size of a variant and their count. */
type Tasks = { serialize: () => void; serializeInto?: () => void; deserialize: () => void; bytes: number; variants: number };

/** The members a scramble leaves alone in protobuf-es messages. */
const protoSkip = new Set(["$typeName", "case"]);

async function scenario(c: Case, data: Uint8Array, proto: Uint8Array | null, codec: NinoCodec<unknown>,
  options: Options): Promise<Row[]> {
  const log = options.log ?? (() => {});
  // Nino's value, checked to write back to .NET's bytes, and its variants, each read back to itself.
  const value = deserialize(codec, data);
  const variants = () => variantsOf(value, data.length, new Set(), (v) => {
    const bytes = serialize(codec, v);
    return same(serialize(codec, deserialize(codec, bytes)), bytes);
  });
  // JSON is timed to and from UTF-8 bytes, as every other format is: what goes over a wire or into a file.
  const encoder = new TextEncoder(), decoder = new TextDecoder();
  // Each serializer's data is made just before its tasks and dropped after them: while a serializer runs, only its
  // own data is live, as in an application that keeps its data in one form. With every serializer's 64 variants
  // live at once, each full GC marks four copies of the data, and output in ArrayBuffers starts full GCs: in Node 24
  // (V8 13.6) every 64 MB of new ArrayBuffers that no scavenge frees in between starts one (the external memory soft
  // limit), so a serializer that allocates little else on the heap paid for marking the other serializers' data.
  // Each returns its tasks, or why it cannot express the case (it fails, or reads back another value).
  const make: Record<Serializer, () => Tasks | string> = {
    Nino: () => {
      const values = variants(), bytes = values.map((v) => serialize(codec, v));
      // An application that writes each value out (to a socket, a file) before the next one, into one writer: once
      // the writer is large enough it allocates nothing, so no ArrayBuffers pile up toward V8's full GC (see above).
      const writer = new NinoWriter();
      return {
        serialize: rotate(values, (v) => serialize(codec, v)),
        serializeInto: rotate(values, (v) => {
          writer.reset();
          nino.serializeInto(codec, v, writer);
        }),
        deserialize: rotate(bytes, (x) => deserialize(codec, x)), bytes: size(bytes), variants: values.length,
      };
    },
    Protobuf: () => {
      if (!c.proto) return "no Protocol Buffers message (as in .NET: no Google.Protobuf rows)";
      const schema = messages.get(c.proto);
      if (!schema) return `no message ${c.proto} in gen/pb (generate.sh)`;
      if (!proto) return "no protobuf-net bytes";
      // The message protobuf-net's bytes hold, as an application that keeps its data in protobuf messages would.
      const message = fromBinary(schema, proto);
      if (!equals(schema, fromBinary(schema, toBinary(schema, message)), message)) return "reads back another message";
      const values = variantsOf(message, proto.length, protoSkip,
        (m) => equals(schema, fromBinary(schema, toBinary(schema, m)), m)) as Message[];
      const bytes = values.map((m) => toBinary(schema, m));
      return {
        serialize: rotate(values, (m) => toBinary(schema, m)),
        deserialize: rotate(bytes, (x) => fromBinary(schema, x)), bytes: size(bytes), variants: values.length,
      };
    },
    MessagePack: () => {
      const values = variants().map(untyped), packed = values.map((v) => packr.pack(v));
      // Read back: the same data (maps come back as objects, which plain data makes of Maps too).
      if (canonical(packr.unpack(packed[0])) !== canonical(values[0])) return "reads back another value";
      return {
        serialize: rotate(values, (v) => packr.pack(v)),
        deserialize: rotate(packed, (x) => packr.unpack(x)), bytes: size(packed), variants: values.length,
      };
    },
    JSON: () => {
      const values = variants().map(plain), utf8 = values.map((j) => encoder.encode(JSON.stringify(j)));
      if (canonical(JSON.parse(decoder.decode(utf8[0]))) !== canonical(value)) return "reads back another value";
      return {
        serialize: rotate(values, (j) => encoder.encode(JSON.stringify(j))),
        deserialize: rotate(utf8, (x) => JSON.parse(decoder.decode(x))), bytes: size(utf8), variants: values.length,
      };
    },
  };

  const rows: Row[] = [];
  for (const serializer of serializers) {
    let tasks: Tasks | string;
    try {
      tasks = make[serializer]();
    } catch (e) {
      tasks = `fails: ${(e as Error).message}`;
    }
    if (typeof tasks === "string") {
      log(`${c.case}/${serializer}: skipped: ${tasks}`);
      continue;
    }
    const bench = new Bench({ name: c.case, time: options.time ?? 1000, warmupTime: options.warmupTime ?? 250 });
    const t = tasks, ops = operations.filter((op) => t[op] !== undefined);
    for (const op of ops) bench.add(op, t[op]!);
    await bench.run();
    for (const op of ops) {
      const result = bench.getTask(op)!.result;
      if (result.state !== "completed") throw new Error(`${c.case}: ${op} ${serializer} ${result.state}`);
      const l = result.latency;
      rows.push({
        scenario: c.case, serializer, op, mean: l.mean, rme: l.rme, p75: l.p75, p99: l.p99, hz: 1000 / l.mean,
        samples: l.samplesCount, bytes: tasks.bytes, variants: tasks.variants,
      });
    }
  }
  return rows;
}

/**
 * The case filter (NINO_BENCH_CASES, as the Rust benchmark's): names separated by `|`, each matched as a whole, with
 * `*` for any run of characters. No regular expression is built from it.
 */
function matcher(pattern: string): (name: string) => boolean {
  const glob = (p: string, name: string): boolean => {
    const star = p.indexOf("*");
    if (star < 0) return p === name;
    const head = p.slice(0, star), tail = p.slice(star + 1);
    if (!name.startsWith(head)) return false;
    for (let i = head.length; i <= name.length; i++) if (glob(tail, name.slice(i))) return true;
    return false;
  };
  const alternatives = pattern.split("|");
  return (name) => alternatives.some((a) => glob(a, name));
}

/**
 * Runs the cases of cases.json that the model has a codec for, in its order, filtered by `options`; `read` gives a
 * file of the data directory, or null when there is none.
 */
export async function run(read: (name: string) => Promise<Uint8Array | null>, options: Options = {}): Promise<Row[]> {
  if (options.only === "BlockCopy") return blockCopyProbe(options);
  const log = options.log ?? (() => {});
  const list = await read("cases.json");
  if (!list) throw new Error("no cases.json: run generate.sh");
  const all = JSON.parse(new TextDecoder().decode(list)) as Case[];
  const only = options.only ? matcher(options.only) : null;
  const rows: Row[] = [];
  for (const c of all) {
    if ((options.quick && !c.quick) || (only && !only(c.case))) continue;
    const codec = ninoCodecOf(c.type);
    if (!codec) {
      log(`${c.case}: skipped: nino-gen writes no TypeScript codec for ${c.type} (generate.sh's warnings say why)`);
      continue;
    }
    const data = (await read(`${c.case}.nino.bin`))!;
    let back: Uint8Array;
    try {
      back = serialize(codec, deserialize(codec, data));
    } catch (e) {
      // An object holding a .NET type TypeScript has no form of (BclTypesTest's BigInteger, Uri...).
      if (c.type !== "System.Object") throw e;
      log(`${c.case}: skipped: an object holding a .NET type with no TypeScript form: ${(e as Error).message}`);
      continue;
    }
    // A value TypeScript keeps less of than .NET wrote: a DateTime is a Date, to the millisecond and without its kind.
    if (!same(back, data)) {
      log(`${c.case}: skipped: the generated TypeScript writes other bytes than .NET's (a DateTime as a Date loses its ` +
        `ticks below the millisecond and its kind)`);
      continue;
    }
    rows.push(...await scenario(c, data, await read(`${c.case}.protobuf.bin`), codec, options));
  }
  return rows;
}

/**
 * The rows as a Markdown table: per case and operation, each serializer's mean ± rme, and the sizes. Means have four
 * significant digits: the small cases take a few microseconds, where three decimals would round away differences of
 * several percent.
 */
export function table(rows: Row[]): string {
  const ms = (r: Row | undefined) => (r ? `${r.mean.toPrecision(4)} ± ${r.rme.toFixed(1)}%` : "");
  const lines = [
    `| Case | Operation | ${serializers.map((s) => s + " (ms)").join(" | ")} |`,
    `|---|---|${serializers.map(() => "--:|").join("")}`,
  ];
  for (const name of [...new Set(rows.map((r) => r.scenario))]) {
    const find = (op: string, s: string) => rows.find((r) => r.scenario === name && r.op === op && r.serializer === s);
    for (const op of operations)
      if (rows.some((r) => r.scenario === name && r.op === op)) lines.push(`| ${name} | ${op} | ${serializers.map((s) => ms(find(op, s))).join(" | ")} |`);
    const sizeOf = (s: string) => find("serialize", s);
    lines.push(`| ${name} | size (KiB) | ${serializers.map((s) => (sizeOf(s) ? (sizeOf(s)!.bytes / 1024).toFixed(1) : "")).join(" | ")} |`);
  }
  return lines.join("\n");
}
