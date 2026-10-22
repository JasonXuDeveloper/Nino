using System;
using System.Runtime.CompilerServices;

namespace Nino.Core.Internal
{
    /// <summary>
    /// Objects of a runtime's own classes made from instances of mirror classes, classes that declare the same fields in
    /// the same order (the nodes of <c>SortedSet&lt;T&gt;</c>, the immutable trees of Nino.Immutable): allocated with
    /// <c>new</c>, then given the real type. Compiled into Nino.Core and Nino.Immutable each, as internal code of
    /// both, so neither depends on the other's internals.
    /// </summary>
    internal static class ObjectTypes
    {
        // Before Usable: static fields are initialized in order. No static constructor, which would add a check before
        // every use.
        private static readonly int Header = FindHeader();

        /// <summary>Whether <see cref="Retype"/> works on this runtime: its object header was found.</summary>
        public static readonly bool Usable = Header != 0;

        // What type an object is: the word before its first field and any other header word, which CoreCLR (the method
        // table, one word) and Mono and IL2CPP (the vtable or class, then the monitor: two words) keep at the address a
        // reference holds. Found from two classes of one byte field: the word that differs between them and not
        // between two instances of one.
        private sealed class Probe
        {
#pragma warning disable CS0649 // read through Unsafe.As
            public byte Data;
#pragma warning restore CS0649
        }

        private sealed class OtherProbe
        {
#pragma warning disable CS0649, CS0169 // laid out only
            public byte Data;
#pragma warning restore CS0649, CS0169
        }

        private static int FindHeader()
        {
            foreach (int header in new[] { IntPtr.Size, 2 * IntPtr.Size })
            {
                IntPtr type = TypeWord(new Probe(), header), other = TypeWord(new OtherProbe(), header);
                if (type == IntPtr.Zero || type != TypeWord(new Probe(), header) || type == other)
                    continue;
                object instance = new OtherProbe();
                TypeWord(instance, header) = type;
                if (TypeOfInstance(instance) == typeof(Probe))
                    return header;
            }

            return 0;
        }

        // Out of line: inlined, the JIT knows the type it allocated and takes that for GetType's (Arm32).
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Type TypeOfInstance(object instance) => instance.GetType();

        private static ref IntPtr TypeWord(object instance, int header) =>
            ref Unsafe.As<byte, IntPtr>(ref Unsafe.Subtract(ref Unsafe.As<Probe>(instance).Data, header));

        /// <summary>The word that says what type <paramref name="instance"/> is.</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static IntPtr TypeOf(object instance) => TypeWord(instance, Header);

        /// <summary>
        /// Makes <paramref name="instance"/>, a new instance of a class laid out as the type <paramref name="type"/>
        /// says (the same fields in the same order: the same size and the same references where the GC looks for
        /// them), an instance of that type: a plain allocation instead of an uninitialized object made by type
        /// (RuntimeHelpers.GetUninitializedObject: 13 ns on .NET 10 but 77 on .NET 8, and FormatterServices on Mono:
        /// 59), and nothing a runtime without them (IL2CPP) could lack.
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void Retype(object instance, IntPtr type) => TypeWord(instance, Header) = type;
    }
}
