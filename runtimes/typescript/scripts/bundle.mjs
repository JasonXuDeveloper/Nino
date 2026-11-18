// Keep the runtime as one scope for V8 and one TypeScript file for nino-gen's embedded resource.
import { readFileSync, writeFileSync } from "node:fs";

const root = new URL("../src/", import.meta.url);
const readModule = (name) => readFileSync(new URL("runtime/" + name, root), "utf8");
const entry = readModule("index.ts");
const exports = new Map();
for (const [, names, file] of entry.matchAll(/^export \{([^}]+)\} from "\.\/([\w-]+\.ts)";$/gm)) {
  exports.set(file, new Set(names.split(",").map((name) => name.trim().replace(/^type /, ""))));
}

const modules = new Set();
const sections = [];
function append(file) {
  if (modules.has(file)) return;
  modules.add(file);
  const source = readModule(file);
  const imports = /^import (?:type )?\{[^}]+\} from "\.\/([\w-]+\.ts)";\n/gm;
  for (const [, dependency] of source.matchAll(imports)) append(dependency);

  // Preserve the original declarations and doc comments. Only the index's exports are public in the bundle.
  const body = source.replace(imports, "").replace(
    /^export ((?:const|let|type|interface|class|function) (\w+))/gm,
    (declaration, text, name) => exports.get(file)?.has(name) ? declaration : text,
  ).trim();
  if (/^(?:import |export (?!const |let |type |interface |class |function ))/m.test(body)) {
    throw new Error(`${file}: use named imports and declaration exports in the runtime bundle`);
  }
  sections.push(body);
}

for (const file of exports.keys()) append(file);
const header = `// Nino runtime for TypeScript (5.7 or later, for the typed arrays' buffer types).
// Generated from src/runtime/ by npm run build:bundle. nino-gen writes this file next to the generated types.`;
const bundle = header + "\n\n" + sections.join("\n\n") + "\n";
const path = new URL("nino.ts", root);

if (process.argv.includes("--check")) {
  if (readFileSync(path, "utf8") !== bundle) {
    throw new Error("src/nino.ts is out of date: run npm run build:bundle");
  }
} else {
  writeFileSync(path, bundle);
}
