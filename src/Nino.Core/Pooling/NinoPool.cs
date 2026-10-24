using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Nino.Core.Generated;

namespace Nino.Core
{
    /// <summary>
    /// Makes Nino reuse instances of a sealed <c>[NinoType]</c> class. The generator adds to the (partial) class:
    /// <list type="bullet">
    /// <item><c>ReturnToPool()</c>: hands the instance back once you are done with it. Returning it twice throws.</item>
    /// <item>two partial methods you must implement: <c>private static partial IEnumerable&lt;T&gt; OnNinoPoolWarmup()</c>,
    /// called once before Nino first deserializes the type, whose instances go straight into the pool (return none
    /// to start empty), and <c>private partial void OnNinoPoolReturn()</c>, called by <c>ReturnToPool</c> to release
    /// what must not outlive this use (state Nino does not deserialize, resources from other pools).</item>
    /// </list>
    /// Wherever Nino deserializes the type (top level, members, collection elements) it takes a pooled instance if
    /// there is one and deserializes into it, reusing its lists, arrays and nested objects as well; otherwise it
    /// creates one as usual. Instances that reuse deserialization drops (the data holds <c>null</c>, a collection
    /// shrinks) are returned to the pool automatically.
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class NinoPooledAttribute : Attribute
    {
    }

    /// <summary>Implemented by the generator for <see cref="NinoPooledAttribute"/> classes.</summary>
    public interface INinoPooled
    {
        /// <summary>Returns the instance to its pool. Do not use it afterwards.</summary>
        void ReturnToPool();
    }

    /// <summary>
    /// The instances of a <see cref="NinoPooledAttribute"/> class waiting to be reused (<see cref="Shared"/>). Thread
    /// safe. Each thread keeps a stack of its own, held by the pooled type itself (a <c>[ThreadStatic]</c> field
    /// there is a direct thread-local access, one here would go through shared generic code), so a thread that
    /// returns what it deserializes never takes a lock; a full thread stack moves half of itself to the shared
    /// stack and an empty one takes half a stack back, one lock per batch.
    /// </summary>
    public sealed class NinoPool<T> : NinoPoolLine where T : class, INinoPooled
    {
        private const int ThreadCapacity = 64;

        private readonly object _lock = new object();
        private T[] _items = new T[ThreadCapacity];
        private int _count;
        private int _maxRetained = NinoSettings.DefaultPoolMaxRetained;

        private NinoPool()
        {
            // From now on a dropped object may be a pooled instance (see NinoRecycler<T>.MayRecycle).
            NinoRecycler.PoolsInUse = true;
        }

        /// <summary>The pool of <typeparamref name="T"/>.</summary>
        public static NinoPool<T> Shared { get; } = new NinoPool<T>();

        /// <summary>
        /// Most instances kept by the shared stack; more are left to the garbage collector. Set through
        /// <see cref="NinoSettings.SetPoolMaxRetained{T}"/> (or <see cref="NinoSettings.DefaultPoolMaxRetained"/>).
        /// </summary>
        public int MaxRetained
        {
            get => Volatile.Read(ref _maxRetained);
            internal set => Volatile.Write(ref _maxRetained, value);
        }

        /// <summary>Instances in the shared stack (not counting those the threads keep).</summary>
        public int Count
        {
            get
            {
                lock (_lock)
                    return _count;
            }
        }

        /// <summary>Drops every pooled instance: the shared stack now, and each thread's own on its next use.</summary>
        public void Clear()
        {
            lock (_lock)
            {
                Array.Clear(_items, 0, _count);
                _count = 0;
                Volatile.Write(ref Generation, Generation + 1);
            }
        }

        /// <summary>For generated code: one thread's instances.</summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public sealed class PerThread
        {
            internal readonly T[] Items = new T[ThreadCapacity];
            internal int Count;
            internal int Generation;
        }

        /// <summary>For generated code: true for the first caller only, which then runs the warmup.</summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool BeginWarmup() =>
            Volatile.Read(ref WarmedUp) == 0 && Interlocked.Exchange(ref WarmedUp, 1) == 0;

        /// <summary>
        /// For generated code: false until an instance is first added, while every stack is empty; a take then answers
        /// null without the thread's own stack (a thread-local access, the most of an empty take's time on Arm64).
        /// </summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public bool EverFilled
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get => Filled != 0;
        }

        /// <summary>For generated code: a pooled instance, or null when the pool is empty.</summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T TryTake(ref PerThread stack)
        {
            var s = stack;
            if (s != null && s.Count != 0 && s.Generation == Generation)
            {
                // Count is within [0, ThreadCapacity], the length of Items: no bounds checks.
                ref T item = ref Unsafe.Add(ref NinoUnsafe.GetArrayDataReference(s.Items), --s.Count);
                var value = item;
                item = null;
                return value;
            }

            // An empty pool (every read until something is returned) answers here, without a call.
            return Volatile.Read(ref _count) == 0 ? null : TakeShared(ref stack);
        }

        /// <summary>For generated code: adds an instance that <c>ReturnToPool</c> has checked.</summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Add(T value, ref PerThread stack)
        {
            var s = stack;
            if (s != null && s.Count != ThreadCapacity && s.Generation == Generation)
                Unsafe.Add(ref NinoUnsafe.GetArrayDataReference(s.Items), s.Count++) = value;
            else
                AddSlow(value, ref stack);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private T TakeShared(ref PerThread stack)
        {
            var s = Current(ref stack);
            lock (_lock)
            {
                // Half a stack: the next takes on this thread are lock free.
                int n = Math.Min(_count, ThreadCapacity / 2);
                if (n == 0)
                    return null;
                _count -= n;
                Array.Copy(_items, _count, s.Items, 0, n);
                Array.Clear(_items, _count, n);
                s.Count = n - 1;
                var value = s.Items[n - 1];
                s.Items[n - 1] = null;
                return value;
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void AddSlow(T value, ref PerThread stack)
        {
            // The first add of each thread comes here (its stack is created here).
            if (Filled == 0)
                Volatile.Write(ref Filled, 1);
            var s = Current(ref stack);
            if (s.Count == ThreadCapacity)
            {
                // The older half goes to the shared stack (or to the garbage collector once that is full).
                const int half = ThreadCapacity / 2;
                lock (_lock)
                {
                    int n = Math.Min(half, Math.Max(0, MaxRetained - _count));
                    if (_count + n > _items.Length)
                        Array.Resize(ref _items, Math.Max(_items.Length * 2, _count + n));
                    Array.Copy(s.Items, 0, _items, _count, n);
                    _count += n;
                }

                Array.Copy(s.Items, half, s.Items, 0, half);
                Array.Clear(s.Items, half, half);
                s.Count = half;
            }

            s.Items[s.Count++] = value;
        }

        /// <summary>The calling thread's stack, created or emptied after a <see cref="Clear"/> as needed.</summary>
        private PerThread Current(ref PerThread stack)
        {
            var s = stack ??= new PerThread();
            int generation = Volatile.Read(ref Generation);
            if (s.Generation != generation)
            {
                Array.Clear(s.Items, 0, s.Count);
                s.Count = 0;
                s.Generation = generation;
            }

            return s;
        }

        /// <summary>For generated code.</summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public static void ThrowReturnedTwice() =>
            throw new InvalidOperationException(
                $"Nino: this {typeof(T).Name} is already in its pool; an instance can be returned once per use.");
    }

    /// <summary>
    /// For <see cref="NinoPool{T}"/>: the fields every take and add reads, on a 64-byte cache line of their own (56
    /// bytes of padding before them, the object header included, and 56 after), away from the shared stack's count
    /// and the lock that batch transfers write, and from neighbouring objects. A non-generic base class, because
    /// generic classes cannot have an explicit layout.
    /// </summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    [StructLayout(LayoutKind.Explicit)]
    public abstract class NinoPoolLine
    {
        // Bumped by Clear: thread stacks filled before it drop their instances.
        [FieldOffset(56)] internal int Generation;
        [FieldOffset(60)] internal int WarmedUp;
        // Set by the first add: until then every stack is empty.
        [FieldOffset(64)] internal int Filled;
        [FieldOffset(120)] private int _end;

        private protected NinoPoolLine()
        {
            _end = 0;
        }
    }
}
