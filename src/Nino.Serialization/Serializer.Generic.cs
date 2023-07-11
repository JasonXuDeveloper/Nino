using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Nino.Serialization
{
    public static partial class Serializer
    {
        /// <summary>
        /// Attempt to serialize hard-coded types + code gen types + custom delegate types
        /// </summary>
        /// <param name="type"></param>
        /// <param name="value"></param>
        /// <param name="writer"></param>
        /// <typeparam name="T"></typeparam>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TrySerializeWrapperType<T>(Type type, ref T value, ref Writer writer)
        {
            //basic type
            if (!WrapperManifest.TryGetWrapper(type, out var wrapper))
            {
                return false;
            }

            if (wrapper is NinoWrapperBase<T> @base)
            {
                @base.Serialize(value, ref writer);
            }
            else
            {
                wrapper.Serialize(value, ref writer);
            }

            return true;
        }

        /// <summary>
        /// Attempt to serialize enum
        /// </summary>
        /// <param name="type"></param>
        /// <param name="value"></param>
        /// <param name="writer"></param>
        /// <typeparam name="T"></typeparam>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TrySerializeEnumType<T>(Type type, ref T value, ref Writer writer)
        {
            //enum
            if (!TypeModel.IsEnum(type))
            {
                return false;
            }

            writer.WriteEnum(value);
            return true;
        }

        /// <summary>
        /// Attempt to serialize code gen type (first time only)
        /// </summary>
        /// <param name="type"></param>
        /// <param name="value"></param>
        /// <param name="writer"></param>
        /// <typeparam name="T"></typeparam>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TrySerializeCodeGenType<T>(Type type, ref T value, ref Writer writer)
        {
            //code generated type
            if (!TypeModel.TryGetWrapper(type, out var wrapper))
            {
                return false;
            }

            //add wrapper
            WrapperManifest.AddWrapper(type, wrapper);

            //start serialize
            if (wrapper is NinoWrapperBase<T> @base)
            {
                @base.Serialize(value, ref writer);
            }
            else
            {
                wrapper.Serialize(value, ref writer);
            }

            return true;
        }

        /// <summary>
        /// Attempt to serialize array
        /// </summary>
        /// <param name="value"></param>
        /// <param name="writer"></param>
        /// <typeparam name="T"></typeparam>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TrySerializeArray<T>(ref T value, ref Writer writer)
        {
            //array
            if (!(value is Array arr))
            {
                return false;
            }

            writer.Write(arr);
            return true;
        }

        /// <summary>
        /// Attempt to serialize list (boxed)
        /// </summary>
        /// <param name="value"></param>
        /// <param name="writer"></param>
        /// <typeparam name="T"></typeparam>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TrySerializeList<T>(ref T value, ref Writer writer)
        {
            if (!(value is IList lst))
            {
                return false;
            }

            writer.Write(lst);
            return true;
        }

        /// <summary>
        /// Attempt to serialize dict
        /// </summary>
        /// <param name="value"></param>
        /// <param name="writer"></param>
        /// <typeparam name="T"></typeparam>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TrySerializeDict<T>(ref T value, ref Writer writer)
        {
            if (!(value is IDictionary dict))
            {
                return false;
            }

            writer.Write(dict);
            return true;
        }

        /// <summary>
        /// Serialize an array of NinoSerialize object
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="val"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte[] Serialize<T>(T[] val)
        {
            int length = GetSize(in val);
            if (length == 0)
            {
                return Array.Empty<byte>();
            }

            byte[] ret = new byte[length];
            if (val == null)
            {
                return ret;
            }

            Writer writer = new Writer(ret.AsSpan(), 0);

            /*
             * HARD-CODED SERIALIZATION
             */
            if (TrySerializeWrapperType(typeof(T[]), ref val, ref writer))
            {
                return ret;
            }

            //attempt to serialize using generic method
            writer.Write(val);
            return ret;
        }

        /// <summary>
        /// Serialize a nullable of NinoSerialize struct
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="val"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte[] Serialize<T>(T? val) where T : struct
        {
            int length = GetSize(in val);
            if (length == 0)
            {
                return Array.Empty<byte>();
            }

            byte[] ret = new byte[length];
            if (val == null)
            {
                return ret;
            }

            Writer writer = new Writer(ret.AsSpan(), 0);

            //attempt to serialize using generic method
            writer.Write(val);
            return ret;
        }

        /// <summary>
        /// Serialize a list of NinoSerialize object
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="val"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte[] Serialize<T>(List<T> val)
        {
            int length = GetSize(in val);
            if (length == 0)
            {
                return Array.Empty<byte>();
            }

            byte[] ret = new byte[length];
            if (val == null)
            {
                return ret;
            }

            Writer writer = new Writer(ret.AsSpan(), 0);

            /*
             * HARD-CODED SERIALIZATION
             */
            if (TrySerializeWrapperType(typeof(List<T>), ref val, ref writer))
            {
                return ret;
            }

            //attempt to serialize using generic method
            writer.Write(val);
            return ret;
        }

        /// <summary>
        /// Serialize a hashset of NinoSerialize object
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="val"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte[] Serialize<T>(HashSet<T> val)
        {
            int length = GetSize(in val);
            if (length == 0)
            {
                return Array.Empty<byte>();
            }

            byte[] ret = new byte[length];
            if (val == null)
            {
                return ret;
            }

            Writer writer = new Writer(ret.AsSpan(), 0);

            /*
             * HARD-CODED SERIALIZATION
             */
            if (TrySerializeWrapperType(typeof(HashSet<T>), ref val, ref writer))
            {
                return ret;
            }

            //attempt to serialize using generic method
            writer.Write(val);
            return ret;
        }

        /// <summary>
        /// Serialize a hashset of NinoSerialize object
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="val"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte[] Serialize<T>(Queue<T> val)
        {
            int length = GetSize(in val);
            if (length == 0)
            {
                return Array.Empty<byte>();
            }

            byte[] ret = new byte[length];
            if (val == null)
            {
                return ret;
            }

            Writer writer = new Writer(ret.AsSpan(), 0);

            /*
             * HARD-CODED SERIALIZATION
             */
            if (TrySerializeWrapperType(typeof(Queue<T>), ref val, ref writer))
            {
                return ret;
            }

            //attempt to serialize using generic method
            writer.Write(val);
            return ret;
        }

        /// <summary>
        /// Serialize a hashset of NinoSerialize object
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="val"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte[] Serialize<T>(Stack<T> val)
        {
            int length = GetSize(in val);
            if (length == 0)
            {
                return Array.Empty<byte>();
            }

            byte[] ret = new byte[length];
            if (val == null)
            {
                return ret;
            }

            Writer writer = new Writer(ret.AsSpan(), 0);

            /*
             * HARD-CODED SERIALIZATION
             */
            if (TrySerializeWrapperType(typeof(Stack<T>), ref val, ref writer))
            {
                return ret;
            }

            //attempt to serialize using generic method
            writer.Write(val);
            return ret;
        }

        /// <summary>
        /// Serialize a dictionary of NinoSerialize object
        /// </summary>
        /// <typeparam name="TKey"></typeparam>
        /// <typeparam name="TValue"></typeparam>
        /// <param name="val"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte[] Serialize<TKey, TValue>(Dictionary<TKey, TValue> val)
        {
            int length = GetSize(in val);
            if (length == 0)
            {
                return Array.Empty<byte>();
            }

            byte[] ret = new byte[length];
            if (val == null)
            {
                return ret;
            }

            Writer writer = new Writer(ret.AsSpan(), 0);

            /*
             * HARD-CODED SERIALIZATION
             */
            if (TrySerializeWrapperType(typeof(Dictionary<TKey, TValue>), ref val, ref writer))
            {
                return ret;
            }

            //attempt to serialize using generic method
            writer.Write(val);
            return ret;
        }
    }
}