// Hash flooding (NinoKeyGuard): keys chosen to share one of V8's hash buckets, which V8 computes for numbers and
// bigints without a seed. A map or set of them must be rejected with a NinoError before it grows quadratic, through
// generated code and the runtime's own readers, while ordinary keys of the same count read as before.
import assert from "node:assert/strict";
import { describe, it } from "node:test";
import { deserialize, deserializeInto, NinoError, serialize, type NinoCodec } from "../src/nino.ts";
import { IntMap, IntMapCodec, IntSet, IntSetCodec } from "./generated/Nino.Conformance.ts";

// V8's ComputeUnseededHash and ComputeLongHash (src/numbers/hash-seed / utils), written from V8's source with 64-bit
// BigInts, independent of nino.ts's 32-bit halves.
function unseeded(key: number): number {
  let h = key >>> 0;
  h = (~h + (h << 15)) >>> 0;
  h = (h ^ (h >>> 12)) >>> 0;
  h = (h + (h << 2)) >>> 0;
  h = (h ^ (h >>> 4)) >>> 0;
  h = Math.imul(h, 2057) >>> 0;
  h = (h ^ (h >>> 16)) >>> 0;
  return h & 0x3fffffff;
}

function long(key: bigint): number {
  const m = (x: bigint) => BigInt.asUintN(64, x);
  let h = m(key);
  h = m(~h + (h << 18n));
  h = h ^ (h >> 31n);
  h = m(h * 21n);
  h = h ^ (h >> 11n);
  h = m(h + (h << 6n));
  h = h ^ (h >> 22n);
  return Number(h & 0x3fffffffn);
}

const bits = new Float64Array(1);
const words = new BigUint64Array(bits.buffer);
const doubleHash = (k: number) => ((bits[0] = k), long(words[0]));

/** `count` keys from `key(0)`, `key(1)`... whose hashes share their low `low` bits: one bucket of any table to 2^low. */
function colliding<K>(count: number, low: number, key: (i: number) => K, hash: (k: K) => number): K[] {
  const keys: K[] = [], mask = (1 << low) - 1;
  for (let i = 0; keys.length < count; i++) if ((hash(key(i)) & mask) === 0) keys.push(key(i));
  return keys;
}

const flooded = (e: unknown) => e instanceof NinoError && /hash flooding/.test(e.message);

/** A HashSet<long> read by the runtime's generic set reader. */
const LongSetCodec: NinoCodec<Set<bigint> | null> = {
  read: (r) => r.set((r) => r.i64()),
  write: (w, v) => w.set(v, (w, x) => w.i64(x)),
};

describe("hash flooding", () => {
  const n = 400; // 400 keys: a table of 512, 256 buckets; keys sharing 9 low bits all land in one
  const ints = colliding(n, 9, (i) => i, unseeded);
  it("rejects int set elements and map keys V8 would put in one bucket, through generated code", () => {
    const set = Object.assign(new IntSet(), { Values: new Set(ints) });
    assert.throws(() => deserialize(IntSetCodec, serialize(IntSetCodec, set)), flooded);
    const map = Object.assign(new IntMap(), { Values: new Map(ints.map((k) => [k, k] as [number, number])) });
    assert.throws(() => deserialize(IntMapCodec, serialize(IntMapCodec, map)), flooded);
    // Into an existing value too.
    const into = Object.assign(new IntSet(), { Values: new Set([1, 2, 3]) });
    assert.throws(() => deserializeInto(IntSetCodec, serialize(IntSetCodec, set), into), flooded);
  });

  it("reads as many ordinary keys, and as many colliding ones as fit under the limit", () => {
    const set = Object.assign(new IntSet(), { Values: new Set(Array.from({ length: n }, (_, i) => i * 7919)) });
    assert.deepEqual(deserialize(IntSetCodec, serialize(IntSetCodec, set)), set);
    // 128 keys or fewer are not guarded: they cannot cost more than 128 probes each.
    const few = Object.assign(new IntSet(), { Values: new Set(ints.slice(0, 128)) });
    assert.deepEqual(deserialize(IntSetCodec, serialize(IntSetCodec, few)), few);
  });

  it("rejects colliding doubles, uint32s past int32 and bigints through the runtime's readers", () => {
    const doubles = colliding(n, 9, (i) => i + 0.5, doubleHash);
    const doubleSet: NinoCodec<Set<number> | null> = {
      read: (r) => r.set((r) => r.f64()), write: (w, v) => w.set(v, (w, x) => w.f64(x)),
    };
    assert.throws(() => deserialize(doubleSet, serialize(doubleSet, new Set(doubles))), flooded);
    const bigints = colliding(n, 9, (i) => BigInt(i) + (1n << 40n), long);
    assert.throws(() => deserialize(LongSetCodec, serialize(LongSetCodec, new Set(bigints))), flooded);
    const ordinary = new Set(Array.from({ length: n }, (_, i) => BigInt(i) * 1_000_003n));
    assert.deepEqual(deserialize(LongSetCodec, serialize(LongSetCodec, ordinary)), ordinary);
  });
});
