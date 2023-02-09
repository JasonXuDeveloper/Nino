using System;
using System.Collections.Generic;
using Nino.Shared.IO;

namespace Nino.Serialization
{
    public static partial class Deserializer
    {
        #region Array

        /// <summary>
        /// Deserialize an array NinoSerialize object
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="data"></param>
        /// <param name="option"></param>
        /// <returns></returns>
        public static T[] DeserializeArray<T>(byte[] data, CompressOption option = CompressOption.Zlib)
            => DeserializeArray<T>(new Span<byte>(data), option);

        /// <summary>
        /// Deserialize an array NinoSerialize object
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="data"></param>
        /// <param name="option"></param>
        /// <returns></returns>
        public static T[] DeserializeArray<T>(ArraySegment<byte> data, CompressOption option = CompressOption.Zlib)
            => DeserializeArray<T>(new Span<byte>(data.Array, data.Offset, data.Count), option);

        /// <summary>
        /// Deserialize an array of NinoSerialize object
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="data"></param>
        /// <param name="option"></param>
        /// <returns></returns>
        public static T[] DeserializeArray<T>(Span<byte> data, CompressOption option = CompressOption.Zlib)
        {
            var type = typeof(T[]);
            var reader = ObjectPool<Reader>.Request();
            reader.Init(data, data.Length,
                TypeModel.IsNonCompressibleType(type) ? CompressOption.NoCompression : option);

            /*
             * NO GC DESERIALIZATION ATTEMPT
             */
            //basic type
            if (TryDeserializeWrapperType(type, reader, false, true, out T[] ret))
            {
                return ret;
            }

            //attempt to deserialize using generic method
            return reader.ReadArray<T>();
        }

        #endregion

        #region Nullable

        /// <summary>
        /// Deserialize a nullable of NinoSerialize struct
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="data"></param>
        /// <param name="option"></param>
        /// <returns></returns>
        public static T? DeserializeNullable<T>(byte[] data, CompressOption option = CompressOption.Zlib)
            where T : struct
            => DeserializeNullable<T>(new Span<byte>(data), option);

        /// <summary>
        /// Deserialize a nullable of NinoSerialize struct
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="data"></param>
        /// <param name="option"></param>
        /// <returns></returns>
        public static T? DeserializeNullable<T>(ArraySegment<byte> data, CompressOption option = CompressOption.Zlib)
            where T : struct
            => DeserializeNullable<T>(new Span<byte>(data.Array, data.Offset, data.Count), option);

        /// <summary>
        /// Deserialize a nullable of NinoSerialize struct
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="data"></param>
        /// <param name="option"></param>
        /// <returns></returns>
        public static T? DeserializeNullable<T>(Span<byte> data, CompressOption option = CompressOption.Zlib)
            where T : struct
        {
            var type = typeof(T?);
            var reader = ObjectPool<Reader>.Request();
            reader.Init(data, data.Length,
                TypeModel.IsNonCompressibleType(type) ? CompressOption.NoCompression : option);

            //attempt to deserialize using generic method
            return reader.ReadNullable<T>();
        }

        #endregion

        #region List

        /// <summary>
        /// Deserialize a list of NinoSerialize object
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="data"></param>
        /// <param name="option"></param>
        /// <returns></returns>
        public static List<T> DeserializeList<T>(byte[] data, CompressOption option = CompressOption.Zlib)
            => DeserializeList<T>(new Span<byte>(data), option);

        /// <summary>
        /// Deserialize a list of NinoSerialize object
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="data"></param>
        /// <param name="option"></param>
        /// <returns></returns>
        public static List<T> DeserializeList<T>(ArraySegment<byte> data, CompressOption option = CompressOption.Zlib)
            => DeserializeList<T>(new Span<byte>(data.Array, data.Offset, data.Count), option);

        /// <summary>
        /// Deserialize a list of NinoSerialize object
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="data"></param>
        /// <param name="option"></param>
        /// <returns></returns>
        public static List<T> DeserializeList<T>(Span<byte> data, CompressOption option = CompressOption.Zlib)
        {
            var type = typeof(List<T>);
            var reader = ObjectPool<Reader>.Request();
            reader.Init(data, data.Length,
                TypeModel.IsNonCompressibleType(type) ? CompressOption.NoCompression : option);

            /*
             * NO GC DESERIALIZATION ATTEMPT
             */
            //basic type
            if (TryDeserializeWrapperType(typeof(List<T>), reader, false, true, out List<T> ret))
            {
                return ret;
            }

            //attempt to deserialize using generic method
            return reader.ReadList<T>();
        }

        #endregion

        #region HashSet

        /// <summary>
        /// Deserialize a HashSet of NinoSerialize object
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="data"></param>
        /// <param name="option"></param>
        /// <returns></returns>
        public static HashSet<T> DeserializeHashSet<T>(byte[] data, CompressOption option = CompressOption.Zlib)
            => DeserializeHashSet<T>(new Span<byte>(data), option);

        /// <summary>
        /// Deserialize a HashSet of NinoSerialize object
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="data"></param>
        /// <param name="option"></param>
        /// <returns></returns>
        public static HashSet<T> DeserializeHashSet<T>(ArraySegment<byte> data,
            CompressOption option = CompressOption.Zlib)
            => DeserializeHashSet<T>(new Span<byte>(data.Array, data.Offset, data.Count), option);

        /// <summary>
        /// Deserialize a HashSet of NinoSerialize object
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="data"></param>
        /// <param name="option"></param>
        /// <returns></returns>
        public static HashSet<T> DeserializeHashSet<T>(Span<byte> data, CompressOption option = CompressOption.Zlib)
        {
            var type = typeof(HashSet<T>);
            var reader = ObjectPool<Reader>.Request();
            reader.Init(data, data.Length,
                TypeModel.IsNonCompressibleType(type) ? CompressOption.NoCompression : option);

            /*
             * NO GC DESERIALIZATION ATTEMPT
             */
            //basic type
            if (TryDeserializeWrapperType(typeof(HashSet<T>), reader, false, true, out HashSet<T> ret))
            {
                return ret;
            }

            //attempt to deserialize using generic method
            return reader.ReadHashSet<T>();
        }

        #endregion

        #region Queue

        /// <summary>
        /// Deserialize a Queue of NinoSerialize object
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="data"></param>
        /// <param name="option"></param>
        /// <returns></returns>
        public static Queue<T> DeserializeQueue<T>(byte[] data, CompressOption option = CompressOption.Zlib)
            => DeserializeQueue<T>(new Span<byte>(data), option);

        /// <summary>
        /// Deserialize a Queue of NinoSerialize object
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="data"></param>
        /// <param name="option"></param>
        /// <returns></returns>
        public static Queue<T> DeserializeQueue<T>(ArraySegment<byte> data, CompressOption option = CompressOption.Zlib)
            => DeserializeQueue<T>(new Span<byte>(data.Array, data.Offset, data.Count), option);

        /// <summary>
        /// Deserialize a Queue of NinoSerialize object
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="data"></param>
        /// <param name="option"></param>
        /// <returns></returns>
        public static Queue<T> DeserializeQueue<T>(Span<byte> data, CompressOption option = CompressOption.Zlib)
        {
            var type = typeof(Queue<T>);
            var reader = ObjectPool<Reader>.Request();
            reader.Init(data, data.Length,
                TypeModel.IsNonCompressibleType(type) ? CompressOption.NoCompression : option);

            /*
             * NO GC DESERIALIZATION ATTEMPT
             */
            //basic type
            if (TryDeserializeWrapperType(typeof(Queue<T>), reader, false, true, out Queue<T> ret))
            {
                return ret;
            }

            //attempt to deserialize using generic method
            return reader.ReadQueue<T>();
        }

        #endregion

        #region Stack

        /// <summary>
        /// Deserialize a Stack of NinoSerialize object
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="data"></param>
        /// <param name="option"></param>
        /// <returns></returns>
        public static Stack<T> DeserializeStack<T>(byte[] data, CompressOption option = CompressOption.Zlib)
            => DeserializeStack<T>(new Span<byte>(data), option);

        /// <summary>
        /// Deserialize a Stack of NinoSerialize object
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="data"></param>
        /// <param name="option"></param>
        /// <returns></returns>
        public static Stack<T> DeserializeStack<T>(ArraySegment<byte> data, CompressOption option = CompressOption.Zlib)
            => DeserializeStack<T>(new Span<byte>(data.Array, data.Offset, data.Count), option);

        /// <summary>
        /// Deserialize a Stack of NinoSerialize object
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="data"></param>
        /// <param name="option"></param>
        /// <returns></returns>
        public static Stack<T> DeserializeStack<T>(Span<byte> data, CompressOption option = CompressOption.Zlib)
        {
            var type = typeof(Stack<T>);
            var reader = ObjectPool<Reader>.Request();
            reader.Init(data, data.Length,
                TypeModel.IsNonCompressibleType(type) ? CompressOption.NoCompression : option);

            /*
             * NO GC DESERIALIZATION ATTEMPT
             */
            //basic type
            if (TryDeserializeWrapperType(typeof(Stack<T>), reader, false, true, out Stack<T> ret))
            {
                return ret;
            }

            //attempt to deserialize using generic method
            return reader.ReadStack<T>();
        }

        #endregion

        #region Dictionary

        /// <summary>
        /// Deserialize a Dictionary of NinoSerialize object
        /// </summary>
        /// <typeparam name="TKey"></typeparam>
        /// <typeparam name="TValue"></typeparam>
        /// <param name="data"></param>
        /// <param name="option"></param>
        /// <returns></returns>
        public static Dictionary<TKey, TValue> DeserializeDictionary<TKey, TValue>(byte[] data,
            CompressOption option = CompressOption.Zlib)
            => DeserializeDictionary<TKey, TValue>(new Span<byte>(data), option);

        /// <summary>
        /// Deserialize a Dictionary of NinoSerialize object
        /// </summary>
        /// <typeparam name="TKey"></typeparam>
        /// <typeparam name="TValue"></typeparam>
        /// <param name="data"></param>
        /// <param name="option"></param>
        /// <returns></returns>
        public static Dictionary<TKey, TValue> DeserializeDictionary<TKey, TValue>(ArraySegment<byte> data,
            CompressOption option = CompressOption.Zlib)
            => DeserializeDictionary<TKey, TValue>(new Span<byte>(data.Array, data.Offset, data.Count), option);

        /// <summary>
        /// Deserialize a Dictionary of NinoSerialize object
        /// </summary>
        /// <typeparam name="TKey"></typeparam>
        /// <typeparam name="TValue"></typeparam>
        /// <param name="data"></param>
        /// <param name="option"></param>
        /// <returns></returns>
        public static Dictionary<TKey, TValue> DeserializeDictionary<TKey, TValue>(Span<byte> data,
            CompressOption option = CompressOption.Zlib)
        {
            var type = typeof(Dictionary<TKey, TValue>);
            var reader = ObjectPool<Reader>.Request();
            reader.Init(data, data.Length,
                TypeModel.IsNonCompressibleType(type) ? CompressOption.NoCompression : option);

            /*
             * NO GC DESERIALIZATION ATTEMPT
             */
            //basic type
            if (TryDeserializeWrapperType(typeof(Dictionary<TKey, TValue>), reader, false, true,
                    out Dictionary<TKey, TValue> ret))
            {
                return ret;
            }

            //attempt to deserialize using generic method
            return reader.ReadDictionary<TKey, TValue>();
        }

        #endregion
    }
}