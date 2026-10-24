// The benchmark in Node: node node.ts <data directory> [scenario]
// With NINO_BENCH_JSON=<file>, the rows are also written to that file as JSON ({ node, rows }), for the release's
// report (benchmarks/runtimes_report.py).
import { readFile, writeFile } from "node:fs/promises";
import { join } from "node:path";
import { run, table } from "./bench.ts";

const [dir, only] = process.argv.slice(2);
const rows = await run(async (name) => new Uint8Array(await readFile(join(dir, name + ".bin"))), only);
console.log(table(rows));
if (process.env.NINO_BENCH_JSON)
  await writeFile(process.env.NINO_BENCH_JSON, JSON.stringify({ node: process.version, rows }, null, 2) + "\n");
