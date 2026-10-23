// Strings in both of Nino's encodings: the default (one byte per char when every char is at most U+00FF, else
// UTF-16, unpaired surrogates kept) and [NinoUtf8]. The runtime takes other paths in Node (Buffer's native methods)
// than in a browser; both are tested, the browser's on a copy of the module loaded without `Buffer`.
import assert from "node:assert/strict";
import { describe, it } from "node:test";
import * as node from "../src/nino.ts";
import { hex, int32, toHex } from "./codecs.ts";

type Runtime = typeof node;

/** The runtime as a browser has it: a second instance of the module, evaluated while `Buffer` is not defined. */
async function browser(): Promise<Runtime> {
  const global = globalThis as { Buffer?: unknown };
  const saved = global.Buffer;
  delete global.Buffer;
  try {
    return (await import(new URL("../src/nino.ts?browser", import.meta.url).href)) as Runtime;
  } finally {
    global.Buffer = saved;
  }
}

const latin1 = (n: number) => Array.from({ length: n }, (_, i) => String.fromCharCode(0x20 + ((i * 37) % 0xdf))).join("");
const ascii = (n: number) => Array.from({ length: n }, (_, i) => String.fromCharCode(0x61 + (i % 26))).join("");
const wide = (n: number) => Array.from({ length: n }, (_, i) => String.fromCharCode(0x4e00 + i)).join("");

/** Texts of every length the runtime treats differently (short, medium, the reader's window, chunks). */
const texts = [
  "", "a", "é", "\u0080\u0081\u009f", "ÿ", "﻿", "😀", "\ud800", "\udc00x", "a\ud800b", "x\udbff",
  ...[1, 7, 8, 9, 12, 13, 15, 16, 17, 31, 32, 33, 255, 256, 257, 4095, 4096, 4097, 5000].flatMap((n) =>
    [ascii(n), latin1(n), wide(n), ascii(n) + "中", "中" + latin1(n), ascii(n) + "\ud800"]),
];

/** The bytes Nino writes for `text` in the default encoding. */
function expected(text: string): string {
  if ([...text].every((c) => c.length === 1 && c.charCodeAt(0) <= 0xff) && text.length !== 0)
    return toHex(Uint8Array.from([...int32(~text.length), ...Array.from(text, (c) => c.charCodeAt(0))]));
  const units = Array.from({ length: text.length }, (_, i) => text.charCodeAt(i));
  return toHex(Uint8Array.from([...int32(text.length), ...units.flatMap((u) => [u & 0xff, u >> 8])]));
}

for (const [name, load] of [["Node", async () => node], ["browser", browser]] as const) {
  describe(`strings (${name})`, async () => {
    const { NinoReader, NinoWriter, NinoError } = await load();
    const write = (f: (w: InstanceType<typeof NinoWriter>) => void) => {
      const w = new NinoWriter();
      f(w);
      return w.toBytes();
    };
    /** Several values one after another, at an odd offset, as the reader's window sees them in a payload. */
    const read = <T>(bytes: Uint8Array, f: (r: InstanceType<typeof NinoReader>) => T): T => {
      const buffer = new Uint8Array(bytes.length + 5);
      buffer.set(bytes, 1);
      return f(new NinoReader(buffer.subarray(1, 1 + bytes.length)));
    };

    it("writes and reads every text in the default encoding", () => {
      for (const text of texts) {
        const bytes = write((w) => w.string(text));
        assert.equal(toHex(bytes), expected(text), JSON.stringify(text.slice(0, 20)));
        assert.equal(read(bytes, (r) => r.string()), text);
      }
    });

    it("reads many texts from one payload", () => {
      const bytes = write((w) => texts.forEach((t) => w.string(t)));
      assert.deepEqual(read(bytes, (r) => texts.map(() => r.string())), texts);
    });

    it("writes null as -1, and reads -1 as null", () => {
      assert.equal(toHex(write((w) => w.string(null))), "ffffffff");
      assert.equal(read(hex("ffffffff"), (r) => r.string()), null);
    });

    it("writes and reads [NinoUtf8] text: char count, byte count, bytes", () => {
      for (const text of texts.filter((t) => !/[\ud800-\udfff]/.test(t) || /😀/.test(t))) {
        const bytes = write((w) => w.utf8(text));
        const utf8 = new TextEncoder().encode(text);
        assert.equal(toHex(bytes), text.length === 0 ? "00000000"
          : toHex(Uint8Array.from([...int32(text.length), ...int32(utf8.length), ...utf8])));
        assert.equal(read(bytes, (r) => r.utf8()), text);
      }
      assert.equal(toHex(write((w) => w.utf8(null))), "ffffffff");
    });

    it("writes an unpaired surrogate as U+FFFD in UTF-8, as .NET does", () => {
      assert.equal(toHex(write((w) => w.utf8("a\ud800"))), "02000000" + "04000000" + "61efbfbd");
    });

    it("rejects UTF-8 that is not, or does not fill the char count", () => {
      for (const bad of ["01000000 01000000 ff", "02000000 02000000 c328", "01000000 02000000 6161",
        "02000000 01000000 61", "03000000 02000000 c3a9", "01000000 04000000 f09f9880", "feffffff",
        "01000000 ffffffff", "05000000 05000000 6161"])
        assert.throws(() => read(hex(bad), (r) => r.utf8()), NinoError, bad);
    });

    it("rejects lengths the data does not hold", () => {
      for (const bad of ["fbffffff 6161", "03000000 610062", "ffffff7f", "00000080", "feffff7f", "02"])
        assert.throws(() => read(hex(bad), (r) => r.string()), NinoError, bad);
    });
  });
}
