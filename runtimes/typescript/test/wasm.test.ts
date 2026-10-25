// The WebAssembly kernels (wasm/text.wat): nino.ts embeds what wasm-tools builds from the source, and text read and
// written through them is the same as through the JavaScript paths they replace, for every length up to 256 and
// past the kernels' limit, with every kind of char at every position (unpaired surrogates included). The tests run
// on an instance of the runtime evaluated without `Buffer`, as a browser has it, with the kernels on and off.
import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { execFileSync } from "node:child_process";
import { readFileSync } from "node:fs";
import { build, embedded, runtimePath, sourceHash, watPath } from "../wasm/embed.ts";
import { toHex, strHeader } from "./codecs.ts";

type Runtime = typeof import("../src/nino.ts");

function hasWasmTools(): boolean {
  try {
    execFileSync("wasm-tools", ["--version"], { stdio: "ignore" });
    return true;
  } catch {
    return false;
  }
}

async function browser(): Promise<Runtime> {
  const global = globalThis as { Buffer?: unknown };
  const saved = global.Buffer;
  delete global.Buffer;
  try {
    return (await import(new URL("../src/nino.ts?wasm", import.meta.url).href)) as Runtime;
  } finally {
    global.Buffer = saved;
  }
}

/** The bytes Nino writes for `text` in the default encoding, built without the runtime. */
function expected(text: string): string {
  const units = Array.from({ length: text.length }, (_, i) => text.charCodeAt(i));
  if (units.every((u) => u <= 0xff)) return toHex(Uint8Array.from([...strHeader(2 * text.length + 1), ...units]));
  return toHex(Uint8Array.from([...strHeader(2 * text.length), ...units.flatMap((u) => [u & 0xff, u >> 8])]));
}

/** One char of each kind the kernels and the paths around them tell apart. */
const kinds = [
  "\u0000", "a", "\u007f", "\u0080", "\u009f", " ", "é", "ÿ", "Ā", "中", "�", "￿",
  "\ud800", "\udfff", "😀",
];

/** ASCII of `n` chars that are not all the same, so a char out of place shows. */
const ascii = (n: number) => Array.from({ length: n }, (_, i) => String.fromCharCode(0x61 + (i % 26))).join("");

/** Texts of every length up to 256, past it to the kernels' limit (4096) and beyond, of each kind of char. */
function texts(): string[] {
  const list: string[] = [];
  for (let n = 0; n <= 256; n++) {
    list.push(ascii(n));
    for (const k of kinds) {
      // The kind at every position for short texts, at the first, last and a few in between for longer ones.
      const positions = n <= 80 ? Array.from({ length: n }, (_, i) => i)
        : [0, 1, 7, 8, 15, 16, 31, 32, 63, 64, n >> 1, n - 17, n - 9, n - 2, n - 1];
      for (const p of positions) if (p >= 0 && p + k.length <= n) list.push(ascii(p) + k + ascii(n - p - k.length));
      if (k.length === 1) list.push(k.repeat(n));
    }
  }
  for (const n of [511, 512, 1023, 1024, 1025, 2047, 4095, 4096, 4097, 5000])
    for (const k of ["a", "é", "\u0085", "中", "\ud800"]) list.push(ascii(n - 1) + k, k + ascii(n - 1));
  return list;
}

describe("WebAssembly kernels", async () => {
  const runtime = await browser();
  const { NinoReader, NinoWriter, deserialize, serialize, useWasm, NinoError } = runtime;
  const all = texts();
  const string = { read: (r: InstanceType<typeof NinoReader>) => r.string(), write: (w: InstanceType<typeof NinoWriter>, v: string | null) => w.string(v) };

  it("are built from wasm/text.wat as it is (npm run build:wasm)", () => {
    assert.equal(embedded().source, sourceHash(), "text.wat changed since npm run build:wasm");
  });

  it("are what wasm-tools builds from wasm/text.wat", (t) => {
    if (!hasWasmTools()) return t.skip("wasm-tools is not installed (see the README)");
    assert.equal(embedded().module, build());
  });

  it("take as many chars as nino.ts passes them (kernelText)", () => {
    const array = /\(array\.new_default \$codes \(i32\.const (\d+)\)\)/.exec(readFileSync(watPath, "utf8"))?.[1];
    const limit = /^const kernelText = (\d+);$/m.exec(readFileSync(runtimePath, "utf8"))?.[1];
    assert.ok(array !== undefined && limit !== undefined);
    assert.equal(array, limit);
  });

  it("run in every Node the tests support (WebAssembly SIMD, GC and the JS String Builtins)", () => {
    assert.equal(useWasm(true), true);
  });

  for (const on of [true, false]) {
    describe(on ? "on" : "off (the JavaScript paths)", () => {
      it("write every text as Nino does", () => {
        assert.equal(useWasm(on), on);
        for (const text of all) {
          const w = new NinoWriter();
          w.string(text);
          assert.equal(toHex(w.toBytes()), expected(text), JSON.stringify(text.slice(0, 40)));
        }
      });

      it("read every text back, from deserialize's buffer and from a reader of its own", () => {
        useWasm(on);
        for (const text of all) {
          const bytes = serialize(string, text).slice();
          assert.equal(deserialize(string, bytes), text);
          // At an odd offset of a larger buffer: the kernels' copy of the bytes.
          const buffer = new Uint8Array(bytes.length + 3);
          buffer.set(bytes, 1);
          assert.equal(new NinoReader(buffer.subarray(1, 1 + bytes.length)).string(), text);
        }
      });

      it("read many texts from one payload", () => {
        useWasm(on);
        const w = new NinoWriter();
        const some = all.filter((_, i) => i % 7 === 0);
        for (const t of some) w.string(t);
        const r = new NinoReader(w.toBytes());
        assert.deepEqual(some.map(() => r.string()), some);
      });

      it("write and read ASCII as [NinoUtf8]", () => {
        useWasm(on);
        for (let n = 0; n <= 300; n++) {
          const text = ascii(n), w = new NinoWriter();
          w.utf8(text);
          assert.equal(toHex(w.toBytes()), n === 0 ? "01" : toHex(Uint8Array.from([...strHeader(n + 1), ...strHeader(n),
            ...Array.from(text, (c) => c.charCodeAt(0))])));
          assert.equal(new NinoReader(w.toBytes()).utf8(), text);
        }
      });

      it("reject text the data does not hold", () => {
        useWasm(on);
        for (const n of [40, 100, 4096]) {
          // UTF-16 and Latin-1 headers claiming one more char than the bytes after them.
          for (const header of [2 * n, 2 * n + 1]) {
            const bytes = Uint8Array.from([...strHeader(header), ...new Array(header & 1 ? n - 1 : 2 * n - 1).fill(0x61)]);
            assert.throws(() => deserialize(string, bytes), NinoError);
            assert.throws(() => new NinoReader(bytes).string(), NinoError);
          }
        }
      });
    });
  }
  useWasm(true);
});
