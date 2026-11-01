// Nino.Core's NinoKernels (Permute, Narrow, Widen, NarrowAscii, WidenAscii) in WebAssembly SIMD128, for WebGL players:
// Burst does not target the web, so this source plugin is compiled by Unity's Emscripten into the player's wasm module.
// It uses SIMD only when the player is built with SIMD (Unity 6's "Target WebAssembly 2023", which compiles everything
// with -msimd128, or the compiler flag passed in Unity 2022.3's Additional IL2CPP Arguments); otherwise
// nino_wasm_kernels returns 0, nothing here holds a SIMD instruction, and NinoWasm registers nothing. Each kernel writes
// exactly the bytes of Nino.Core's scalar reference (NinoKernels.PermuteScalar, NarrowScalar, WidenScalar,
// NarrowAsciiScalar, WidenAsciiScalar). Nino.Core calls them through the function pointers nino_wasm_kernels hands out
// (call_indirect), not through DllImport.
#include <stdint.h>

// The kernels are reached only through nino_wasm_kernels' table (unused, and so not compiled in, without SIMD).
#define NINO_KERNEL static __attribute__((unused))

// Clang's vector extensions and one wasm builtin, not <wasm_simd128.h>: Unity 2022.3's Emscripten (3.1.8) ships a
// wrapper of that header whose #include_next finds nothing in Unity's LLVM, so including it fails the build.
#if defined(__wasm_simd128__) && __has_builtin(__builtin_wasm_swizzle_i8x16)
#define NINO_SIMD 1
typedef int8_t i8x16 __attribute__((vector_size(16), aligned(1)));
typedef uint8_t u8x16 __attribute__((vector_size(16), aligned(1)));
typedef uint16_t u16x8 __attribute__((vector_size(16), aligned(1)));
typedef uint64_t u64x2 __attribute__((vector_size(16), aligned(1)));
typedef uint8_t u8x8 __attribute__((vector_size(8)));
#define LOAD(p) (*(const u8x16*)(p))
#define STORE(p, v) (*(u8x16*)(p) = (v))
#define SWIZZLE(x, m) ((u8x16)__builtin_wasm_swizzle_i8x16((i8x16)(x), (i8x16)(m)))
#endif

NINO_KERNEL void nino_wasm_permute(const uint8_t* source, int sourceStride, uint8_t* destination, int destinationStride,
    const uint8_t* table, int count)
{
    int i = 0;
#ifdef NINO_SIMD
    if (sourceStride <= 64 && destinationStride <= 64)
    {
        int loads = (sourceStride + 15) >> 4, stores = (destinationStride + 15) >> 4;
        // The last value whose loads stay inside the source and whose stores inside the destination (a store past a
        // value's own bytes writes over the next value's, which is written after it).
        int a = count - (loads * 16 + sourceStride - 1) / sourceStride;
        int b = count - (stores * 16 + destinationStride - 1) / destinationStride;
        int last = a < b ? a : b;
        if (last >= 0)
        {
            // The shuffle of load s for store o: its bytes from that load at their index, every other byte 0x80
            // (swizzle gives 0 for an index of 16 or more).
            uint8_t masks[16][16];
            int used = 0;
            for (int o = 0; o < stores; o++)
                for (int s = 0; s < loads; s++)
                    for (int j = 0; j < 16; j++)
                    {
                        int at = o * 16 + j;
                        int from = at < destinationStride ? table[at] : 0xFF;
                        int here = from != 0xFF && (from >> 4) == s;
                        masks[o * 4 + s][j] = here ? (uint8_t)(from & 15) : (uint8_t)0x80;
                        if (here)
                            used |= 1 << (o * 4 + s);
                    }

            const uint8_t* at0 = source;
            uint8_t* to0 = destination;
            int n = last + 1;
            if (loads == 1 && stores == 1)
            {
                u8x16 m = LOAD(masks[0]);
                for (int k = 0; k < n; k++)
                {
                    STORE(to0, SWIZZLE(LOAD(at0), m));
                    at0 += sourceStride;
                    to0 += destinationStride;
                }
            }
            else
            {
                for (int k = 0; k < n; k++)
                {
                    u8x16 x[4];
                    for (int s = 0; s < loads; s++)
                        x[s] = LOAD(at0 + 16 * s);
                    for (int o = 0; o < stores; o++)
                    {
                        u8x16 r = {0};
                        for (int s = 0; s < loads; s++)
                            if (used & (1 << (o * 4 + s)))
                                r |= SWIZZLE(x[s], LOAD(masks[o * 4 + s]));
                        STORE(to0 + o * 16, r);
                    }

                    at0 += sourceStride;
                    to0 += destinationStride;
                }
            }

            i = n;
        }
    }
#endif
    for (; i < count; i++)
    {
        const uint8_t* from = source + (long)i * sourceStride;
        uint8_t* to = destination + (long)i * destinationStride;
        for (int j = 0; j < destinationStride; j++)
            to[j] = table[j] == 0xFF ? 0 : from[table[j]];
    }
}

// 1 when all length chars are Latin-1 (written a byte each), 0 at the first 16 with a char above U+00FF.
NINO_KERNEL int nino_wasm_narrow(const uint16_t* source, uint8_t* destination, int length)
{
#ifdef NINO_SIMD
    if (length >= 16)
    {
        int i = 0;
        for (;;)
        {
            u8x16 a = LOAD(source + i), b = LOAD(source + i + 8);
            u64x2 high = (u64x2)((u16x8)(a | b) & (uint16_t)0xFF00);
            if (high[0] | high[1])
                return 0;
            // Every high byte is 0: the low bytes (the even ones, little-endian) are the Latin-1 bytes.
            STORE(destination + i, __builtin_shufflevector(a, b, 0, 2, 4, 6, 8, 10, 12, 14, 16, 18, 20, 22, 24, 26, 28, 30));
            if (i + 16 == length)
                return 1;
            i = i + 32 <= length ? i + 16 : length - 16;
        }
    }
#endif
    for (int i = 0; i < length; i++)
    {
        if (source[i] > 0xFF)
            return 0;
        destination[i] = (uint8_t)source[i];
    }

    return 1;
}

NINO_KERNEL void nino_wasm_widen(const uint8_t* source, uint16_t* destination, int length)
{
#ifdef NINO_SIMD
    if (length >= 16)
    {
        int i = 0;
        for (;;)
        {
            u8x16 v = LOAD(source + i);
            STORE(destination + i, (u8x16)__builtin_convertvector(
                (u8x8)__builtin_shufflevector(v, v, 0, 1, 2, 3, 4, 5, 6, 7), u16x8));
            STORE(destination + i + 8, (u8x16)__builtin_convertvector(
                (u8x8)__builtin_shufflevector(v, v, 8, 9, 10, 11, 12, 13, 14, 15), u16x8));
            if (i + 16 == length)
                return;
            i = i + 32 <= length ? i + 16 : length - 16;
        }
    }
#endif
    for (int i = 0; i < length; i++)
        destination[i] = source[i];
}

// The number of leading ASCII chars (below U+0080), each written as its byte; from the first 16 with another char, one
// at a time.
NINO_KERNEL int nino_wasm_narrow_ascii(const uint16_t* source, uint8_t* destination, int length)
{
    int i = 0;
#ifdef NINO_SIMD
    if (length >= 16)
    {
        for (;;)
        {
            u8x16 a = LOAD(source + i), b = LOAD(source + i + 8);
            u64x2 high = (u64x2)((u16x8)(a | b) & (uint16_t)0xFF80);
            if (high[0] | high[1])
                break;
            STORE(destination + i, __builtin_shufflevector(a, b, 0, 2, 4, 6, 8, 10, 12, 14, 16, 18, 20, 22, 24, 26, 28, 30));
            if (i + 16 == length)
                return length;
            i = i + 32 <= length ? i + 16 : length - 16;
        }
    }
#endif
    for (; i < length && source[i] < 0x80; i++)
        destination[i] = (uint8_t)source[i];
    return i;
}

// The number of leading ASCII bytes (below 0x80), each written as its char; from the first 16 with another byte, one
// at a time.
NINO_KERNEL int nino_wasm_widen_ascii(const uint8_t* source, uint16_t* destination, int length)
{
    int i = 0;
#ifdef NINO_SIMD
    if (length >= 16)
    {
        for (;;)
        {
            u8x16 v = LOAD(source + i);
            u64x2 high = (u64x2)(v & (uint8_t)0x80);
            if (high[0] | high[1])
                break;
            STORE(destination + i, (u8x16)__builtin_convertvector(
                (u8x8)__builtin_shufflevector(v, v, 0, 1, 2, 3, 4, 5, 6, 7), u16x8));
            STORE(destination + i + 8, (u8x16)__builtin_convertvector(
                (u8x8)__builtin_shufflevector(v, v, 8, 9, 10, 11, 12, 13, 14, 15), u16x8));
            if (i + 16 == length)
                return length;
            i = i + 32 <= length ? i + 16 : length - 16;
        }
    }
#endif
    for (; i < length && source[i] < 0x80; i++)
        destination[i] = source[i];
    return i;
}

// The kernels, in NinoWasm's order (Permute, Narrow, Widen, NarrowAscii, WidenAscii): 1 with the five written to
// table, 0 (nothing written) in a player built without SIMD, where Nino keeps its own paths.
int nino_wasm_kernels(void** table)
{
#ifdef NINO_SIMD
    table[0] = (void*)nino_wasm_permute;
    table[1] = (void*)nino_wasm_narrow;
    table[2] = (void*)nino_wasm_widen;
    table[3] = (void*)nino_wasm_narrow_ascii;
    table[4] = (void*)nino_wasm_widen_ascii;
    return 1;
#else
    (void)table;
    return 0;
#endif
}
