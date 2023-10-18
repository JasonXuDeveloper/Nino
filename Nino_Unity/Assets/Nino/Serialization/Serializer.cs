using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Nino.Shared.IO;

namespace Nino.Serialization
{
    public static partial class Serializer
    {
        /// <summary>
        /// Serialize a NinoSerialize object
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="val"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte[] Serialize<T>(in T val)
        {
            Dictionary<MemberInfo, object> fields = ObjectPool<Dictionary<MemberInfo, object>>.Request();
            fields.Clear();
            int length = GetSize(in val, fields);
            if (length == 0)
            {
                return Array.Empty<byte>();
            }

            byte[] ret = new byte[length];
            if (val == null)
            {
                ObjectPool<Dictionary<MemberInfo, object>>.Return(fields);
                return ret;
            }

            Serialize(typeof(T), val, fields, ret.AsSpan(), 0);
            ObjectPool<Dictionary<MemberInfo, object>>.Return(fields);
            return ret;
        }

        /// <summary>
        /// Serialize a NinoSerialize object
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="buffer"></param>
        /// <param name="val"></param>
        /// <param name="fields"></param>
        /// <returns>actual written size</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Serialize<T>(Span<byte> buffer, in T val, Dictionary<MemberInfo, object> fields = null)
        {
            if (val == null)
            {
                buffer[0] = 0;
                return 1;
            }

            return Serialize(typeof(T), val, fields, buffer, 0);
        }

        /// <summary>
        /// Serialize a NinoSerialize object
        /// </summary>
        /// <param name="val"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte[] Serialize(object val)
        {
            Dictionary<MemberInfo, object> fields = ObjectPool<Dictionary<MemberInfo, object>>.Request();
            fields.Clear();
            int length = GetSize(in val, fields);
            if (length == 0)
            {
                return Array.Empty<byte>();
            }

            byte[] ret = new byte[length];
            if (val == null)
            {
                ObjectPool<Dictionary<MemberInfo, object>>.Return(fields);
                return ret;
            }

            Serialize(val.GetType(), val, fields, ret.AsSpan(), 0);
            ObjectPool<Dictionary<MemberInfo, object>>.Return(fields);
            return ret;
        }

        /// <summary>
        /// Serialize a NinoSerialize object
        /// </summary>
        /// <param name="buffer"></param>
        /// <param name="val"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int Serialize(Span<byte> buffer, object val)
        {
            if (val == null)
            {
                buffer[0] = 0;
                return 1;
            }

            return Serialize(val.GetType(), val, null, buffer, 0);
        }

        internal static int Serialize<T>(Type type, T value, Dictionary<MemberInfo, object> fields, Span<byte> buffer,
            int pos)
        {
            Writer writer = new Writer(buffer, pos);

            //null check
            if (value == null)
            {
                writer.Write(false); // false -> is null
                return writer.Position;
            }

            /*
             * HARD-CODED SERIALIZATION
             */

            if (TrySerializeWrapperType(type, ref value, ref writer))
            {
                return writer.Position;
            }

            if (TrySerializeEnumType(type, ref value, ref writer))
            {
                return writer.Position;
            }

            if (TrySerializeCodeGenType(type, ref value, ref writer))
            {
                return writer.Position;
            }

            if (TrySerializeUnmanagedType(type, ref value, ref writer))
            {
                return writer.Position;
            }

            if (TrySerializeArray(ref value, ref writer))
            {
                return writer.Position;
            }

            //generic
            if (type.IsGenericType)
            {
                if (TrySerializeList(ref value, ref writer))
                {
                    return writer.Position;
                }

                if (TrySerializeDict(ref value, ref writer))
                {
                    return writer.Position;
                }
            }

            /*
             * CUSTOM STRUCT/CLASS SERIALIZATION
             */
            writer.Write(true); //true -> not null

            //Get Attribute that indicates a class/struct to be serialized
            TypeModel.TryGetModel(type, out var model);

            //invalid model
            if (model != null && !model.Valid)
            {
                return 0;
            }

            //generate model
            if (model == null)
            {
                model = TypeModel.CreateModel(type);
            }

            //serialize all recorded members
            foreach (var info in model.Members)
            {
                type = info.Type;
                if (fields == null || !fields.TryGetValue(info.Member, out var obj))
                {
                    obj = info.GetValue(value);
                }

                writer.Position = Serialize(type, obj, null, buffer, writer.Position);
            }

            return writer.Position;
        }
    }
}