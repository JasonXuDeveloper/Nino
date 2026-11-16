#if NET8_0_OR_GREATER
using System;
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Nino.Core.CompilerServices;

namespace Nino.Core.Internal
{
    /// <summary>Read-only snapshots of the published chain of an exact CoreLib concurrent stack.</summary>
    internal static class ConcurrentStackNodes<T>
    {
#pragma warning disable CS0649 // Mirrors, never instantiated with assigned fields.
        private sealed class StackView
        {
            public Node Head;
        }

        private sealed class Node
        {
            public T Value;
            public Node Next;
        }
#pragma warning restore CS0649

        public static readonly bool Usable = ObjectTypes.Passes(Check);

        /// <summary>Writes raw elements from bottom to top, or returns false when this layout is unavailable.</summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        public static bool TryWrite(ConcurrentStack<T> stack, ref Writer writer)
        {
            if (!Usable)
                return false;

            // Push publishes new nodes, and pop and clear only replace the head. Both passes see this one chain.
            var head = Volatile.Read(ref Unsafe.As<StackView>(stack).Head);
            long length = 0;
            for (var node = head; node != null; node = node.Next)
                length++;
            if (length > int.MaxValue)
                NinoThrow.TooLarge();
            int count = (int)length, stride = Unsafe.SizeOf<T>();
            ref byte first = ref writer.ReserveCounted(count, stride);
            ref byte end = ref Unsafe.Add(ref first, count * stride);
            for (var node = head; node != null; node = node.Next)
            {
                end = ref Unsafe.Subtract(ref end, stride);
                NinoUnsafe.WriteValueIn(ref end, 0, in node.Value);
            }

            return true;
        }

        [UnconditionalSuppressMessage("Trimming", "IL2075",
            Justification = "Private metadata is optional. Missing or changed fields leave the public snapshot path enabled.")]
        private static bool Check()
        {
            // The version gate is not redundant with the field checks below: they prove the layout, not that popping
            // and clearing only replace the head and leave a published chain intact, which was read in the .NET 8
            // sources; ConcurrentStack.cs is the same file, byte for byte, in release/8.0 to release/11.0. Later
            // versions are trusted to keep that (the owner's call): CI runs the tests that prove it
            // (ConcurrentStackKeepsTheCapturedChainAcrossPopsAndCompaction) on each new runtime it adds.
            if (!RuntimeFeature.IsDynamicCodeSupported || Environment.Version.Major < 8 ||
                Type.GetType("Mono.RuntimeStructs") != null || NinoUnsafe.IsReferenceOrContainsReferences<T>())
                return false;

            const BindingFlags fields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic |
                                        BindingFlags.DeclaredOnly;
            var stackFields = typeof(ConcurrentStack<T>).GetFields(fields);
            if (stackFields.Length != 1 || stackFields[0].Name != "_head" ||
                Array.IndexOf(stackFields[0].GetRequiredCustomModifiers(), typeof(IsVolatile)) < 0)
                return false;
            var nodeType = stackFields[0].FieldType;
            if (!nodeType.IsSealed || !nodeType.IsAutoLayout || nodeType.BaseType != typeof(object))
                return false;
            var nodeFields = nodeType.GetFields(fields);
            if (nodeFields.Length != 2 || nodeFields[0].Name != "_value" || nodeFields[0].FieldType != typeof(T) ||
                !nodeFields[0].IsInitOnly || nodeFields[1].Name != "_next" || nodeFields[1].FieldType != nodeType)
                return false;

            var probe = new ConcurrentStack<T>();
            var values = new T[3];
            for (int i = 0; i < values.Length; i++)
                Unsafe.As<T, byte>(ref values[i]) = (byte)(i + 1);
            probe.Push(values[0]);
            probe.PushRange(values, 1, 2);
            var expected = probe.ToArray();
            object head = stackFields[0].GetValue(probe);
            var view = new StackView();
            if (!ObjectFields.Holds(probe, ObjectFields.OffsetOf(view, ref view.Head), head))
                return false;

            // Pop, range pop and clear must leave the captured nodes intact.
            if (!probe.TryPop(out _) || probe.TryPopRange(values, 0, 1) != 1)
                return false;
            probe.Clear();
            var mirror = new Node();
            int next = ObjectFields.OffsetOf(mirror, ref mirror.Next);
            int item = ObjectFields.OffsetOf(mirror, ref mirror.Value);
            foreach (T value in expected)
            {
                if (head == null || head.GetType() != nodeType)
                    return false;
                object tail = nodeFields[1].GetValue(head);
                if (!ObjectFields.Holds(head, next, tail) ||
                    !MemoryMarshal.CreateReadOnlySpan(ref ObjectFields.At<byte>(head, item), Unsafe.SizeOf<T>())
                        .SequenceEqual(MemoryMarshal.CreateReadOnlySpan(
                            ref Unsafe.As<T, byte>(ref Unsafe.AsRef(in value)), Unsafe.SizeOf<T>())))
                    return false;
                head = tail;
            }

            return head == null;
        }
    }
}
#endif
