using System;
using System.ComponentModel;
using System.Threading;

namespace Nino.Core
{
    /// <summary>
    /// Makes Nino reuse instances of a sealed <c>[NinoType]</c> class. The generator adds to the (partial) class:
    /// <list type="bullet">
    /// <item><c>ReturnToPool()</c>: hands the instance back once you are done with it. Returning it twice throws.</item>
    /// <item>two partial methods you must implement: <c>static partial void OnNinoPoolWarmup()</c>, called once
    /// before Nino first deserializes the type (e.g. <c>new T().ReturnToPool()</c> a few times, or nothing), and
    /// <c>partial void OnNinoPoolReturn()</c>, called by <c>ReturnToPool</c> to release what must not outlive this
    /// use (state Nino does not deserialize, resources from other pools).</item>
    /// </list>
    /// Wherever Nino deserializes the type (top level, members, collection elements) it takes a pooled instance if
    /// there is one and deserializes into it, reusing its lists, arrays and nested objects as well; otherwise it
    /// creates one as usual. Instances that ref deserialization drops (the data holds <c>null</c>, a collection
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
    /// The instances of a <see cref="NinoPooledAttribute"/> class waiting to be reused. Thread safe; each thread
    /// also keeps one instance of its own, so a return followed by a deserialization on one thread takes no lock.
    /// </summary>
    public static class NinoPool<T> where T : class, INinoPooled
    {
        [ThreadStatic] private static T _local;
        private static readonly object Lock = new object();
        private static T[] _items = new T[8];
        private static int _count;
        private static int _maxRetained = 256;
        private static int _warmedUp;

        /// <summary>Most instances kept by the shared pool; more are left to the garbage collector. Default 256.</summary>
        public static int MaxRetained
        {
            get => _maxRetained;
            set
            {
                if (value < 0)
                    throw new ArgumentOutOfRangeException(nameof(value));
                _maxRetained = value;
            }
        }

        /// <summary>Instances in the shared pool (not counting the one each thread keeps).</summary>
        public static int Count
        {
            get
            {
                lock (Lock)
                    return _count;
            }
        }

        /// <summary>Drops the instances of the shared pool and the calling thread's own.</summary>
        public static void Clear()
        {
            _local = null;
            lock (Lock)
            {
                Array.Clear(_items, 0, _count);
                _count = 0;
            }
        }

        /// <summary>For generated code: true for the first caller only, which then runs the warmup.</summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public static bool BeginWarmup() =>
            Volatile.Read(ref _warmedUp) == 0 && Interlocked.Exchange(ref _warmedUp, 1) == 0;

        /// <summary>For generated code: a pooled instance, or null when the pool is empty.</summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public static T TryTake()
        {
            var value = _local;
            if (value != null)
            {
                _local = null;
                return value;
            }

            if (Volatile.Read(ref _count) == 0)
                return null;
            lock (Lock)
            {
                if (_count == 0)
                    return null;
                value = _items[--_count];
                _items[_count] = null;
                return value;
            }
        }

        /// <summary>For generated code: adds an instance that <c>ReturnToPool</c> has checked.</summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public static void Add(T value)
        {
            NinoRecycler.PoolsInUse = true;
            if (_local == null)
            {
                _local = value;
                return;
            }

            lock (Lock)
            {
                if (_count >= _maxRetained)
                    return;
                if (_count == _items.Length)
                    Array.Resize(ref _items, _count * 2);
                _items[_count++] = value;
            }
        }

        /// <summary>For generated code.</summary>
        [EditorBrowsable(EditorBrowsableState.Never)]
        public static void ThrowReturnedTwice() =>
            throw new InvalidOperationException(
                $"Nino: this {typeof(T).Name} is already in its pool; an instance can be returned once per use.");
    }
}
