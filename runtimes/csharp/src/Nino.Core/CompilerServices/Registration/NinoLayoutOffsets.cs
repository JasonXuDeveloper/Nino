using System;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Nino.Core.Internal;

namespace Nino.Core.CompilerServices
{
    /// <summary>
    /// The field offsets and sizes of generic structs as the code compiled for them lays them out, registered by
    /// generated registries for <see cref="NinoLayout"/>. IL2CPP places the fields of some generic structs in one way
    /// for reflection and shared generic code and in another for the code compiled for the type
    /// (<see cref="NinoReflection.Il2CppSplitLayoutIn"/>); the formatters the generator registers are compiled code, so
    /// the layout they copy is the one measured here, in the registry's own compiled code, never by reflection. Every
    /// runtime reads these tables, so the CoreCLR tests check them against reflection.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static class NinoLayoutOffsets
    {
        private static readonly ConcurrentDictionary<Type, Table> Tables = NinoUnload.Cache<Table>();

        /// <summary>A struct's size and its instance fields' offsets, in the order reflection lists the fields.</summary>
        internal sealed class Table
        {
            public readonly int Size;
            public readonly int[] Offsets;

            public Table(int size, int[] offsets)
            {
                Size = size;
                Offsets = offsets;
            }
        }

        /// <summary>
        /// Registers the offsets of <typeparamref name="T"/>'s instance fields, in declaration order (<see cref="Of"/>
        /// of each), with its size as this code compiles it.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public static void Register<T>(params int[] offsets) where T : struct
        {
            if (offsets == null)
                throw new ArgumentNullException(nameof(offsets));
            Tables[typeof(T)] = new Table(Unsafe.SizeOf<T>(), offsets);
        }

        /// <summary>
        /// Registers <c><typeparamref name="T"/>?</c>: its flag first, its value where <c>T?</c> and <typeparamref name="T"/>
        /// differ in size, as this code compiles it.
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public static void RegisterNullable<T>() where T : struct =>
            Tables[typeof(T?)] = new Table(Unsafe.SizeOf<T?>(), new[] { 0, Unsafe.SizeOf<T?>() - Unsafe.SizeOf<T>() });

        /// <summary>The offset of <paramref name="field"/> in <paramref name="value"/>.</summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Of<T, TField>(ref T value, ref TField field) =>
            (int)Unsafe.ByteOffset(ref Unsafe.As<T, byte>(ref value), ref Unsafe.As<TField, byte>(ref field));

        /// <summary>Whether no table is registered: no generated code names such a struct (almost every program).</summary>
        internal static bool IsEmpty => Tables.IsEmpty;

        /// <summary>The table registered for <paramref name="type"/>, or null.</summary>
        internal static Table Find(Type type) => Tables.TryGetValue(type, out var table) ? table : null;
    }
}
