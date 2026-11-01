using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nino.Fuzz
{
    /// <summary>
    /// Finds freed heap memory in a value after a rejected read into it. Nino allocates large arrays of unmanaged
    /// elements uninitialized (NinoUnsafe.NewArray, from 6 KB); one that takes its slot in the caller's object before
    /// a check that can still reject the data (a bool, a has-value byte, an Untrusted mode check) leaves the object
    /// holding whatever the memory held before. Such bytes are valid values, so no other check sees them: here the
    /// freed memory first holds a pattern (<see cref="Poison"/>), and <see cref="Find"/> looks for it in every array
    /// of unmanaged elements the value reaches, through private fields too (a list's backing array, a dictionary's
    /// entries).
    /// </summary>
    public static class StaleMemory
    {
        /// <summary>
        /// The byte at address <c>a</c> of poisoned memory is <c>Pattern[a % 8]</c>: none is 0 or 1 (a bool), and
        /// input bytes cannot line up with it by chance (the mutator only copies bytes of the seeds, which hold none of
        /// it, and random ones).
        /// </summary>
        static readonly byte[] Pattern = { 0xCD, 0x9A, 0x5E, 0xB3, 0x47, 0xE1, 0x2C, 0x78 };

        /// <summary>A 64-byte line with this many poisoned bytes is stale (random bytes match 1 in 256).</summary>
        const int Threshold = 16;

        /// <summary>
        /// Fills freed memory with the pattern: 8 MB of arrays on the large object heap, dropped, then a full
        /// collection, after which the next large arrays the read allocates uninitialized take that memory as it is
        /// (<see cref="Samples.Stale"/>: the gen0 allocator clears smaller ones ahead, most of the time).
        /// </summary>
        public static void Poison()
        {
            Fill(32, 256 << 10);
            GC.Collect();

            static void Fill(int count, int size)
            {
                for (int i = 0; i < count; i++)
                {
                    var garbage = GC.AllocateUninitializedArray<byte>(size);
                    unsafe
                    {
                        fixed (byte* p = garbage)
                        {
                            // Bytes up to an 8-byte boundary, then whole words of the pattern.
                            int head = (int)((8 - ((nuint)p & 7)) & 7);
                            for (int j = 0; j < head; j++)
                                p[j] = Pattern[(int)((nuint)(p + j) & 7)];
                            new Span<ulong>(p + head, (size - head) / 8).Fill(BitConverter.ToUInt64(Pattern));
                        }
                    }
                }
            }
        }

        /// <summary>
        /// The first array of unmanaged elements reachable from <paramref name="root"/> that holds poisoned memory,
        /// described as its path and how many of its bytes are poisoned; null when there is none.
        /// </summary>
        public static string Find(object root)
        {
            var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
            var stack = new Stack<(object Value, string Path)>();
            stack.Push((root, root?.GetType().Name ?? ""));
            while (stack.Count > 0)
            {
                var (o, path) = stack.Pop();
                if (o == null || seen.Count > 1_000_000 || !seen.Add(o)) continue;
                var t = o.GetType();
                if (t.IsPrimitive || t.IsEnum || o is string || o is MemberInfo || o is Delegate || o is Pointer) continue;
                if (o is Array array)
                {
                    var element = t.GetElementType();
                    if (!TypeLayout.HoldsReferences(element))
                    {
                        long stale = Poisoned(array, element);
                        if (stale > 0)
                            return $"{path} ({Name(t)}, {array.Length} elements): {stale} bytes in 64-byte lines that hold the poison pattern";
                        continue;
                    }
                    if (array.Rank == 1)
                        for (int i = 0; i < array.Length; i++)
                            stack.Push((array.GetValue(i), path + "[]"));
                    continue;
                }
                for (var type = t; type != null; type = type.BaseType)
                    foreach (var f in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                        if (!f.FieldType.IsPrimitive && !f.FieldType.IsPointer && !f.FieldType.IsByRefLike)
                            stack.Push((f.GetValue(o), path + "." + f.Name));
            }
            return null;
        }

        /// <summary>The bytes of <paramref name="array"/> in 64-byte lines (by address) with <see cref="Threshold"/> poisoned bytes or more.</summary>
        static unsafe long Poisoned(Array array, Type element)
        {
            long length = array.LongLength * TypeLayout.Size(element);
            long stale = 0;
            fixed (byte* start = &MemoryMarshal.GetArrayDataReference(array))
            {
                byte* end = start + length;
                for (byte* line = (byte*)((nuint)start & ~(nuint)63); line < end; line += 64)
                {
                    int matches = 0, bytes = 0;
                    for (byte* p = line; p < line + 64; p++)
                    {
                        if (p < start || p >= end) continue;
                        bytes++;
                        if (*p == Pattern[(int)((nuint)p & 7)]) matches++;
                    }
                    if (matches >= Threshold) stale += bytes;
                }
            }
            return stale;
        }

        static string Name(Type t) => t.IsArray ? Name(t.GetElementType()) + "[]" :
            t.IsGenericType ? t.Name.Split('`')[0] + "<" + string.Join(", ", Array.ConvertAll(t.GetGenericArguments(), Name)) + ">" : t.Name;
    }

    /// <summary>What <c>RuntimeHelpers.IsReferenceOrContainsReferences</c> and <c>Unsafe.SizeOf</c> say of a type known only at run time.</summary>
    public static class TypeLayout
    {
        static readonly Dictionary<Type, (bool References, int Size)> Layouts = new Dictionary<Type, (bool, int)>();
        static readonly MethodInfo Of = typeof(TypeLayout).GetMethod(nameof(Layout), BindingFlags.NonPublic | BindingFlags.Static);

        /// <summary>Whether a value of <paramref name="t"/> is or holds a reference (false: unmanaged, no collection inside).</summary>
        public static bool HoldsReferences(Type t) => Get(t).References;

        public static int Size(Type t) => Get(t).Size;

        static (bool References, int Size) Get(Type t)
        {
            if (!Layouts.TryGetValue(t, out var layout))
                Layouts[t] = layout = ((bool, int))Of.MakeGenericMethod(t).Invoke(null, null);
            return layout;
        }

        static (bool, int) Layout<T>() => (RuntimeHelpers.IsReferenceOrContainsReferences<T>(), Unsafe.SizeOf<T>());
    }
}
