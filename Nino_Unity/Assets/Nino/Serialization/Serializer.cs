using System;
using Nino.Shared.IO;
using Nino.Shared.Mgr;
using System.Reflection;
using System.Collections;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;

namespace Nino.Serialization
{
    public static class Serializer
    {
        /// <summary>
        /// Custom importer delegate that writes object to writer
        /// </summary>
        internal delegate void ImporterDelegate<in T>(T val, Writer writer);

        /// <summary>
        /// Add custom importer of all type T objects
        /// </summary>
        /// <param name="action"></param>
        /// <typeparam name="T"></typeparam>
        public static void AddCustomImporter<T>(Action<T, Writer> action)
        {
            var type = typeof(T);
            if (WrapperManifest.TryGetWrapper(type, out var wrapper))
            {
                ((GenericWrapper<T>)wrapper).Importer = action.Invoke;
                return;
            }

            GenericWrapper<T> genericWrapper = new GenericWrapper<T>
            {
                Importer = action.Invoke
            };
            WrapperManifest.AddWrapper(type, genericWrapper);
        }

        /// <summary>
        /// Serialize a NinoSerialize object
        /// </summary>
        /// <typeparam name="T"></typeparam>
        /// <param name="val"></param>
        /// <param name="option"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte[] Serialize<T>(T val, CompressOption option = CompressOption.Zlib)
        {
            Writer writer = ObjectPool<Writer>.Request();
            writer.Init(option);
            return Serialize(typeof(T), val, writer, option);
        }

        /// <summary>
        /// Serialize a NinoSerialize object
        /// </summary>
        /// <param name="val"></param>
        /// <param name="option"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte[] Serialize(object val, CompressOption option = CompressOption.Zlib)
        {
            Writer writer = ObjectPool<Writer>.Request();
            writer.Init(option);
            return Serialize(val is null ? typeof(void) : val.GetType(), val, writer, option);
        }

        /// <summary>
        /// Serialize a NinoSerialize object
        /// </summary>
        /// <param name="type"></param>
        /// <param name="value"></param>
        /// <param name="writer"></param>
        /// <param name="option"></param>
        /// <param name="returnValue"></param>
        /// <returns></returns>
        /// <exception cref="InvalidOperationException"></exception>
        /// <exception cref="NullReferenceException"></exception>
        // ReSharper disable CognitiveComplexity
        internal static byte[] Serialize<T>(Type type, T value, Writer writer,
                CompressOption option = CompressOption.Zlib, [MarshalAs(UnmanagedType.U1)] bool returnValue = true)
            // ReSharper restore CognitiveComplexity
        {
            bool boxed = false;
            Type tType = typeof(T);
            if (type != tType || tType == ConstMgr.ObjectType)
            {
                if (value == null)
                {
                    throw new InvalidOperationException("Failed to retrieve unbox type");
                }

                type = value.GetType();
                boxed = true;
            }

            //ILRuntime
#if ILRuntime
			if(value is ILRuntime.Runtime.Intepreter.ILTypeInstance ins)
			{
				type = ins.Type.ReflectionType;
			}

			type = type.ResolveRealType();
#endif

            if (writer == null)
            {
                writer = ObjectPool<Writer>.Request();
            }

            if (returnValue)
            {
                writer.Init(TypeModel.IsNonCompressibleType(type) ? CompressOption.NoCompression : option);
            }

            /*
             * HARD-CODED SERIALIZATION
             */

            if (TrySerializeWrapperType(type, value, writer, boxed, returnValue, out var ret))
            {
                return ret;
            }

            if (TrySerializeEnumType(type, value, writer, returnValue, out ret))
            {
                return ret;
            }

            if (TrySerializeCodeGenType(type, value, writer, boxed, returnValue, out ret))
            {
                return ret;
            }

            if (TrySerializeArray(value, writer, returnValue, out ret))
            {
                return ret;
            }

            //generic
            if (type.IsGenericType)
            {
                if (TrySerializeList(value, writer, returnValue, out ret))
                {
                    return ret;
                }

                if (TrySerializeDict(value, writer, returnValue, out ret))
                {
                    return ret;
                }
            }

            /*
             * CUSTOM STRUCT/CLASS SERIALIZATION
             */
            if (WriteNullCheck(value, writer, returnValue, out ret))
            {
                return ret;
            }

            //Get Attribute that indicates a class/struct to be serialized
            TypeModel.TryGetModel(type, out var model);

            //invalid model
            if (model != null && !model.Valid)
            {
                ObjectPool<Writer>.Return(writer);
                return ConstMgr.Null;
            }

            //generate model
            if (model == null)
            {
                model = TypeModel.CreateModel(type);
            }

            //only include all model need this
            if (model.IncludeAll)
            {
                //write len
                writer.CompressAndWrite(model.Members.Count);
            }

            //serialize all recorded members
            foreach (var member in model.Members)
            {
                object obj;
                switch (member)
                {
                    case FieldInfo fo:
                        type = fo.FieldType;
                        obj = fo.GetValue(value);
                        break;
                    case PropertyInfo po:
                        type = po.PropertyType;
                        obj = po.GetValue(value);
                        break;
                    default:
                        return null;
                }

                //only include all model need this
                if (model.IncludeAll)
                {
                    writer.Write(member.Name);
                    writer.Write(type.AssemblyQualifiedName);
                }

                Serialize(type, obj, writer, option, false);
            }

            return Return(returnValue, writer);
        }

        /// <summary>
        /// Attempt to serialize hard-coded types + code gen types + custom delegate types
        /// </summary>
        /// <param name="type"></param>
        /// <param name="value"></param>
        /// <param name="writer"></param>
        /// <param name="boxed"></param>
        /// <param name="returnValue"></param>
        /// <param name="ret"></param>
        /// <typeparam name="T"></typeparam>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TrySerializeWrapperType<T>(Type type, T value, Writer writer,
            [MarshalAs(UnmanagedType.U1)] bool boxed, [MarshalAs(UnmanagedType.U1)] bool returnValue,
            out byte[] ret)
        {
            //basic type
            if (!WrapperManifest.TryGetWrapper(type, out var wrapper))
            {
                ret = ConstMgr.Null;
                return false;
            }

            if (boxed)
            {
                wrapper.Serialize(value, writer);
            }
            else
            {
                ((NinoWrapperBase<T>)wrapper).Serialize(value, writer);
            }

            ret = Return(returnValue, writer);
            return true;
        }

        /// <summary>
        /// Attempt to serialize enum
        /// </summary>
        /// <param name="type"></param>
        /// <param name="value"></param>
        /// <param name="writer"></param>
        /// <param name="returnValue"></param>
        /// <param name="ret"></param>
        /// <typeparam name="T"></typeparam>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TrySerializeEnumType<T>(Type type, T value, Writer writer, 
            [MarshalAs(UnmanagedType.U1)] bool returnValue, out byte[] ret)
        {
            //enum
            if (!TypeModel.IsEnum(type))
            {
                ret = ConstMgr.Null;
                return false;
            }

            writer.CompressAndWriteEnum(value);
            ret = Return(returnValue, writer);
            return true;
        }

        /// <summary>
        /// Attempt to serialize code gen type (first time only)
        /// </summary>
        /// <param name="type"></param>
        /// <param name="value"></param>
        /// <param name="writer"></param>
        /// <param name="boxed"></param>
        /// <param name="returnValue"></param>
        /// <param name="ret"></param>
        /// <typeparam name="T"></typeparam>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TrySerializeCodeGenType<T>(Type type, T value, Writer writer,
            [MarshalAs(UnmanagedType.U1)] bool boxed, [MarshalAs(UnmanagedType.U1)] bool returnValue,
            out byte[] ret)
        {
            //code generated type
            if (!TypeModel.TryGetWrapper(type, out var wrapper))
            {
                ret = ConstMgr.Null;
                return false;
            }

            //add wrapper
            WrapperManifest.AddWrapper(type, wrapper);

            //start serialize
            if (boxed)
            {
                wrapper.Serialize(value, writer);
            }
            else
            {
                ((NinoWrapperBase<T>)wrapper).Serialize(value, writer);
            }

            ret = Return(returnValue, writer);
            return true;
        }

        /// <summary>
        /// Attempt to serialize array
        /// </summary>
        /// <param name="value"></param>
        /// <param name="writer"></param>
        /// <param name="returnValue"></param>
        /// <param name="ret"></param>
        /// <typeparam name="T"></typeparam>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TrySerializeArray<T>(T value, Writer writer,
            [MarshalAs(UnmanagedType.U1)] bool returnValue, out byte[] ret)
        {
            //array
            if (!(value is Array arr))
            {
                ret = ConstMgr.Null;
                return false;
            }

            writer.Write(arr);
            ret = Return(returnValue, writer);
            return true;
        }

        /// <summary>
        /// Attempt to serialize list
        /// </summary>
        /// <param name="value"></param>
        /// <param name="writer"></param>
        /// <param name="returnValue"></param>
        /// <param name="ret"></param>
        /// <typeparam name="T"></typeparam>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TrySerializeList<T>(T value, Writer writer,
            [MarshalAs(UnmanagedType.U1)] bool returnValue, out byte[] ret)
        {
            if (!(value is IList lst))
            {
                ret = ConstMgr.Null;
                return false;
            }

            writer.Write(lst);
            ret = Return(returnValue, writer);
            return true;
        }

        /// <summary>
        /// Attempt to serialize dict
        /// </summary>
        /// <param name="value"></param>
        /// <param name="writer"></param>
        /// <param name="returnValue"></param>
        /// <param name="ret"></param>
        /// <typeparam name="T"></typeparam>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool TrySerializeDict<T>(T value, Writer writer,
            [MarshalAs(UnmanagedType.U1)] bool returnValue, out byte[] ret)
        {
            if (!(value is IDictionary dict))
            {
                ret = ConstMgr.Null;
                return false;
            }

            writer.Write(dict);
            ret = Return(returnValue, writer);
            return true;
        }

        /// <summary>
        /// Check for null
        /// </summary>
        /// <param name="value"></param>
        /// <param name="writer"></param>
        /// <param name="returnValue"></param>
        /// <param name="ret"></param>
        /// <typeparam name="T"></typeparam>
        /// <returns>true when null</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool WriteNullCheck<T>(T value, Writer writer,
            [MarshalAs(UnmanagedType.U1)] bool returnValue, out byte[] ret)
        {
            //null check
            if (value == null)
            {
                writer.Write(false); // if null -> write false
                ret = Return(returnValue, writer);
                return true;
            }

            writer.Write(true); // if not null -> write true
            ret = ConstMgr.Null;
            return false;
        }

        /// <summary>
        /// Get return value
        /// </summary>
        /// <param name="returnValue"></param>
        /// <param name="writer"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static byte[] Return([MarshalAs(UnmanagedType.U1)] bool returnValue, Writer writer)
        {
            byte[] ret = ConstMgr.Null;
            if (returnValue)
            {
                ret = writer.ToBytes();
                ObjectPool<Writer>.Return(writer);
            }

            return ret;
        }
    }
}