// The benchmark in Node: node node.ts <data directory> [cases]   ([cases]: case names separated by |, * for any run)
// NINO_BENCH_CASES picks cases the same way; NINO_BENCH_QUICK=1 runs the quick ones at 100 ms per task with 25 ms of
// warm-up (the trend check a pull request labeled `benchmark` runs); NINO_BENCH_TIME sets tinybench's time per task in
// milliseconds (default 1000). With NINO_BENCH_JSON=<file>, the rows are also written to that file as JSON
// ({ node, tinybench, rows }), for the release's report (runtimes/csharp/benchmarks/reports/runtimes_report.py).
import { readFile, writeFile } from "node:fs/promises";
import { join } from "node:path";
import { run, table } from "./bench.ts";

const [dir, only = process.env.NINO_BENCH_CASES] = process.argv.slice(2);
const quick = process.env.NINO_BENCH_QUICK === "1";
const time = Number(process.env.NINO_BENCH_TIME) || (quick ? 100 : undefined);
// A quarter of the time per task as warm-up, at most tinybench's default 250 ms (quick mode: 25 ms).
const warmupTime = time === undefined ? undefined : Math.min(250, time / 4);
const read = async (name: string) => {
  try {
    return new Uint8Array(await readFile(join(dir, name)));
  } catch {
    return null;
  }
};
const rows = await run(read, { time, warmupTime, quick, only, log: (line) => console.error(line) });
console.log(table(rows));
if (process.env.NINO_BENCH_JSON) {
  const tinybench = JSON.parse(await readFile(new URL("node_modules/tinybench/package.json", import.meta.url), "utf8")).version;
  await writeFile(process.env.NINO_BENCH_JSON, JSON.stringify({ node: process.version, tinybench, rows }, null, 2) + "\n");
}
