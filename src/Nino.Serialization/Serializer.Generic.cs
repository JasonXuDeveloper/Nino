using Nino.Shared.IO;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Nino.Serialization
{
    public static partial class Serializer
    {
        /// <summary>
        /// Serialize an array of NinoSerialize object
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="val"></param>
        /// <param name="option"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte[] Serialize<T>(T[] val, CompressOption option = CompressOption.Zlib)
        {
            Writer writer = ObjectPool<Writer>.Request();
            writer.Init(option);

            /*
             * HARD-CODED SERIALIZATION
             */
            if (TrySerializeWrapperType(typeof(T[]), val, writer, false, true, out var ret))
            {
                return ret;
            }

            //attempt to serialize using generic method
            writer.Write(val);
            return Return(true, writer);
        }
        
        /// <summary>
        /// Serialize a nullable of NinoSerialize struct
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="val"></param>
        /// <param name="option"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte[] Serialize<T>(T? val, CompressOption option = CompressOption.Zlib) where T: struct
        {
            Writer writer = ObjectPool<Writer>.Request();
            writer.Init(option);

            //attempt to serialize using generic method
            writer.Write(val);
            return Return(true, writer);
        }

        /// <summary>
        /// Serialize a list of NinoSerialize object
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="val"></param>
        /// <param name="option"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte[] Serialize<T>(List<T> val, CompressOption option = CompressOption.Zlib)
        {
            Writer writer = ObjectPool<Writer>.Request();
            writer.Init(option);

            /*
             * HARD-CODED SERIALIZATION
             */
            if (TrySerializeWrapperType(typeof(List<T>), val, writer, false, true, out var ret))
            {
                return ret;
            }

            //attempt to serialize using generic method
            writer.Write(val);
            return Return(true, writer);
        }

        /// <summary>
        /// Serialize a hashset of NinoSerialize object
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="val"></param>
        /// <param name="option"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte[] Serialize<T>(HashSet<T> val, CompressOption option = CompressOption.Zlib)
        {
            Writer writer = ObjectPool<Writer>.Request();
            writer.Init(option);

            /*
             * HARD-CODED SERIALIZATION
             */
            if (TrySerializeWrapperType(typeof(HashSet<T>), val, writer, false, true, out var ret))
            {
                return ret;
            }

            //attempt to serialize using generic method
            writer.Write(val);
            return Return(true, writer);
        }

        /// <summary>
        /// Serialize a hashset of NinoSerialize object
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="val"></param>
        /// <param name="option"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte[] Serialize<T>(Queue<T> val, CompressOption option = CompressOption.Zlib)
        {
            Writer writer = ObjectPool<Writer>.Request();
            writer.Init(option);

            /*
             * HARD-CODED SERIALIZATION
             */
            if (TrySerializeWrapperType(typeof(Queue<T>), val, writer, false, true, out var ret))
            {
                return ret;
            }

            //attempt to serialize using generic method
            writer.Write(val);
            return Return(true, writer);
        }

        /// <summary>
        /// Serialize a hashset of NinoSerialize object
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="val"></param>
        /// <param name="option"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte[] Serialize<T>(Stack<T> val, CompressOption option = CompressOption.Zlib)
        {
            Writer writer = ObjectPool<Writer>.Request();
            writer.Init(option);

            /*
             * HARD-CODED SERIALIZATION
             */
            if (TrySerializeWrapperType(typeof(Stack<T>), val, writer, false, true, out var ret))
            {
                return ret;
            }

            //attempt to serialize using generic method
            writer.Write(val);
            return Return(true, writer);
        }

        /// <summary>
        /// Serialize a dictionary of NinoSerialize object
        /// </summary>
        /// <typeparam name="TKey"></typeparam>
        /// <typeparam name="TValue"></typeparam>
        /// <param name="val"></param>
        /// <param name="option"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte[] Serialize<TKey, TValue>(Dictionary<TKey, TValue> val,
            CompressOption option = CompressOption.Zlib)
        {
            Writer writer = ObjectPool<Writer>.Request();
            writer.Init(option);

            /*
             * HARD-CODED SERIALIZATION
             */
            if (TrySerializeWrapperType(typeof(Dictionary<TKey, TValue>), val, writer, false, true, out var ret))
            {
                return ret;
            }

            //attempt to serialize using generic method
            writer.Write(val);
            return Return(true, writer);
        }
    }
}