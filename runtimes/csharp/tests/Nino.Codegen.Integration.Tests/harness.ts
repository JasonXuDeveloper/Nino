// Run by TypeScriptTests with Node: reads each case's C# bytes with its codec, writes the value back and prints the
// bytes and the value as JSON, so the test compares them with what .NET wrote.
import { readFileSync } from "node:fs";
import { deserialize, serialize, type NinoCodec } from "./nino.ts";
import * as types from "./Nino.Codegen.Integration.Tests.ts";

const hex = (bytes: Uint8Array) => Buffer.from(bytes).toString("hex");
const json = (value: unknown) =>
  JSON.stringify(value, (_, v) =>
    typeof v === "bigint" ? v.toString()
    : v instanceof Map ? Object.fromEntries(v)
    : v instanceof Set ? [...v]
    : ArrayBuffer.isView(v) ? [...(v as unknown as Iterable<unknown>)]
    : v);

const cases = JSON.parse(readFileSync(process.argv[2], "utf8")) as { codec: string; hex: string }[];
// "typed" when the code was generated with --numeric-arrays typed (int[] is an Int32Array), else "plain".
const ints = (...values: number[]) => (process.argv[3] === "typed" ? Int32Array.from(values) : values) as never;
const codecs = types as unknown as Record<string, NinoCodec<unknown>>;
// A case that throws gives its error (bad data must throw a NinoError); a value too deep for JSON.stringify, no JSON.
const results = cases.map(({ codec, hex: input }) => {
  let value: unknown;
  try {
    value = deserialize(codecs[codec], Buffer.from(input, "hex"));
  } catch (e) {
    return { hex: "", json: "", error: `${(e as Error).name}: ${(e as Error).message}` };
  }
  let text = "";
  try {
    text = json(value);
  } catch {
    // Too deep to print.
  }
  return { hex: hex(serialize(codecs[codec], value)), json: text, error: "" };
});

// A value made in TypeScript, for .NET to read.
const player = new types.Player();
player.id.value = 42n;
player.name = "Zoë";
player.rank = types.Rank.Gold;
player.access = types.Access.Read | types.Access.Admin;
player.joined = new Date(Date.UTC(2026, 9, 18, 12, 30, 45, 123));
player.balance = "-12.345";
player.levels = ints(1, 2, 3);
player.inventory = new Map([["sword", ints(7)]]);
player.pair = [5, "five"];
const cat = new types.Cat();
cat.name = "Tom";
cat.lives = 9;
player.pet = cat;
const square = new types.Square();
square.area = 4;
square.side = 2;
player.shape = square;
const saved = new types.Saved();
saved.health = 77;
player.saved = saved;
results.push({ hex: hex(serialize(types.PlayerCodec, player)), json: "", error: "" });

// Reading keys like "__proto__" must not have touched any prototype.
if (Object.keys(Object.prototype).length !== 0 || ({} as { polluted?: unknown }).polluted !== undefined)
  throw new Error("Object.prototype was changed.");
console.log(JSON.stringify(results));
