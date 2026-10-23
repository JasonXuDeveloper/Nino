// The benchmark in Node: node node.ts <data directory> [scenario]
import { readFile } from "node:fs/promises";
import { join } from "node:path";
import { run, table } from "./bench.ts";

const [dir, only] = process.argv.slice(2);
console.log(table(await run(async (name) => new Uint8Array(await readFile(join(dir, name + ".bin"))), only)));
