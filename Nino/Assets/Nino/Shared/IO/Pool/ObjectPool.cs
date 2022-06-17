namespace Nino.Shared.IO
{
    public static class ObjectPool<T> where T: new()
    {
        /// <summary>
        /// A shared buffer queue
        /// </summary>
        private static volatile UncheckedStack<T> _pool = new UncheckedStack<T>(3);

        /// <summary>
        /// Request an obj
        /// </summary>
        /// <returns></returns>
        public static T Request()
        {
            T ret;
            if (_pool.Count > 0)
            {
                ret = _pool.Pop();
                return ret;
            }
            else
            {
                ret = new T();
            }

            return ret;
        }

        /// <summary>
        /// Return an obj to pool
        /// </summary>
        /// <param name="obj"></param>
        public static void Return(T obj)
        {
            _pool.Push(obj);
        }
    }
}