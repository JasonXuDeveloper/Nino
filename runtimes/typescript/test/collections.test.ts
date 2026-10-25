// Arrays, sets, maps, nullables, byte arrays, the type id registry, and `serialize`'s shared buffers.
import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { deserialize, NinoError, NinoReader, NinoRegistry, NinoWriter, serialize, type NinoCodec } from "../src/nino.ts";
import { hex, int32, toHex } from "./codecs.ts";

const i32: NinoCodec<number> = { read: (r) => r.i32(), write: (w, v) => w.i32(v) };
const text: NinoCodec<string | null> = { read: (r) => r.string(), write: (w, v) => w.string(v) };

function write(f: (w: NinoWriter) => void): Uint8Array {
  const w = new NinoWriter();
  f(w);
  return w.toBytes();
}

describe("collections", () => {
  it("writes and reads arrays, null and empty ones", () => {
    const values = [1, -2, 2147483647];
    assert.equal(toHex(write((w) => w.array(values, i32.write))), "03000000 01000000 feffffff ffffff7f".replace(/ /g, ""));
    assert.deepEqual(new NinoReader(write((w) => w.array(values, i32.write))).array(i32.read), values);
    assert.equal(toHex(write((w) => w.array(null, i32.write))), "ffffffff");
    assert.equal(new NinoReader(hex("ffffffff")).array(i32.read), null);
    assert.deepEqual(new NinoReader(hex("00000000")).array(i32.read), []);
  });

  it("writes and reads sets in their order", () => {
    const set = new Set([3, 1, 2]);
    assert.deepEqual([...new NinoReader(write((w) => w.set(set, i32.write))).set(i32.read)!], [3, 1, 2]);
    assert.equal(new NinoReader(hex("ffffffff")).set(i32.read), null);
  });

  it("writes and reads maps, null keys and values included", () => {
    const map = new Map<string | null, string | null>([["a", "x"], [null, "n"], ["名字", null]]);
    const bytes = write((w) => w.map(map, text.write, text.write));
    assert.deepEqual([...new NinoReader(bytes).map(text.read, text.read)!], [...map]);
    assert.equal(new NinoReader(hex("ffffffff")).map(text.read, text.read), null);
  });

  it("reads keys like __proto__ as Map entries, touching no prototype", () => {
    const keys = ["__proto__", "constructor", "prototype", "toString", "hasOwnProperty"];
    const map = new Map(keys.map((k, i) => [k, i] as [string, number]));
    const read = new NinoReader(write((w) => w.map(map, text.write, i32.write))).map(text.read, i32.read)!;
    assert.ok(read instanceof Map);
    assert.deepEqual([...read], [...map]);
    assert.equal(Object.getPrototypeOf(read), Map.prototype);
    assert.deepEqual(Object.keys(read), []);
    const plain = {} as Record<string, unknown>;
    assert.equal(plain.polluted, undefined);
    assert.equal(typeof plain.toString, "function");
    assert.equal(Object.getPrototypeOf(plain), Object.prototype);
  });

  it("rejects a key or element read twice, as .NET's dictionaries and sets do", () => {
    const latin1 = (t: string) => [...int32(~t.length), ...[...t].map((c) => c.charCodeAt(0))];
    const utf16 = (t: string) => [...int32(t.length), ...[...t].flatMap((c) => [c.charCodeAt(0), 0])];
    const f64 = (x: number) => [...new Uint8Array(Float64Array.of(x).buffer)];
    const cases: [string, number[], (r: NinoReader) => unknown][] = [
      ["int keys", [...int32(2), ...int32(1), ...int32(5), ...int32(1), ...int32(6)], (r) => r.map(i32.read, i32.read)],
      ["string keys", [...int32(2), ...latin1("a"), ...int32(5), ...latin1("a"), ...int32(6)],
        (r) => r.map(text.read, i32.read)],
      ["one key in Latin-1 and in UTF-16", [...int32(2), ...latin1("a"), ...int32(5), ...utf16("a"), ...int32(6)],
        (r) => r.map(text.read, i32.read)],
      ["null keys", [...int32(2), ...int32(-1), ...int32(5), ...int32(-1), ...int32(6)], (r) => r.map(text.read, i32.read)],
      ["int elements", [...int32(3), ...int32(7), ...int32(8), ...int32(7)], (r) => r.set(i32.read)],
      ["empty strings", [...int32(2), ...int32(0), ...int32(0)], (r) => r.set(text.read)],
      ["null strings", [...int32(2), ...int32(-1), ...int32(-1)], (r) => r.set(text.read)],
      ["NaN", [...int32(2), ...f64(NaN), ...f64(NaN)], (r) => r.set((r) => r.f64())],
    ];
    for (const [name, bytes, read] of cases)
      assert.throws(() => read(new NinoReader(Uint8Array.from(bytes))), /repeated in a map or set/, name);
    // Distinct keys are kept, however alike.
    assert.equal(new NinoReader(Uint8Array.from([...int32(2), ...latin1("a"), ...latin1("A")])).set(text.read)!.size, 2);
  });

  it("writes and reads nullable values with a presence byte", () => {
    assert.equal(toHex(write((w) => w.nullable(5, i32.write))), "0105000000");
    assert.equal(toHex(write((w) => w.nullable(null, i32.write))), "00");
    assert.equal(new NinoReader(hex("0105000000")).nullable(i32.read), 5);
    assert.equal(new NinoReader(hex("00")).nullable(i32.read), null);
    for (const bad of ["02", "ff"]) assert.throws(() => new NinoReader(hex(bad)).nullable(i32.read), NinoError);
  });

  it("copies byte arrays, also from a Node Buffer", () => {
    const data = Buffer.from(write((w) => w.bytes(Uint8Array.of(0, 1, 254, 255))));
    const bytes = new NinoReader(data).bytes()!;
    assert.equal(Object.getPrototypeOf(bytes), Uint8Array.prototype);
    assert.deepEqual([...bytes], [0, 1, 254, 255]);
    data.fill(9);
    assert.deepEqual([...bytes], [0, 1, 254, 255]);
    assert.equal(new NinoReader(hex("ffffffff")).bytes(), null);
  });
});

describe("registry", () => {
  class Dog {
    name: string | null = null;
  }
  class Cat {
    lives = 0;
  }
  class Stray {}
  const types = new NinoRegistry();
  types.add(10, Dog, (r) => Object.assign(new Dog(), { name: r.string() }), (w, v) => w.string(v.name));
  types.add(-20, Cat, (r) => Object.assign(new Cat(), { lives: r.i32() }), (w, v) => w.i32(v.lives));

  it("writes the type id of the value's class, then its body", () => {
    const cat = Object.assign(new Cat(), { lives: 9 });
    assert.equal(toHex(serialize(types.codec([10, -20]), cat)), "ecffffff09000000");
    assert.deepEqual(deserialize(types.codec([10, -20]), hex("ecffffff09000000")), cat);
    assert.equal(toHex(serialize(types.codec([10]), null)), "00000000");
    assert.equal(deserialize(types.codec([10]), hex("00000000")), null);
  });

  it("reads only the ids it is given, and writes only registered classes", () => {
    assert.throws(() => deserialize(types.codec([10]), hex("ecffffff09000000")), NinoError);
    assert.throws(() => deserialize(types.codec([10, -20]), hex("0b000000")), NinoError);
    assert.throws(() => serialize(types.codec([10]), new Cat()), NinoError);
    assert.throws(() => serialize(types.codec([10, -20]), new Stray()), NinoError);
    assert.throws(() => serialize(types.codec([10, -20]), { lives: 1 }), NinoError);
  });
});

describe("serialize", () => {
  it("returns results that later ones never overwrite, 8-byte aligned", () => {
    const results = Array.from({ length: 5000 }, (_, i) => serialize(text, "value " + i));
    results.forEach((bytes, i) => {
      assert.equal(new NinoReader(bytes).string(), "value " + i);
      assert.equal(bytes.byteOffset % 8, 0);
    });
  });

  it("writes values larger than its buffers, and grows a writer keeping what it wrote", () => {
    const big = Array.from({ length: 100000 }, (_, i) => i);
    const codec: NinoCodec<number[]> = { read: (r) => r.array(i32.read)!, write: (w, v) => w.array(v, i32.write) };
    assert.deepEqual(deserialize(codec, serialize(codec, big)), big);
    assert.equal(serialize(text, "after").length, 9);
  });

  it("keeps every result intact across values of changing sizes", () => {
    const codec: NinoCodec<number[]> = { read: (r) => r.array(i32.read)!, write: (w, v) => w.array(v, i32.write) };
    const sizes = [1, 5000, 3, 70000, 20000, 2, 300000, 1, 20000, 20000, 20000, 20000, 20000, 20000, 20000, 20000, 9];
    const values = sizes.map((n, k) => Array.from({ length: n }, (_, i) => i * 31 + k));
    const results = values.map((v) => serialize(codec, v));
    results.forEach((bytes, k) => {
      assert.deepEqual(deserialize(codec, bytes), values[k]);
      assert.equal(bytes.byteOffset % 8, 0);
    });
  });

  it("reads data at any offset of any buffer", () => {
    const bytes = serialize(text, "offset");
    for (let offset = 0; offset < 9; offset++) {
      const buffer = new Uint8Array(bytes.length + offset);
      buffer.set(bytes, offset);
      assert.equal(deserialize(text, buffer.subarray(offset)), "offset");
    }
  });

  it("deserializes in a codec that deserializes, and after a value that fails to read", () => {
    // A value whose string holds another serialized value, read with deserialize while the outer call reads.
    const inner = serialize(text, "inner");
    const nested: NinoCodec<string> = {
      read: (r) => {
        const bytes = r.bytes()!;
        return deserialize(text, bytes)! + "/" + r.string();
      },
      write: (w, v) => { w.bytes(inner); w.string(v); },
    };
    assert.equal(deserialize(nested, serialize(nested, "outer")), "inner/outer");
    assert.throws(() => deserialize(text, Uint8Array.of(0xf0, 0xff, 0xff, 0xff, 0x61)), NinoError);
    assert.equal(deserialize(text, serialize(text, "after")), "after");
  });

  it("keeps working after a value that fails to write", () => {
    assert.throws(() => serialize(i32, 1n as unknown as number), TypeError);
    assert.equal(toHex(serialize(i32, 7)), "07000000");
  });
});
