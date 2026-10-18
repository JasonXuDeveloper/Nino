// Run by TypeScriptTests with Node: reads each case's C# bytes with its codec, writes the value back and prints the
// bytes and the value as JSON, so the test compares them with what .NET wrote.
import { readFileSync } from "node:fs";
import { deserialize, serialize, type NinoCodec } from "./nino.ts";
import * as types from "./Nino.TypeScript.Tests.ts";

const hex = (bytes: Uint8Array) => Buffer.from(bytes).toString("hex");
const json = (value: unknown) =>
  JSON.stringify(value, (_, v) =>
    typeof v === "bigint" ? v.toString()
    : v instanceof Map ? Object.fromEntries(v)
    : v instanceof Set ? [...v]
    : v instanceof Uint8Array ? [...v]
    : v);

const cases = JSON.parse(readFileSync(process.argv[2], "utf8")) as { codec: string; hex: string }[];
const codecs = types as unknown as Record<string, NinoCodec<unknown>>;
const results = cases.map(({ codec, hex: input }) => {
  const value = deserialize(codecs[codec], Buffer.from(input, "hex"));
  return { hex: hex(serialize(codecs[codec], value)), json: json(value) };
});

// A value made in TypeScript, for .NET to read.
const player = new types.Player();
player.id.value = 42n;
player.name = "Zoë";
player.rank = types.Rank.Gold;
player.access = types.Access.Read | types.Access.Admin;
player.joined = new Date(Date.UTC(2026, 9, 18, 12, 30, 45, 123));
player.balance = "-12.345";
player.levels = [1, 2, 3];
player.inventory = new Map([["sword", [7]]]);
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
results.push({ hex: hex(serialize(types.PlayerCodec, player)), json: "" });

console.log(JSON.stringify(results));
