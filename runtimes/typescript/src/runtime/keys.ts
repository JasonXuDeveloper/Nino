import { NinoError } from "./error.ts";

/**
 * The most keys a key read from the data may find in its bucket of the `Map` or `Set` it goes into, as
 * `NinoSettings.MaxHashCollisions`'s default in .NET. See `NinoKeyGuard`.
 */
const maxHashCollisions = 100;
/** Maps and sets of up to this many keys are not guarded: their keys cannot cost more than this many probes each. */
export const guardedKeys = 128;

/**
 * Bounds what keys read from the data cost a `Map` or `Set` (hash flooding), as `NinoSettings.MaxHashCollisions`
 * does in .NET. V8 hashes strings with a per-process seed, but numbers and bigints without one (its
 * `ComputeUnseededHash` and `ComputeLongHash`), and a string that is an array index ("0" to "4294967294") by its
 * value (`MakeArrayIndexHash`), so data can choose keys that share a bucket, and each insert then scans every key
 * before it: 8,000 such ints took 40 times as long as ordinary ones in Node 22, 4,000 such strings 117 times, and
 * the read grows with the square of their count. The guard counts the keys of each bucket of the table V8 ends with
 * (`OrderedHashTable`: a power of two from 4 that holds them all, half as many buckets, a bucket by the hash's low
 * bits) and throws a `NinoError` when a key would find more than `maxHashCollisions` keys in its bucket. The smaller
 * tables V8 fills on the way are not counted (that would cost twice as much per key): one of `b` buckets holds at most
 * `2b` keys and its buckets gather those of the final one, so a key there scans at most the smaller of `2b` and
 * `maxHashCollisions` times the ratio of the two tables' sizes, which keeps a read within O(n log n) probes. It is
 * exact for V8 (Node, Chromium, Deno); other engines (JavaScriptCore in Safari and Bun, SpiderMonkey in Firefox) hash
 * differently, and there it bounds V8's buckets, not theirs. Generated code makes one for a map or set of more than
 * `guardedKeys` number, bigint or string keys (`NinoReader.keyGuard`) and passes each key through it before adding it.
 */
export class NinoKeyGuard {
  // Keys per bucket of the final table.
  private readonly counts: Int32Array;
  private readonly mask: number;

  constructor(length: number) {
    let capacity = 4;
    while (capacity < length) capacity *= 2;
    this.counts = new Int32Array(capacity >>> 1);
    this.mask = (capacity >>> 1) - 1;
  }

  /** An int32 key (of a type no wider than `int`), after checking its bucket. */
  int(key: number): number {
    this.add(unseededHash(key));
    return key;
  }

  /** A number key, after checking its bucket. */
  number(key: number): number {
    this.add(numberHash(key));
    return key;
  }

  /** A key of either kind or null (a nullable number or bigint), after checking its bucket. */
  key<K>(key: K): K {
    return guardKey(this, key);
  }

  /** A bigint key, after checking its bucket. */
  bigint(key: bigint): bigint {
    this.add(bigintHash(key));
    return key;
  }

  /** A string key, after checking its bucket when V8 hashes it by its value (an array index, `arrayIndexHash`). */
  string<S extends string | null>(key: S): S {
    if (key !== null && key.length <= 10) {
      const first = key.charCodeAt(0);
      if (first >= 0x30 && first <= 0x39) this.index(key, key.length);
    }
    return key;
  }

  private index(key: string, length: number): void {
    const hash = arrayIndexHash(key, length);
    if (hash >= 0) this.add(hash);
  }

  private add(hash: number): void {
    if (this.counts[hash & this.mask]++ === maxHashCollisions + 1) throw flooded();
  }
}

function flooded(): NinoError {
  return new NinoError(`more than ${maxHashCollisions} keys of a map or set share a hash bucket (hash flooding).`);
}

/** V8's hash of a number key (`Object::GetSimpleHash`): int32 values (and -0) by `ComputeUnseededHash`, others by bits. */
function numberHash(key: number): number {
  if ((key | 0) === key) return unseededHash(key | 0);
  if (key !== key) return 0x3FFFFFFF;
  keyBits[0] = key;
  return longHash(keyWords[1], keyWords[0]);
}

/** V8's hash of a bigint key (`BigInt::Hash`): the low 64 bits of its magnitude by `ComputeLongHash`, 0 for 0n. */
function bigintHash(key: bigint): number {
  const magnitude = BigInt.asUintN(64, key < 0n ? -key : key);
  return magnitude === 0n ? 0 : longHash(Number(magnitude >> 32n), Number(magnitude & 0xFFFFFFFFn));
}

const keyBits = new Float64Array(1);
const keyWords = new Uint32Array(keyBits.buffer);

/**
 * V8's hash of a string that is an array index (`StringHasher::MakeArrayIndexHash`: its value and its length, no
 * seed), or -1 for any other string of up to 10 chars starting with a digit (those V8 hashes with its seed).
 */
function arrayIndexHash(key: string, length: number): number {
  if (length > 1 && key.charCodeAt(0) === 0x30) return -1;
  let value = 0;
  for (let i = 0; i < length; i++) {
    const digit = key.charCodeAt(i) - 0x30;
    if (digit < 0 || digit > 9) return -1;
    value = value * 10 + digit;
  }
  if (value > 4294967294) return -1;
  return (((value << 2) | (length << 26)) >>> 2) & 0x3FFFFFFF;
}

/** V8's `ComputeUnseededHash`, of a 32-bit key (in int32 arithmetic, the same bits). */
function unseededHash(key: number): number {
  let h = key | 0;
  h = (~h + (h << 15)) | 0;
  h ^= h >>> 12;
  h = (h + (h << 2)) | 0;
  h ^= h >>> 4;
  h = Math.imul(h, 2057);
  h ^= h >>> 16;
  return h & 0x3FFFFFFF;
}

/** V8's `ComputeLongHash`, of the 64-bit key `hi:lo` (in 32-bit halves). */
function longHash(hi: number, lo: number): number {
  // h = ~h + (h << 18)
  let nh = ~hi >>> 0, nl = ~lo >>> 0;
  let sh = ((hi << 18) | (lo >>> 14)) >>> 0, sl = (lo << 18) >>> 0;
  let l = (nl + sl) >>> 0, h = (nh + sh + (l < nl ? 1 : 0)) >>> 0;
  // h ^= h >> 31
  l = (l ^ ((h << 1) | (l >>> 31))) >>> 0;
  h = (h ^ (h >>> 31)) >>> 0;
  // h *= 21: h + (h << 2) + (h << 4)
  let ah = ((h << 2) | (l >>> 30)) >>> 0, al = (l << 2) >>> 0;
  let bh = ((h << 4) | (l >>> 28)) >>> 0, bl = (l << 4) >>> 0;
  let tl = (l + al) >>> 0, th = (h + ah + (tl < l ? 1 : 0)) >>> 0;
  l = (tl + bl) >>> 0;
  h = (th + bh + (l < tl ? 1 : 0)) >>> 0;
  // h ^= h >> 11
  l = (l ^ ((h << 21) | (l >>> 11))) >>> 0;
  h = (h ^ (h >>> 11)) >>> 0;
  // h += h << 6
  sh = ((h << 6) | (l >>> 26)) >>> 0;
  sl = (l << 6) >>> 0;
  tl = (l + sl) >>> 0;
  h = (h + sh + (tl < l ? 1 : 0)) >>> 0;
  l = tl;
  // h ^= h >> 22
  l = (l ^ ((h << 10) | (l >>> 22))) >>> 0;
  return l & 0x3FFFFFFF;
}

/** A key of a type known only at run time through `guard`: numbers, bigints and strings (see `NinoKeyGuard`). */
export function guardKey<K>(guard: NinoKeyGuard, key: K): K {
  if (typeof key === "number") guard.number(key);
  else if (typeof key === "bigint") guard.bigint(key);
  else if (typeof key === "string") guard.string(key);
  return key;
}
