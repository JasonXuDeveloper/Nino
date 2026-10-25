// Every primitive the reader and writer have: the bytes .NET writes for it (little-endian, unaligned) and back.
import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { NinoError, NinoReader, NinoWriter } from "../src/nino.ts";
import { hex, toHex } from "./codecs.ts";

/** The bytes `write` writes. */
function bytes(write: (w: NinoWriter) => void): string {
  const w = new NinoWriter();
  write(w);
  return toHex(w.toBytes());
}

/** A reader over `text` placed at an odd offset of a larger buffer, so nothing is aligned. */
function reader(text: string): NinoReader {
  const data = hex(text), buffer = new Uint8Array(data.length + 7);
  buffer.set(data, 3);
  return new NinoReader(buffer.subarray(3, 3 + data.length));
}

describe("primitives", () => {
  const cases: [string, (w: NinoWriter) => void, (r: NinoReader) => unknown, unknown, string][] = [
    ["bool true", (w) => w.bool(true), (r) => r.bool(), true, "01"],
    ["bool false", (w) => w.bool(false), (r) => r.bool(), false, "00"],
    ["u8", (w) => w.u8(255), (r) => r.u8(), 255, "ff"],
    ["i8", (w) => w.i8(-2), (r) => r.i8(), -2, "fe"],
    ["u16", (w) => w.u16(0xbeef), (r) => r.u16(), 0xbeef, "efbe"],
    ["i16", (w) => w.i16(-32768), (r) => r.i16(), -32768, "0080"],
    ["u32", (w) => w.u32(0xdeadbeef), (r) => r.u32(), 0xdeadbeef, "efbeadde"],
    ["i32", (w) => w.i32(-2), (r) => r.i32(), -2, "feffffff"],
    ["u64", (w) => w.u64(0xfedcba9876543210n), (r) => r.u64(), 0xfedcba9876543210n, "1032547698badcfe"],
    ["i64", (w) => w.i64(-9223372036854775808n), (r) => r.i64(), -9223372036854775808n, "0000000000000080"],
    ["f32", (w) => w.f32(1.5), (r) => r.f32(), 1.5, "0000c03f"],
    ["f64", (w) => w.f64(-0.1), (r) => r.f64(), -0.1, "9a9999999999b9bf"],
    ["f64 NaN", (w) => w.f64(NaN), (r) => r.f64(), NaN, "000000000000f87f"],
    ["char", (w) => w.char("中"), (r) => r.char(), "中", "2d4e"],
    ["char lone surrogate", (w) => w.char("\ud800"), (r) => r.char(), "\ud800", "00d8"],
    ["timeSpan", (w) => w.timeSpan(54300000000n), (r) => r.timeSpan(), 54300000000n, "003f88a40c000000"],
  ];
  for (const [name, write, read, value, expected] of cases) {
    it(name, () => {
      assert.equal(bytes(write), expected);
      assert.deepEqual(read(reader(expected)), value);
    });
  }

  it("wraps out-of-range integers as the typed views do", () => {
    assert.equal(bytes((w) => w.u8(256 + 7)), "07");
    assert.equal(bytes((w) => w.i32(2 ** 32 + 5)), "05000000");
    // i32 writes four bytes itself: the same as DataView.setInt32 for every number.
    for (const v of [0, -1, 1.9, -1.9, 2 ** 31, -(2 ** 31) - 1, 2 ** 53 + 2, -(2 ** 60), NaN, Infinity, -Infinity, -0, 1e300]) {
      const view = new DataView(new ArrayBuffer(4));
      view.setInt32(0, v, true);
      assert.equal(bytes((w) => w.i32(v)), toHex(new Uint8Array(view.buffer)), String(v));
    }
    assert.equal(bytes((w) => w.u64(-1n)), "ffffffffffffffff");
  });

  it("reads a bool as .NET does: 0 or 1, any other byte is invalid data", () => {
    assert.equal(reader("01").bool(), true);
    assert.equal(reader("00").bool(), false);
    assert.throws(() => reader("02").bool(), NinoError);
  });

  it("reads past nothing: each read checks the bytes left", () => {
    for (const read of [(r: NinoReader) => r.i32(), (r: NinoReader) => r.f64(), (r: NinoReader) => r.i64(),
      (r: NinoReader) => r.guid(), (r: NinoReader) => r.decimal(), (r: NinoReader) => r.dateTime()])
      assert.throws(() => read(reader("010203")), NinoError);
    assert.throws(() => reader("").u8(), NinoError);
  });
});

describe("Guid", () => {
  const text = "0f8fad5b-d9cb-469f-a165-70867728950e", wire = "5bad8f0fcbd99f46a1657086772895 0e";
  it("writes and reads its usual text form", () => {
    assert.equal(bytes((w) => w.guid(text)), wire.replace(" ", ""));
    assert.equal(reader(wire).guid(), text);
  });
  it("accepts the N and B forms and upper case", () => {
    for (const form of [text.replaceAll("-", ""), `{${text}}`, text.toUpperCase()])
      assert.equal(bytes((w) => w.guid(form)), wire.replace(" ", ""));
  });
  it("rejects text that is not a Guid", () => {
    for (const bad of ["", "0f8fad5b", text + "0", text.replace("a", "g")])
      assert.throws(() => bytes((w) => w.guid(bad)), NinoError);
  });
});

describe("decimal", () => {
  // flags (scale << 16, sign bit), then the 96-bit integer as hi, lo, mid.
  const cases: [string, string][] = [
    ["0", "00000000 00000000 00000000 00000000"],
    ["-1234.5600", "00000480 00000000 0061bc00 00000000"],
    ["0.5", "00000100 00000000 05000000 00000000"],
    ["79228162514264337593543950335", "00000000 ffffffff ffffffff ffffffff"],
    ["-0.0001", "00000480 00000000 01000000 00000000"],
    ["123456789012345678901234567.8", "00000100 eb35fd03 4ef338be 917a796d"],
  ];
  for (const [text, wire] of cases) {
    it(text, () => {
      assert.equal(bytes((w) => w.decimal(text)), wire.replace(/ /g, ""));
      assert.equal(reader(wire).decimal(), text);
    });
  }
  it("writes -0 as 0 and accepts a sign and a trailing point", () => {
    assert.equal(bytes((w) => w.decimal("-0")), bytes((w) => w.decimal("0.")));
    assert.equal(bytes((w) => w.decimal("+1.")), bytes((w) => w.decimal("1")));
  });
  it("rejects what a decimal cannot hold", () => {
    for (const bad of ["", ".", "1e5", "abc", "79228162514264337593543950336", "0." + "1".repeat(29)])
      assert.throws(() => bytes((w) => w.decimal(bad)), NinoError);
  });
  it("reads every scale and sign a decimal can have", () => {
    assert.equal(reader("00001c80 00000000 01000000 00000000").decimal(), "-0." + "0".repeat(27) + "1");
  });
  it("rejects a scale past 28 and flag bits other than the sign and the scale, as the writer does (§9.10)", () => {
    for (const flags of ["00001d00", "00ff0000", "01000000", "00000140"])
      assert.throws(() => reader(flags + " 00000000 01000000 00000000").decimal(), NinoError, flags);
  });
});

describe("DateTime", () => {
  it("writes a Date as UTC ticks and reads it back to the millisecond", () => {
    const date = new Date(Date.UTC(2026, 9, 18, 12, 30, 45, 123));
    const ticks = (BigInt(date.getTime()) + 62135596800000n) * 10000n | (1n << 62n);
    const w = new NinoWriter();
    w.dateTime(date);
    assert.equal(new DataView(w.toBytes().buffer).getBigUint64(0, true), ticks);
    assert.equal(new NinoReader(w.toBytes()).dateTime().getTime(), date.getTime());
  });
  it("reads DateTime.MinValue and MaxValue, of any kind", () => {
    assert.equal(reader("0000000000000000").dateTime().getTime(), -62135596800000);
    assert.equal(reader("ff3f37f47528ca2b").dateTime().getTime(), 253402300799999);
    assert.equal(reader("ff3f37f47528ca6b").dateTime().getTime(), 253402300799999);
  });
  it("rejects a date outside DateTime's range", () => {
    assert.throws(() => bytes((w) => w.dateTime(new Date(-62135596800001))), NinoError);
    assert.throws(() => bytes((w) => w.dateTime(new Date(NaN))), NinoError);
  });
});
