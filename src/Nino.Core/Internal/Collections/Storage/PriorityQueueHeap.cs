#if NET10_0_OR_GREATER
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using Nino.Core.CompilerServices;

namespace Nino.Core.Internal
{
    internal static class PriorityQueueHeap
    {
        internal static readonly bool Usable = ObjectTypes.Passes(Check);

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool TryWrite(PriorityQueue<int, int> queue, int count, ref Writer writer)
        {
            if (!Usable)
                return false;

            int version = Fields<int, int>.Version(queue);
            var nodes = Fields<int, int>.Nodes(queue);
            if (nodes.Length < count || Fields<int, int>.Size(queue) != count)
                NinoThrow.ModifiedDuringWrite();
            ref byte target = ref writer.ReserveCounted(count, 8);
            ref byte source = ref Unsafe.As<(int, int), byte>(ref NinoUnsafe.GetArrayDataReference(nodes));
            NinoUnsafe.CopyToNew(ref target, ref source, (uint)(count * 8));
            if (Fields<int, int>.Version(queue) != version)
                NinoThrow.ModifiedDuringWrite();
            return true;
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        internal static bool TryAdopt(PriorityQueue<int, int> queue, int count, ref Reader reader)
        {
            if (!Usable || !ReferenceEquals(queue.Comparer, Comparer<int>.Default))
                return false;
            var nodes = Fields<int, int>.Nodes(queue);
            if (nodes.Length < count || (ulong)(uint)count * 8 > (uint)reader.Remaining)
                return false;
            // The default comparer leaves a node in place when its priority equals its parent.
            ref byte source = ref reader.Peek();
            ref byte target = ref Unsafe.As<(int, int), byte>(ref NinoUnsafe.GetArrayDataReference(nodes));
            int i = 1;
            int copied = 0;
            if (AdvSimd.Arm64.IsSupported || Sse2.IsSupported)
            {
                // Until Size is published, a rejected group leaves an empty queue for the enqueue fallback.
                NinoUnsafe.Write(ref target, NinoUnsafe.Read<long>(ref source));
                ref byte children = ref Unsafe.Add(ref source, 8);
                ref byte destination = ref Unsafe.Add(ref target, 8);
                ref byte parentPriority = ref Unsafe.Add(ref source, 4);
                for (; i <= count - 4; i += 4)
                {
                    var a = Vector128.LoadUnsafe(ref children).AsInt32();
                    var b = Vector128.LoadUnsafe(ref children, 16).AsInt32();
                    int parent = NinoUnsafe.Read<int>(ref parentPriority);
                    if (Vector128.GreaterThanAny(Vector128.Create(parent), NinoSimd.OddInt32(a, b)))
                        return false;
                    a.AsByte().StoreUnsafe(ref destination);
                    b.AsByte().StoreUnsafe(ref destination, 16);
                    children = ref Unsafe.Add(ref children, 32);
                    destination = ref Unsafe.Add(ref destination, 32);
                    parentPriority = ref Unsafe.Add(ref parentPriority, 8);
                }

                copied = i;
            }

            for (; i < count; i++)
            {
                ref byte child = ref Unsafe.Add(ref source, i * 8);
                if (NinoUnsafe.Read<int>(ref Unsafe.Add(ref child, 4)) <
                    NinoUnsafe.Read<int>(ref Unsafe.Add(ref source, ((i - 1) >> 2) * 8 + 4)))
                    return false;
            }

            if (copied != count)
                NinoUnsafe.CopyToNew(ref Unsafe.Add(ref target, copied * 8), ref Unsafe.Add(ref source, copied * 8),
                    (uint)((count - copied) * 8));
            Fields<int, int>.Size(queue) = count;
            Fields<int, int>.Version(queue) += count;
            reader.Advance(count * 8);
            return true;
        }

        private static bool Check()
        {
            var queue = new PriorityQueue<int, int>(8);
            for (int i = 0; i < 8; i++)
                queue.Enqueue(20 + i, i);
            var nodes = Fields<int, int>.Nodes(queue);
            if (nodes.Length != 8 || Fields<int, int>.Size(queue) != 8 || Fields<int, int>.Version(queue) != 8 ||
                Unsafe.SizeOf<(int, int)>() != 8)
                return false;

            ref byte first = ref Unsafe.As<(int, int), byte>(ref nodes[0]);
            if (NinoUnsafe.Read<int>(ref first) != 20 || NinoUnsafe.Read<int>(ref Unsafe.Add(ref first, 4)) != 0)
                return false;

            queue.Enqueue(99, -1);
            nodes = Fields<int, int>.Nodes(queue);
            return queue.Count == 9 && Fields<int, int>.Version(queue) == 9 &&
                   nodes[0] == (99, -1) && nodes[8] == (21, 1);
        }

        private static class Fields<TElement, TPriority>
        {
            [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_nodes")]
            internal static extern ref (TElement, TPriority)[] Nodes(PriorityQueue<TElement, TPriority> queue);
            [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_size")]
            internal static extern ref int Size(PriorityQueue<TElement, TPriority> queue);
            [UnsafeAccessor(UnsafeAccessorKind.Field, Name = "_version")]
            internal static extern ref int Version(PriorityQueue<TElement, TPriority> queue);
        }
    }
}
#endif
