using System;
using System.Collections.Generic;

namespace Nino.Shared.IO
{
    public static class ExtensibleObjectPool
    {
        /// <summary>
        /// Shared pool
        /// </summary>
        private static volatile Dictionary<int, Queue<object[]>> _pool = new Dictionary<int, Queue<object[]>>();

        /// <summary>
        /// Check pool size
        /// </summary>
        /// <param name="size"></param>
        private static void CheckPool(int size)
        {
            if (!_pool.TryGetValue(size, out _))
            {
                //new queue
                _pool.Add(size, new Queue<object[]>());
            }
        }
        
        /// <summary>
        /// Request a object arr with internal length of size
        /// </summary>
        /// <param name="size"></param>
        /// <returns></returns>
        public static object[] RequestObjArr(int size)
        {
            CheckPool(size);
            var queue = _pool[size];
            //get from queue
            if (queue.Count > 0)
            {
                var ret = queue.Dequeue();
                //double check
                if (ret.Length != size)
                {
                    Array.Resize(ref ret, size);
                }
                return ret;
            }
            //return new obj[]
            return new object[size];
        }

        /// <summary>
        /// Return arr to pool
        /// </summary>
        /// <param name="size"></param>
        /// <param name="arr"></param>
        public static void ReturnObjArr(int size, object[] arr)
        {
            CheckPool(size);
            _pool[size].Enqueue(arr);
        }

        /// <summary>
        /// Return arr to pool
        /// </summary>
        /// <param name="arr"></param>
        public static void ReturnObjArr(object[] arr)
        {
            ReturnObjArr(arr.Length, arr);
        }
    }
}