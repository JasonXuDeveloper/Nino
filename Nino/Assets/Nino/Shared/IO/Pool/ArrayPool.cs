using System;
using System.Collections.Generic;

namespace Nino.Shared.IO
{
    public static class ArrayPool<T>
    {
        /// <summary>
        /// Shared pool
        /// </summary>
        private static volatile Dictionary<int, UncheckedStack<T[]>> _pool = new Dictionary<int, UncheckedStack<T[]>>(3);

        /// <summary>
        /// Check pool size
        /// </summary>
        /// <param name="size"></param>
        private static void CheckPool(int size)
        {
            if (!_pool.TryGetValue(size, out _))
            {
                //new queue
                _pool.Add(size, new UncheckedStack<T[]>());
            }
        }
        
        /// <summary>
        /// Request a T arr with internal length of size
        /// </summary>
        /// <param name="size"></param>
        /// <returns></returns>
        public static T[] Request(int size)
        {
            CheckPool(size);
            var queue = _pool[size];
            //get from queue
            if (queue.Count > 0)
            {
                var ret = queue.Pop();
                //double check
                if (ret.Length != size)
                {
                    Array.Resize(ref ret, size);
                }
                return ret;
            }
            //return new obj[]
            return new T[size];
        }

        /// <summary>
        /// Return arr to pool
        /// </summary>
        /// <param name="size"></param>
        /// <param name="arr"></param>
        public static void Return(int size, T[] arr)
        {
            CheckPool(size);
            _pool[size].Push(arr);
        }

        /// <summary>
        /// Return arr to pool
        /// </summary>
        /// <param name="arr"></param>
        public static void Return(T[] arr)
        {
            Return(arr.Length, arr);
        }
    }
}