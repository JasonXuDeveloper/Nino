// The benchmark in Node: node node.ts <data directory> [scenario]
// NINO_BENCH_TIME sets tinybench's time per task in milliseconds (default 1000; NINO_BENCH_QUICK=1: 100). With NINO_BENCH_JSON=<file>, the
// rows are also written to that file as JSON ({ node, tinybench, rows }), for the release's report
// (runtimes/csharp/benchmarks/reports/runtimes_report.py).
import { readFile, writeFile } from "node:fs/promises";
import { join } from "node:path";
import { run, table } from "./bench.ts";

const [dir, only] = process.argv.slice(2);
// Quick mode (NINO_BENCH_QUICK=1, the trend check a pull request labeled `benchmark` runs): 100 ms per task, 25 ms warm-up.
const quick = process.env.NINO_BENCH_QUICK === "1";
const time = Number(process.env.NINO_BENCH_TIME) || (quick ? 100 : undefined);
const warmupTime = quick ? 25 : undefined;
const rows = await run(async (name) => new Uint8Array(await readFile(join(dir, name + ".bin"))), only, { time, warmupTime });
console.log(table(rows));
if (process.env.NINO_BENCH_JSON) {
  const tinybench = JSON.parse(await readFile(new URL("node_modules/tinybench/package.json", import.meta.url), "utf8")).version;
  await writeFile(process.env.NINO_BENCH_JSON, JSON.stringify({ node: process.version, tinybench, rows }, null, 2) + "\n");
}
