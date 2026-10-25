// npm run build:wasm: assembles wasm/text.wat with wasm-tools (https://github.com/bytecodealliance/wasm-tools, see
// the README) and writes the module into src/nino.ts, base64, between its `wasm:begin` and `wasm:end` comments, with
// the SHA-256 of the text.wat it came from. test/wasm.test.ts checks the two still match: by building again where
// wasm-tools is installed, else by the hash.
import { execFileSync } from "node:child_process";
import { createHash } from "node:crypto";
import { readFileSync, writeFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

const here = (path: string) => fileURLToPath(new URL(path, import.meta.url));
export const watPath = here("text.wat"), runtimePath = here("../src/nino.ts");

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
  const runtime = readFileSync(runtimePath, "utf8"), module = build(), source = sourceHash();
  if (!markers.test(runtime)) throw new Error("no wasm:begin, wasm:source and wasm:end markers in nino.ts");
  const next = runtime.replace(markers, (_, begin: string, _source: string, open: string, _module: string, end: string) =>
    begin + source + open + module + end);
  writeFileSync(runtimePath, next);
  console.log(`wasm/text.wat: ${Buffer.from(module, "base64").length} bytes${next === runtime ? ", unchanged" : ""}`);
}
