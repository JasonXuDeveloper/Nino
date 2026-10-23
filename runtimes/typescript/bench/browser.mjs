// The benchmark in headless Chromium (Playwright): node browser.mjs <compiled directory> <data directory> [scenario]
// The page loads the compiled bench.js and the data from a route, so nothing else is served.
import { readFile } from "node:fs/promises";
import { join } from "node:path";
import { createRequire } from "node:module";

// require, so a global Playwright is found through NODE_PATH (run.sh sets it).
const { chromium } = createRequire(import.meta.url)("playwright");

const [js, data, only = ""] = process.argv.slice(2);
const browser = await chromium.launch();
const page = await browser.newPage();
await page.route("http://bench/**", async (route) => {
  const path = new URL(route.request().url()).pathname;
  if (path === "/") return route.fulfill({ contentType: "text/html", body: "<!doctype html><title>bench</title>" });
  const [root, name] = path.startsWith("/data/") ? [data, path.slice(6)] : [js, path.slice(1)];
  return route.fulfill({
    body: await readFile(join(root, name)),
    contentType: name.endsWith(".js") ? "text/javascript" : "application/octet-stream",
  });
});
await page.goto("http://bench/");
console.log(`${await browser.version()}`);
console.log(await page.evaluate(async (only) => {
  const { run, table } = await import("/bench.js");
  return table(await run(async (name) => new Uint8Array(await (await fetch(`/data/${name}.bin`)).arrayBuffer()), only));
}, only));
await browser.close();
