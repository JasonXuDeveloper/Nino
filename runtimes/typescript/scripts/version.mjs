// After `tsc` (npm run build): writes the package's version over the placeholder in dist, as nino-gen writes its
// own version over it in the copy it puts next to generated code (src/Nino.Codegen/Targets/TypeScriptTarget.cs).
import { readFileSync, writeFileSync } from "node:fs";

const root = new URL("../", import.meta.url);
const { version } = JSON.parse(readFileSync(new URL("package.json", root), "utf8"));
for (const file of ["dist/nino.js", "dist/nino.d.ts"]) {
  const path = new URL(file, root), text = readFileSync(path, "utf8");
  if (!text.includes("__NINO_VERSION__")) throw new Error(`${file}: no __NINO_VERSION__ to replace`);
  writeFileSync(path, text.replaceAll("__NINO_VERSION__", version));
}
