// npm run build:wasm: assembles wasm/text.wat with wasm-tools (https://github.com/bytecodealliance/wasm-tools,
// see the README) and writes the module and source SHA-256 between the markers in src/runtime/text.ts.
// npm then rebuilds src/nino.ts. The tests check the source hash and compare the rebuilt Wasm where wasm-tools
// is installed.
import { execFileSync } from "node:child_process";
import { createHash } from "node:crypto";
import { readFileSync, writeFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

const here = (path: string) => fileURLToPath(new URL(path, import.meta.url));
export const watPath = here("text.wat"), runtimePath = here("../src/nino.ts");
const modulePath = here("../src/runtime/text.ts");

const markers = /(\/\/ wasm:begin\n\s*\/\/ wasm:source sha256:)([0-9a-f]*)(\n\s*")([^"]*)(";\n\s*\/\/ wasm:end)/;

/** The module nino.ts embeds (base64), and the SHA-256 of the text.wat it was built from. */
export function embedded(): { module: string; source: string } {
  const match = markers.exec(readFileSync(runtimePath, "utf8"));
  if (match === null) throw new Error("no wasm:begin, wasm:source and wasm:end markers in nino.ts");
  return { module: match[4], source: match[2] };
}

/** The SHA-256 of text.wat now. */
export function sourceHash(): string {
  return createHash("sha256").update(readFileSync(watPath)).digest("hex");
}

/** The module of text.wat, base64: `wasm-tools parse`, then `strip --all` (its name section). Throws without it. */
export function build(): string {
  const parsed = execFileSync("wasm-tools", ["parse", watPath]);
  return execFileSync("wasm-tools", ["strip", "--all", "-"], { input: parsed }).toString("base64");
}

if (process.argv[1] !== undefined && fileURLToPath(import.meta.url) === process.argv[1]) {
  const text = readFileSync(modulePath, "utf8"), module = build(), source = sourceHash();
  if (!markers.test(text)) throw new Error("no wasm:begin, wasm:source and wasm:end markers in text.ts");
  const next = text.replace(markers, (_, begin: string, _source: string, open: string, _module: string, end: string) =>
    begin + source + open + module + end);
  if (next !== text) writeFileSync(modulePath, next);
  console.log(`wasm/text.wat: ${Buffer.from(module, "base64").length} bytes${next === text ? ", unchanged" : ""}`);
}
