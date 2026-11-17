// The benchmark in headless Chromium (Playwright): node browser.mjs <compiled directory> <data directory> [cases]
// The page loads the bundled bench.js (esbuild, run.sh) and the data from a route, so nothing else is served.
// [cases] or NINO_BENCH_CASES: case names separated by |, * for any run. NINO_BENCH_TIME: tinybench's time per task in
// milliseconds (NINO_BENCH_QUICK=1: the quick cases at 100).
import { readFile } from "node:fs/promises";
import { join } from "node:path";
import { createRequire } from "node:module";

// require, so a global Playwright is found through NODE_PATH (run.sh sets it).
const { chromium } = createRequire(import.meta.url)("playwright");

const [js, data, only = process.env.NINO_BENCH_CASES ?? ""] = process.argv.slice(2);
const browser = await chromium.launch();
const page = await browser.newPage();
await page.route("http://bench/**", async (route) => {
  const path = new URL(route.request().url()).pathname;
  if (path === "/") return route.fulfill({ contentType: "text/html", body: "<!doctype html><title>bench</title>" });
  const [root, name] = path.startsWith("/data/") ? [data, path.slice(6)] : [js, path.slice(1)];
  let body;
  try {
    body = await readFile(join(root, name));
  } catch {
    return route.fulfill({ status: 404 });
  }
  return route.fulfill({
    body,
    contentType: name.endsWith(".js") ? "text/javascript" : "application/octet-stream",
  });
});
await page.goto("http://bench/");
console.log(`${await browser.version()}`);
// Quick mode (NINO_BENCH_QUICK=1, the trend check a pull request labeled `benchmark` runs): 100 ms per task, 25 ms warm-up.
const quick = process.env.NINO_BENCH_QUICK === "1";
const time = Number(process.env.NINO_BENCH_TIME) || (quick ? 100 : undefined);
// A quarter of the time per task as warm-up, at most tinybench's default 250 ms (quick mode: 25 ms).
const warmupTime = time === undefined ? undefined : Math.min(250, time / 4);
page.on("console", (m) => console.error(m.text()));
console.log(await page.evaluate(async ([only, time, warmupTime, quick]) => {
  const { run, table } = await import("/bench.js");
  const read = async (name) => {
    const response = await fetch(`/data/${name}`);
    return response.ok ? new Uint8Array(await response.arrayBuffer()) : null;
  };
  return table(await run(read, { only, time, warmupTime, quick, log: (line) => console.log(line) }));
}, [only, time, warmupTime, quick]));
await browser.close();
