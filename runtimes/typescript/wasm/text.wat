;; The WebAssembly kernels of the TypeScript runtime (src/nino.ts): text between JavaScript strings and the bytes of
;; Nino's string encodings, for engines with WebAssembly SIMD, GC and the JS String Builtins ("wasm:js-string", which
;; read and make JavaScript strings without copying them through JavaScript). nino.ts embeds the module built from
;; this file (npm run build:wasm: wasm-tools, through wasm/embed.ts); test/wasm.test.ts checks the two match.
;;
;; A string goes through $codes, an array of UTF-16 code units: intoCharCodeArray and fromCharCodeArray copy a
;; string's chars to and from it natively, and these loops move them between it and linear memory, 16 or 8 at a
;; time through a SIMD register (a GC array has no vector loads). Callers pass only ranges they have checked: at most
;; `kernelText` chars, inside the one page of memory (it never grows), so nothing here can trap.
;;
;; Memory: bytes 0-4095 hold the bytes `deserialize` reads (its reader's buffer); 4096-12287 are where text is copied
;; in and written out (see `transfer` in nino.ts).
(module
  (type $codes (array (mut i16)))
  (import "wasm:js-string" "intoCharCodeArray"
    (func $into (param externref (ref null $codes) i32) (result i32)))
  (import "wasm:js-string" "fromCharCodeArray"
    (func $from (param (ref null $codes) i32 i32) (result (ref extern))))
  (memory (export "memory") 1 1)
  ;; The longest text a kernel takes (`kernelText` in nino.ts, 4096 chars: test/wasm.test.ts checks the two agree).
  (global $codes (ref $codes) (array.new_default $codes (i32.const 4096)))

  ;; The text of the $n (at most 4096) Latin-1 bytes at $p.
  (func (export "latin1") (param $p i32) (param $n i32) (result externref)
    (local $a (ref $codes)) (local $i i32) (local $end i32) (local $v v128)
    (local.set $a (global.get $codes))
    (local.set $end (i32.and (local.get $n) (i32.const -16)))
    (block $done
      (loop $next
        (br_if $done (i32.ge_u (local.get $i) (local.get $end)))
        (local.set $v (v128.load (i32.add (local.get $p) (local.get $i))))
        (array.set $codes (local.get $a) (i32.add (local.get $i) (i32.const 0)) (i8x16.extract_lane_u 0 (local.get $v)))
        (array.set $codes (local.get $a) (i32.add (local.get $i) (i32.const 1)) (i8x16.extract_lane_u 1 (local.get $v)))
        (array.set $codes (local.get $a) (i32.add (local.get $i) (i32.const 2)) (i8x16.extract_lane_u 2 (local.get $v)))
        (array.set $codes (local.get $a) (i32.add (local.get $i) (i32.const 3)) (i8x16.extract_lane_u 3 (local.get $v)))
        (array.set $codes (local.get $a) (i32.add (local.get $i) (i32.const 4)) (i8x16.extract_lane_u 4 (local.get $v)))
        (array.set $codes (local.get $a) (i32.add (local.get $i) (i32.const 5)) (i8x16.extract_lane_u 5 (local.get $v)))
        (array.set $codes (local.get $a) (i32.add (local.get $i) (i32.const 6)) (i8x16.extract_lane_u 6 (local.get $v)))
        (array.set $codes (local.get $a) (i32.add (local.get $i) (i32.const 7)) (i8x16.extract_lane_u 7 (local.get $v)))
        (array.set $codes (local.get $a) (i32.add (local.get $i) (i32.const 8)) (i8x16.extract_lane_u 8 (local.get $v)))
        (array.set $codes (local.get $a) (i32.add (local.get $i) (i32.const 9)) (i8x16.extract_lane_u 9 (local.get $v)))
        (array.set $codes (local.get $a) (i32.add (local.get $i) (i32.const 10)) (i8x16.extract_lane_u 10 (local.get $v)))
        (array.set $codes (local.get $a) (i32.add (local.get $i) (i32.const 11)) (i8x16.extract_lane_u 11 (local.get $v)))
        (array.set $codes (local.get $a) (i32.add (local.get $i) (i32.const 12)) (i8x16.extract_lane_u 12 (local.get $v)))
        (array.set $codes (local.get $a) (i32.add (local.get $i) (i32.const 13)) (i8x16.extract_lane_u 13 (local.get $v)))
        (array.set $codes (local.get $a) (i32.add (local.get $i) (i32.const 14)) (i8x16.extract_lane_u 14 (local.get $v)))
        (array.set $codes (local.get $a) (i32.add (local.get $i) (i32.const 15)) (i8x16.extract_lane_u 15 (local.get $v)))
        (local.set $i (i32.add (local.get $i) (i32.const 16)))
        (br $next)))
    (block $done
      (loop $next
        (br_if $done (i32.ge_u (local.get $i) (local.get $n)))
        (array.set $codes (local.get $a) (local.get $i) (i32.load8_u (i32.add (local.get $p) (local.get $i))))
        (local.set $i (i32.add (local.get $i) (i32.const 1)))
        (br $next)))
    (call $from (local.get $a) (i32.const 0) (local.get $n)))

  ;; The text of the $n (at most 4096) little-endian UTF-16 code units at $p, unpaired surrogates kept.
  (func (export "utf16") (param $p i32) (param $n i32) (result externref)
    (local $a (ref $codes)) (local $i i32) (local $end i32) (local $v v128)
    (local.set $a (global.get $codes))
    (local.set $end (i32.and (local.get $n) (i32.const -8)))
    (block $done
      (loop $next
        (br_if $done (i32.ge_u (local.get $i) (local.get $end)))
        (local.set $v (v128.load (i32.add (local.get $p) (i32.shl (local.get $i) (i32.const 1)))))
        (array.set $codes (local.get $a) (i32.add (local.get $i) (i32.const 0)) (i16x8.extract_lane_u 0 (local.get $v)))
        (array.set $codes (local.get $a) (i32.add (local.get $i) (i32.const 1)) (i16x8.extract_lane_u 1 (local.get $v)))
        (array.set $codes (local.get $a) (i32.add (local.get $i) (i32.const 2)) (i16x8.extract_lane_u 2 (local.get $v)))
        (array.set $codes (local.get $a) (i32.add (local.get $i) (i32.const 3)) (i16x8.extract_lane_u 3 (local.get $v)))
        (array.set $codes (local.get $a) (i32.add (local.get $i) (i32.const 4)) (i16x8.extract_lane_u 4 (local.get $v)))
        (array.set $codes (local.get $a) (i32.add (local.get $i) (i32.const 5)) (i16x8.extract_lane_u 5 (local.get $v)))
        (array.set $codes (local.get $a) (i32.add (local.get $i) (i32.const 6)) (i16x8.extract_lane_u 6 (local.get $v)))
        (array.set $codes (local.get $a) (i32.add (local.get $i) (i32.const 7)) (i16x8.extract_lane_u 7 (local.get $v)))
        (local.set $i (i32.add (local.get $i) (i32.const 8)))
        (br $next)))
    (block $done
      (loop $next
        (br_if $done (i32.ge_u (local.get $i) (local.get $n)))
        (array.set $codes (local.get $a) (local.get $i)
          (i32.load16_u (i32.add (local.get $p) (i32.shl (local.get $i) (i32.const 1)))))
        (local.set $i (i32.add (local.get $i) (i32.const 1)))
        (br $next)))
    (call $from (local.get $a) (i32.const 0) (local.get $n)))

  ;; Copies the chars of $s ($n of them, at most 4096) into $codes and writes them at $out, one byte each, up to the
  ;; first wider than a byte: returns how many it wrote ($n when every char fits).
  (func (export "narrow") (param $s externref) (param $n i32) (param $out i32) (result i32)
    (local $a (ref $codes)) (local $i i32) (local $end i32) (local $c i32) (local $v v128)
    (local.set $a (global.get $codes))
    (drop (call $into (local.get $s) (local.get $a) (i32.const 0)))
    (local.set $end (i32.and (local.get $n) (i32.const -8)))
    (block $done
      (loop $next
        (br_if $done (i32.ge_u (local.get $i) (local.get $end)))
        (local.set $v (i16x8.replace_lane 0 (local.get $v) (array.get_u $codes (local.get $a) (i32.add (local.get $i) (i32.const 0)))))
        (local.set $v (i16x8.replace_lane 1 (local.get $v) (array.get_u $codes (local.get $a) (i32.add (local.get $i) (i32.const 1)))))
        (local.set $v (i16x8.replace_lane 2 (local.get $v) (array.get_u $codes (local.get $a) (i32.add (local.get $i) (i32.const 2)))))
        (local.set $v (i16x8.replace_lane 3 (local.get $v) (array.get_u $codes (local.get $a) (i32.add (local.get $i) (i32.const 3)))))
        (local.set $v (i16x8.replace_lane 4 (local.get $v) (array.get_u $codes (local.get $a) (i32.add (local.get $i) (i32.const 4)))))
        (local.set $v (i16x8.replace_lane 5 (local.get $v) (array.get_u $codes (local.get $a) (i32.add (local.get $i) (i32.const 5)))))
        (local.set $v (i16x8.replace_lane 6 (local.get $v) (array.get_u $codes (local.get $a) (i32.add (local.get $i) (i32.const 6)))))
        (local.set $v (i16x8.replace_lane 7 (local.get $v) (array.get_u $codes (local.get $a) (i32.add (local.get $i) (i32.const 7)))))
        (br_if $done (v128.any_true (v128.and (local.get $v) (v128.const i16x8 0xff00 0xff00 0xff00 0xff00 0xff00 0xff00 0xff00 0xff00))))
        (v128.store64_lane 0 (i32.add (local.get $out) (local.get $i)) (i8x16.narrow_i16x8_u (local.get $v) (local.get $v)))
        (local.set $i (i32.add (local.get $i) (i32.const 8)))
        (br $next)))
    (block $done
      (loop $next
        (br_if $done (i32.ge_u (local.get $i) (local.get $n)))
        (local.set $c (array.get_u $codes (local.get $a) (local.get $i)))
        (br_if $done (i32.gt_u (local.get $c) (i32.const 255)))
        (i32.store8 (i32.add (local.get $out) (local.get $i)) (local.get $c))
        (local.set $i (i32.add (local.get $i) (i32.const 1)))
        (br $next)))
    (local.get $i))

  ;; Writes the $n chars `narrow` copied into $codes at $out as little-endian UTF-16 code units.
  (func (export "widen") (param $n i32) (param $out i32)
    (local $a (ref $codes)) (local $i i32) (local $end i32) (local $v v128)
    (local.set $a (global.get $codes))
    (local.set $end (i32.and (local.get $n) (i32.const -8)))
    (block $done
      (loop $next
        (br_if $done (i32.ge_u (local.get $i) (local.get $end)))
        (local.set $v (i16x8.replace_lane 0 (local.get $v) (array.get_u $codes (local.get $a) (i32.add (local.get $i) (i32.const 0)))))
        (local.set $v (i16x8.replace_lane 1 (local.get $v) (array.get_u $codes (local.get $a) (i32.add (local.get $i) (i32.const 1)))))
        (local.set $v (i16x8.replace_lane 2 (local.get $v) (array.get_u $codes (local.get $a) (i32.add (local.get $i) (i32.const 2)))))
        (local.set $v (i16x8.replace_lane 3 (local.get $v) (array.get_u $codes (local.get $a) (i32.add (local.get $i) (i32.const 3)))))
        (local.set $v (i16x8.replace_lane 4 (local.get $v) (array.get_u $codes (local.get $a) (i32.add (local.get $i) (i32.const 4)))))
        (local.set $v (i16x8.replace_lane 5 (local.get $v) (array.get_u $codes (local.get $a) (i32.add (local.get $i) (i32.const 5)))))
        (local.set $v (i16x8.replace_lane 6 (local.get $v) (array.get_u $codes (local.get $a) (i32.add (local.get $i) (i32.const 6)))))
        (local.set $v (i16x8.replace_lane 7 (local.get $v) (array.get_u $codes (local.get $a) (i32.add (local.get $i) (i32.const 7)))))
        (v128.store (i32.add (local.get $out) (i32.shl (local.get $i) (i32.const 1))) (local.get $v))
        (local.set $i (i32.add (local.get $i) (i32.const 8)))
        (br $next)))
    (block $done
      (loop $next
        (br_if $done (i32.ge_u (local.get $i) (local.get $n)))
        (i32.store16 (i32.add (local.get $out) (i32.shl (local.get $i) (i32.const 1)))
          (array.get_u $codes (local.get $a) (local.get $i)))
        (local.set $i (i32.add (local.get $i) (i32.const 1)))
        (br $next))))
)
