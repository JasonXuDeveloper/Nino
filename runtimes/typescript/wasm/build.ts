// Builds the WebAssembly kernels of src/nino.ts from wasm/text.wat and writes them into nino.ts, base64, between
// its `wasm:begin` and `wasm:end` comments: npm run build:wasm. test/wasm.test.ts checks that nino.ts holds what this
// builds. The assembler reads the subset of the WebAssembly text format text.wat is written in (folded instructions,
// named types, functions, locals, globals and labels; the core, SIMD and GC instructions in `ops`), so the build needs
// nothing but Node; an instruction it does not know is an error.
import { readFileSync, writeFileSync } from "node:fs";
import { fileURLToPath } from "node:url";

type Node = string | Node[];

/** The S-expressions of `source`, comments dropped. */
function parse(source: string): Node[] {
  const tokens = source.replace(/\(;[\s\S]*?;\)/g, " ").replace(/;;.*$/gm, " ")
    .match(/"[^"]*"|\(|\)|[^\s()"]+/g) ?? [];
  let i = 0;
  const list = (): Node[] => {
    const items: Node[] = [];
    while (i < tokens.length) {
      const t = tokens[i++];
      if (t === "(") items.push(list());
      else if (t === ")") return items;
      else items.push(t);
    }
    return items;
  };
  return list();
}

function uleb(n: number | bigint): number[] {
  let v = BigInt(n);
  const out: number[] = [];
  do {
    let b = Number(v & 0x7fn);
    v >>= 7n;
    if (v !== 0n) b |= 0x80;
    out.push(b);
  } while (v !== 0n);
  return out;
}

function sleb(n: number | bigint): number[] {
  let v = BigInt(n);
  const out: number[] = [];
  for (;;) {
    const b = Number(v & 0x7fn);
    v >>= 7n;
    if ((v === 0n && (b & 0x40) === 0) || (v === -1n && (b & 0x40) !== 0)) {
      out.push(b);
      return out;
    }
    out.push(b | 0x80);
  }
}

const utf8 = (s: string) => [...new TextEncoder().encode(s)];
const name = (s: string) => [...uleb(utf8(s).length), ...utf8(s)];
const vec = (items: number[][]) => [...uleb(items.length), ...items.flat()];
const unquote = (s: Node) => {
  if (typeof s !== "string" || !s.startsWith('"')) throw new Error(`expected a string, got ${JSON.stringify(s)}`);
  return s.slice(1, -1);
};

/**
 * Instructions: opcode bytes, then what follows them. "memarg:k" takes no immediate in the text (offset 0, alignment
 * 2^k); "lane" a lane index; "local", "global", "func", "type" and "label" a name.
 */
const ops: Record<string, [number[], ...string[]]> = {
  "block": [[0x02]], "loop": [[0x03]], "br": [[0x0c], "label"], "br_if": [[0x0d], "label"],
  "call": [[0x10], "func"], "drop": [[0x1a]],
  "local.get": [[0x20], "local"], "local.set": [[0x21], "local"], "global.get": [[0x23], "global"],
  "i32.load8_u": [[0x2d], "memarg:0"], "i32.load16_u": [[0x2f], "memarg:1"],
  "i32.store8": [[0x3a], "memarg:0"], "i32.store16": [[0x3b], "memarg:1"],
  "i32.const": [[0x41], "i32"],
  "i32.gt_u": [[0x4b]], "i32.ge_u": [[0x4f]], "i32.add": [[0x6a]], "i32.and": [[0x71]], "i32.shl": [[0x74]],
  "array.new_default": [[0xfb, 7], "type"], "array.get_u": [[0xfb, 13], "type"], "array.set": [[0xfb, 14], "type"],
  "v128.load": [[0xfd, 0x00], "memarg:4"], "v128.store": [[0xfd, 0x0b], "memarg:4"], "v128.const": [[0xfd, 0x0c], "v128"],
  "i8x16.extract_lane_u": [[0xfd, 0x16], "lane"], "i16x8.extract_lane_u": [[0xfd, 0x19], "lane"],
  "i16x8.replace_lane": [[0xfd, 0x1a], "lane"], "v128.and": [[0xfd, 0x4e]], "v128.any_true": [[0xfd, 0x53]],
  "v128.store64_lane": [[0xfd, 0x5b], "memarg:3", "lane"], "i8x16.narrow_i16x8_u": [[0xfd, 0x66]],
};

/** The binary module of `source`. */
export function assemble(source: string): Uint8Array {
  const [module] = parse(source);
  if (!Array.isArray(module) || module[0] !== "module") throw new Error("expected (module ...)");
  const fields = module.slice(1) as Node[][];

  // Types: named array types first (as declared), then each distinct function type.
  const types: number[][] = [], typeIndex = new Map<string, number>(), funcTypes = new Map<string, number>();
  const heap = (t: string) => {
    if (t === "extern") return [0x6f];
    const index = typeIndex.get(t);
    if (index === undefined) throw new Error(`unknown type ${t}`);
    return uleb(index);
  };
  const valtype = (t: Node): number[] => {
    if (t === "i32") return [0x7f];
    if (t === "v128") return [0x7b];
    if (t === "externref") return [0x6f];
    if (Array.isArray(t) && t[0] === "ref")
      return t[1] === "null" ? [0x63, ...heap(t[2] as string)] : [0x64, ...heap(t[1] as string)];
    throw new Error(`unknown value type ${JSON.stringify(t)}`);
  };
  for (const f of fields) {
    if (f[0] !== "type") continue;
    const def = f[2] as Node[];
    const field = def[1] as Node[];
    if (def[0] !== "array" || field[0] !== "mut" || field[1] !== "i16") throw new Error("only (array (mut i16)) types");
    typeIndex.set(f[1] as string, types.length);
    types.push([0x5e, 0x77, 0x01]);
  }
  /** A function's signature: its parameter names and the index of its type. */
  const signature = (parts: Node[]) => {
    const params: string[] = [], paramTypes: number[][] = [], results: number[][] = [];
    for (const p of parts) {
      if (!Array.isArray(p)) continue;
      if (p[0] === "param") {
        const named = typeof p[1] === "string" && p[1].startsWith("$");
        for (const t of named ? [p[2]] : p.slice(1)) {
          params.push(named ? p[1] as string : "");
          paramTypes.push(valtype(t));
        }
      } else if (p[0] === "result") {
        for (const t of p.slice(1)) results.push(valtype(t));
      }
    }
    const bytes = [0x60, ...vec(paramTypes), ...vec(results)], key = bytes.join(",");
    let index = funcTypes.get(key);
    if (index === undefined) {
      funcTypes.set(key, index = types.length);
      types.push(bytes);
    }
    return { params, type: index };
  };

  const funcIndex = new Map<string, number>(), globalIndex = new Map<string, number>();
  const imports: number[][] = [], functions: number[] = [], exports: number[][] = [], globals: number[][] = [];
  let memory: number[] | null = null;
  const bodies: { params: string[]; parts: Node[] }[] = [];
  for (const f of fields) {
    if (f[0] === "import") {
      const func = f[3] as Node[];
      if (func[0] !== "func") throw new Error("only function imports");
      funcIndex.set(func[1] as string, imports.length);
      imports.push([...name(unquote(f[1])), ...name(unquote(f[2])), 0x00, ...uleb(signature(func.slice(2)).type)]);
    }
  }
  for (const f of fields) {
    if (f[0] === "memory") {
      const [, exported, min, max] = f as [string, Node[], string, string];
      if (exported[0] !== "export") throw new Error("expected (memory (export ...) min max)");
      memory = [0x01, ...uleb(Number(min)), ...uleb(Number(max))];
      exports.push([...name(unquote(exported[1])), 0x02, 0x00]);
    } else if (f[0] === "global") {
      globalIndex.set(f[1] as string, globals.length);
      // Immutable, its initializer a constant expression (instructions without locals).
      globals.push([...valtype(f[2]), 0x00, ...expression([f[3]], new Map(), []), 0x0b]);
    } else if (f[0] === "func") {
      const index = imports.length + functions.length;
      let parts = f.slice(1);
      if (typeof parts[0] === "string") funcIndex.set(parts.shift() as string, index);
      if (Array.isArray(parts[0]) && parts[0][0] === "export") {
        exports.push([...name(unquote(parts[0][1])), 0x00, ...uleb(index)]);
        parts = parts.slice(1);
      }
      const { params, type } = signature(parts);
      functions.push(type);
      bodies.push({ params, parts });
    } else if (!["type", "import"].includes(f[0] as string)) {
      throw new Error(`unknown module field ${f[0]}`);
    }
  }

  /** The code of folded instructions `nodes`: each one's operands, then its opcode and immediates. */
  function expression(nodes: Node[], locals: Map<string, number>, labels: string[]): number[] {
    const out: number[] = [];
    for (const node of nodes) {
      if (!Array.isArray(node)) throw new Error(`expected a folded instruction, got ${node}`);
      const op = node[0] as string, spec = ops[op];
      if (spec === undefined) throw new Error(`unknown instruction ${op}`);
      const [code, ...immediates] = spec;
      if (op === "block" || op === "loop") {
        out.push(...code, 0x40, ...expression(node.slice(2), locals, [node[1] as string, ...labels]), 0x0b);
        continue;
      }
      const args = node.slice(1), operands: Node[] = [], imm: number[] = [];
      let a = 0;
      for (const kind of immediates) {
        if (kind.startsWith("memarg:")) {
          imm.push(...uleb(Number(kind.slice(7))), 0x00);
          continue;
        }
        const t = args[a++];
        if (typeof t !== "string") throw new Error(`${op}: expected an immediate`);
        const lookup = (map: Map<string, number>, what: string) => {
          const index = map.get(t);
          if (index === undefined) throw new Error(`${op}: unknown ${what} ${t}`);
          return uleb(index);
        };
        if (kind === "local") imm.push(...lookup(locals, "local"));
        else if (kind === "global") imm.push(...lookup(globalIndex, "global"));
        else if (kind === "func") imm.push(...lookup(funcIndex, "function"));
        else if (kind === "type") imm.push(...lookup(typeIndex, "type"));
        else if (kind === "lane") imm.push(Number(t));
        else if (kind === "i32") imm.push(...sleb(BigInt.asIntN(32, BigInt(t))));
        else if (kind === "label") {
          const depth = labels.indexOf(t);
          if (depth < 0) throw new Error(`${op}: unknown label ${t}`);
          imm.push(...uleb(depth));
        } else if (kind === "v128") {
          if (t !== "i16x8") throw new Error(`${op}: only i16x8 constants`);
          for (let k = 0; k < 8; k++) {
            const v = Number(args[a++]) & 0xffff;
            imm.push(v & 0xff, v >> 8);
          }
        }
      }
      operands.push(...args.slice(a));
      out.push(...expression(operands, locals, labels), ...code, ...imm);
    }
    return out;
  }

  const code = bodies.map(({ params, parts }) => {
    const locals = new Map<string, number>(params.map((p, i) => [p, i]));
    const declared: number[][] = [];
    let body: Node[] = [];
    for (const p of parts) {
      if (Array.isArray(p) && p[0] === "local") {
        locals.set(p[1] as string, locals.size);
        // Consecutive locals of one type are one entry: a count and the type.
        const type = valtype(p[2]), last = declared[declared.length - 1];
        if (last !== undefined && last.slice(1).join() === type.join()) last[0]++;
        else declared.push([1, ...type]);
      } else if (!(Array.isArray(p) && (p[0] === "param" || p[0] === "result"))) {
        body = parts.slice(parts.indexOf(p));
        break;
      }
    }
    const bytes = [...vec(declared), ...expression(body, locals, []), 0x0b];
    return [...uleb(bytes.length), ...bytes];
  });

  const section = (id: number, items: number[]) => [id, ...uleb(items.length), ...items];
  return Uint8Array.from([
    0x00, 0x61, 0x73, 0x6d, 0x01, 0x00, 0x00, 0x00,
    ...section(1, vec(types)),
    ...section(2, vec(imports)),
    ...section(3, vec(functions.map((t) => uleb(t)))),
    ...(memory === null ? [] : section(5, vec([memory]))),
    ...section(6, vec(globals)),
    ...section(7, vec(exports)),
    ...section(10, vec(code)),
  ]);
}

const here = (path: string) => fileURLToPath(new URL(path, import.meta.url));
export const watPath = here("text.wat"), runtimePath = here("../src/nino.ts");

/** The literal between nino.ts's markers: the module of text.wat, base64. */
export function literal(): string {
  return Buffer.from(assemble(readFileSync(watPath, "utf8"))).toString("base64");
}

/** The module nino.ts embeds now. */
export function embedded(): string {
  const match = /\/\/ wasm:begin\n\s*"([^"]*)";\n\s*\/\/ wasm:end/.exec(readFileSync(runtimePath, "utf8"));
  if (match === null) throw new Error("no wasm:begin/wasm:end markers in nino.ts");
  return match[1];
}

if (process.argv[1] !== undefined && fileURLToPath(import.meta.url) === process.argv[1]) {
  const runtime = readFileSync(runtimePath, "utf8"), base64 = literal();
  const next = runtime.replace(/(\/\/ wasm:begin\n\s*")[^"]*(";)/, (_, a: string, b: string) => a + base64 + b);
  writeFileSync(runtimePath, next);
  console.log(`wasm/text.wat: ${base64.length * 3 / 4 | 0} bytes${next === runtime ? ", unchanged" : ""}`);
}
