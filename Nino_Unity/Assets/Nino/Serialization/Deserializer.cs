using System;
using Nino.Shared.IO;
using Nino.Shared.Mgr;
using System.Reflection;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;

namespace Nino.Serialization
{
    public static partial class Deserializer
    {
        /// <summary>
        /// Custom Exporter delegate that reads bytes to object
        /// </summary>
        internal delegate T ExporterDelegate<out T>(Reader reader);

        /// <summary>
        /// Add custom Exporter of all type T objects
        /// </summary>
        /// <param name="func"></param>
        /// <typeparam name="T"></typeparam>
        public static void AddCustomExporter<T>(Func<Reader, T> func)
        {
            var type = typeof(T);
            if (WrapperManifest.TryGetWrapper(type, out var wrapper))
            {
                ((GenericWrapper<T>)wrapper).Exporter = func.Invoke;
                return;
            }

            GenericWrapper<T> genericWrapper = new GenericWrapper<T>
            {
                Exporter = func.Invoke
            };
            WrapperManifest.AddWrapper(typeof(T), genericWrapper);
        }

        /// <summary>
        /// Deserialize a NinoSerialize object
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="data"></param>
        /// <param name="option"></param>
        /// <returns></returns>
        public static T Deserialize<T>(byte[] data, CompressOption option = CompressOption.Zlib)
            => Deserialize<T>(new Span<byte>(data), null, option);

        /// <summary>
        /// Deserialize a NinoSerialize object
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="data"></param>
        /// <param name="option"></param>
        /// <returns></returns>
        public static T Deserialize<T>(ArraySegment<byte> data, CompressOption option = CompressOption.Zlib)
            => Deserialize<T>(new Span<byte>(data.Array, data.Offset, data.Count), null, option);

        /// <summary>
        /// Deserialize a NinoSerialize object
        /// </summary>
        /// <param name="type"></param>
        /// <param name="data"></param>
        /// <param name="option"></param>
        /// <returns></returns>
        public static object Deserialize(Type type, byte[] data, CompressOption option = CompressOption.Zlib)
            => Deserialize(type, null, new Span<byte>(data), null, option);

        /// <summary>
        /// Deserialize a NinoSerialize object
        /// </summary>
        /// <param name="type"></param>
        /// <param name="data"></param>
        /// <param name="option"></param>
        /// <returns></returns>
        public static object Deserialize(Type type, ArraySegment<byte> data,
            CompressOption option = CompressOption.Zlib)
            => Deserialize(type, null, new Span<byte>(data.Array, data.Offset, data.Count), null, option);

        /// <summary>
        /// Deserialize a NinoSerialize object
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="data"></param>
        /// <param name="reader"></param>
        /// <param name="option"></param>
        /// <param name="returnDispose"></param>
        /// <returns></returns>
        internal static T Deserialize<T>(Span<byte> data, Reader reader,
            CompressOption option = CompressOption.Zlib, [MarshalAs(UnmanagedType.U1)] bool returnDispose = true)
        {
            Type type = typeof(T);

            if (reader == null)
            {
                reader = ObjectPool<Reader>.Request();
                reader.Init(data, data.Length,
                    TypeModel.IsNonCompressibleType(type) ? CompressOption.NoCompression : option);
            }

            /*
             * NO GC DESERIALIZATION ATTEMPT
             */
            //basic type
            if (TryDeserializeWrapperType(type, reader, false, returnDispose, out T ret))
            {
                return ret;
            }

            //code generated type
            if (TryDeserializeCodeGenType(type, reader, false, returnDispose, out ret))
            {
                return ret;
            }

            /*
             * GC DESERIALIZATION WHILE T IS STRUCT
             */
            var result = Deserialize(type, null, data, reader, option, returnDispose);
            if (result != null) return (T)result;
            ObjectPool<Reader>.Return(reader);
            return default;
        }

        /// <summary>
        /// Deserialize a NinoSerialize object
        /// </summary>
        /// <param name="type"></param>
        /// <param name="val"></param>
        /// <param name="data"></param>
        /// <param name="reader"></param>
        /// <param name="option"></param>
        /// <param name="returnDispose"></param>
        /// <returns></returns>
        /// <exception cref="InvalidOperationException"></exception>
        /// <exception cref="NullReferenceException"></exception>
        internal static object Deserialize(Type type, object val, Span<byte> data, Reader reader,
            CompressOption option = CompressOption.Zlib, [MarshalAs(UnmanagedType.U1)] bool returnDispose = true)
        {
            if (reader == null)
            {
                reader = ObjectPool<Reader>.Request();
                reader.Init(data, data.Length,
                    TypeModel.IsNonCompressibleType(type) ? CompressOption.NoCompression : option);
            }

            //array
            if (TryDeserializeArray(type, reader, returnDispose, out var arr))
            {
                return arr;
            }

            //list, dict
            if (type.IsGenericType)
            {
                var genericDefType = type.GetGenericTypeDefinition();
                if (TryDeserializeList(type, genericDefType, reader, returnDispose, out var lst))
                {
                    return lst;
                }

                if (TryDeserializeDict(type, genericDefType, reader, returnDispose, out var dict))
                {
                    return dict;
                }
            }

#if ILRuntime
			type = type.ResolveRealType();
#endif

            //basic type
            if (TryDeserializeWrapperType(type, reader, true, returnDispose, out object basicObj))
            {
                return basicObj;
            }

            //enum
            if (TryDeserializeEnum(type, reader, returnDispose, out var e))
            {
                return e;
            }

            //code generated type
            if (TryDeserializeCodeGenType(type, reader, true, returnDispose, out object codeGenRet))
            {
                return codeGenRet;
            }

            /*
             * CUSTOM STRUCT/CLASS SERIALIZATION
             */
            if (ReadNullCheck(reader, returnDispose, out var nullCheck))
            {
                return nullCheck;
            }

            //create type
            if (val == null || val == ConstMgr.Null)
            {
#if ILRuntime
				val = ILRuntimeResolver.CreateInstance(type);
#else
                val = Activator.CreateInstance(type);
#endif
            }

            //Get Attribute that indicates a class/struct to be serialized
            TypeModel.TryGetModel(type, out var model);

            //invalid model
            if (model != null && !model.Valid)
            {
                return val;
            }

            //generate model
            if (model == null)
            {
                model = TypeModel.CreateModel(type);
            }

            //start Deserialize
            //only include all model need this
            if (model.IncludeAll)
            {
                //read len
                var len = reader.ReadLength();
                Dictionary<string, object> values = ObjectPool<Dictionary<string, object>>.Request();
                values.Clear();
                //read elements key by key
                for (int i = 0; i < len; i++)
                {
                    var key = reader.ReadString();
                    var typeFullName = reader.ReadString();
                    var value = Deserialize(Type.GetType(typeFullName), ConstMgr.Null, ConstMgr.Null, reader,
                        option, false);
                    values.Add(key, value);
                }

                //set elements
                foreach (var member in model.Members)
                {
                    //try get same member and set it
                    if (values.TryGetValue(member.Name, out var ret))
                    {
                        SetMember(member, val, ret);
                    }
                }

                values.Clear();
                ObjectPool<Dictionary<string, object>>.Return(values);
            }
            else
            {
                foreach (var member in model.Members)
                {
                    //if end, skip
                    if (reader.EndOfReader)
                    {
                        break;
                    }

                    type = member is FieldInfo fi ? fi.FieldType : ((PropertyInfo)member).PropertyType;

                    //read basic values
                    SetMember(member, val, Deserialize(type, ConstMgr.Null, Span<byte>.Empty, reader, option, false));
                }
            }

            if (returnDispose)
            {
                ObjectPool<Reader>.Return(reader);
            }

            return val;
        }

        /// <summary>
        /// Try deserialize wrapper type
        /// </summary>
        /// <param name="type"></param>
        /// <param name="reader"></param>
        /// <param name="boxed"></param>
        /// <param name="returnDispose"></param>
        /// <param name="ret"></param>
        /// <typeparam name="T"></typeparam>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TryDeserializeWrapperType<T>(Type type, Reader reader,
            [MarshalAs(UnmanagedType.U1)] bool boxed, [MarshalAs(UnmanagedType.U1)] bool returnDispose,
            out T ret)
        {
            if (WrapperManifest.TryGetWrapper(type, out var wrapper))
            {
                if (boxed)
                {
                    var obj = wrapper.Deserialize(reader);
                    ret = obj != null ? (T)obj : default;
                }
                else
                {
                    ret = ((NinoWrapperBase<T>)wrapper).Deserialize(reader);
                }

                if (returnDispose)
                    ObjectPool<Reader>.Return(reader);
                return true;
            }

            ret = default;
            return false;
        }

        /// <summary>
        /// Try deserialize code generated type (first time only)
        /// </summary>
        /// <param name="type"></param>
        /// <param name="reader"></param>
        /// <param name="boxed"></param>
        /// <param name="returnDispose"></param>
        /// <param name="ret"></param>
        /// <typeparam name="T"></typeparam>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TryDeserializeCodeGenType<T>(Type type, Reader reader,
            [MarshalAs(UnmanagedType.U1)] bool boxed, [MarshalAs(UnmanagedType.U1)] bool returnDispose,
            out T ret)
        {
            if (TypeModel.TryGetWrapper(type, out var wrapper))
            {
                //add wrapper
                WrapperManifest.AddWrapper(type, wrapper);
                //start Deserialize
                if (boxed)
                {
                    var obj = wrapper.Deserialize(reader);
                    ret = obj != null ? (T)obj : default;
                }
                else
                {
                    ret = ((NinoWrapperBase<T>)wrapper).Deserialize(reader);
                }

                if (returnDispose)
                    ObjectPool<Reader>.Return(reader);
                return true;
            }

            ret = default;
            return false;
        }

        /// <summary>
        /// Try deserialize array
        /// </summary>
        /// <param name="type"></param>
        /// <param name="reader"></param>
        /// <param name="returnDispose"></param>
        /// <param name="ret"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TryDeserializeArray(Type type, Reader reader,
            [MarshalAs(UnmanagedType.U1)] bool returnDispose, out Array ret)
        {
            if (type.IsArray)
            {
                ret = reader.ReadArray(type);
                if (returnDispose)
                {
                    ObjectPool<Reader>.Return(reader);
                }

                return true;
            }

            ret = null;
            return false;
        }

        /// <summary>
        /// Try deserialize list
        /// </summary>
        /// <param name="type"></param>
        /// <param name="genericDefType"></param>
        /// <param name="reader"></param>
        /// <param name="returnDispose"></param>
        /// <param name="ret"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TryDeserializeList(Type type, Type genericDefType, Reader reader,
            [MarshalAs(UnmanagedType.U1)] bool returnDispose, out IList ret)
        {
            if (genericDefType == ConstMgr.ListDefType)
            {
                ret = reader.ReadList(type);
                if (returnDispose)
                {
                    ObjectPool<Reader>.Return(reader);
                }

                return true;
            }

            ret = null;
            return false;
        }

        /// <summary>
        /// Try deserialize dictionary
        /// </summary>
        /// <param name="type"></param>
        /// <param name="genericDefType"></param>
        /// <param name="reader"></param>
        /// <param name="returnDispose"></param>
        /// <param name="ret"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TryDeserializeDict(Type type, Type genericDefType, Reader reader,
            [MarshalAs(UnmanagedType.U1)] bool returnDispose, out IDictionary ret)
        {
            if (genericDefType == ConstMgr.DictDefType)
            {
                ret = reader.ReadDictionary(type);
                if (returnDispose)
                {
                    ObjectPool<Reader>.Return(reader);
                }

                return true;
            }

            ret = null;
            return false;
        }

        /// <summary>
        /// Try deserialize enum
        /// </summary>
        /// <param name="type"></param>
        /// <param name="reader"></param>
        /// <param name="returnDispose"></param>
        /// <param name="obj"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TryDeserializeEnum(Type type, Reader reader,
            [MarshalAs(UnmanagedType.U1)] bool returnDispose, out object obj)
        {
            if (TypeModel.IsEnum(type))
            {
                var underlyingType = Enum.GetUnderlyingType(type);
                var ret = reader.DecompressAndReadEnum(underlyingType);
#if ILRuntime
				if (type is ILRuntime.Reflection.ILRuntimeType)
                {
                    if (underlyingType == ConstMgr.LongType
                        || underlyingType == ConstMgr.UIntType
                        || underlyingType == ConstMgr.ULongType)
                        obj = unchecked((long)ret);
                    obj = unchecked((int)ret);
                }
#endif
                obj = Enum.ToObject(type, ret);
                if (returnDispose)
                {
                    ObjectPool<Reader>.Return(reader);
                }

                return true;
            }

            obj = null;
            return false;
        }

        /// <summary>
        /// Check for null
        /// </summary>
        /// <param name="reader"></param>
        /// <param name="returnDispose"></param>
        /// <param name="obj"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool ReadNullCheck(Reader reader, [MarshalAs(UnmanagedType.U1)] bool returnDispose,
            out object obj)
        {
            obj = null;
            if (!reader.ReadBool()) // if null -> readBool will give false
            {
                if (returnDispose)
                {
                    ObjectPool<Reader>.Return(reader);
                }

                return true;
            }

            return false;
        }

        /// <summary>
        /// Set value from MemberInfo
        /// </summary>
        /// <param name="info"></param>
        /// <param name="instance"></param>
        /// <param name="val"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void SetMember(MemberInfo info, object instance, object val)
        {
            switch (info)
            {
                case FieldInfo fo:
                    fo.SetValue(instance, val);
                    break;
                case PropertyInfo po:
                    po.SetValue(instance, val);
                    break;
                default:
                    return;
            }
        }
    }
}