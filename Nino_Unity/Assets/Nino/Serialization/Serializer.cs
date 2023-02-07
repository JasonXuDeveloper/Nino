using System;
using Nino.Shared.IO;
using Nino.Shared.Mgr;
using System.Reflection;
using System.Collections;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;

// ReSharper disable UnusedMember.Local

namespace Nino.Serialization
{
    // ReSharper disable UnusedParameter.Local
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
            WrapperManifest.AddWrapper(typeof(T), genericWrapper);
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
        /// <param name="skipBasicCheck"></param>
        /// <param name="skipCodeGenCheck"></param>
        /// <param name="skipGenericCheck"></param>
        /// <param name="skipEnumCheck"></param>
        /// <returns></returns>
        /// <exception cref="InvalidOperationException"></exception>
        /// <exception cref="NullReferenceException"></exception>
        // ReSharper disable CognitiveComplexity
        internal static byte[] Serialize<T>(Type type, T value, Writer writer,
                CompressOption option = CompressOption.Zlib,
                bool returnValue = true, bool skipBasicCheck = false, bool skipCodeGenCheck = false,
                bool skipGenericCheck = false, bool skipEnumCheck = false)
            // ReSharper restore CognitiveComplexity
        {
            bool boxed = false;
            if (type != typeof(T) || typeof(T) == ConstMgr.ObjectType)
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

            //basic type
            if (!skipBasicCheck && WrapperManifest.TryGetWrapper(type, out var wrapper))
            {
                if (writer == null)
                {
                    writer = ObjectPool<Writer>.Request();
                }

                if (returnValue)
                {
                    writer.Init(TypeModel.IsNonCompressibleType(type) ? CompressOption.NoCompression : option);
                }

                if (boxed)
                {
                    wrapper.Serialize(value, writer);
                }
                else
                {
                    ((NinoWrapperBase<T>)wrapper).Serialize(value, writer);
                }

                return Return(returnValue, writer);
            }

            if (writer == null)
            {
                writer = ObjectPool<Writer>.Request();
            }

            //enum			
            if (!skipEnumCheck && TypeModel.IsEnum(type))
            {
                type = Enum.GetUnderlyingType(type);
                //use basic type wrapper
                if (!skipBasicCheck && WrapperManifest.TryGetWrapper(type, out wrapper))
                {
                    if (returnValue)
                    {
                        writer.Init(TypeModel.IsNonCompressibleType(type) ? CompressOption.NoCompression : option);
                    }

                    writer.CompressAndWriteEnum(value);
                    return Return(returnValue, writer);
                }

                throw new InvalidCastException("Failed to cast enum to basic type");
            }

            //code generated type
            if (!skipCodeGenCheck && TypeModel.TryGetWrapper(type, out wrapper))
            {
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

                return Return(returnValue, writer);
            }

            //null check
            if (value == null)
            {
                writer.Write(false);
                return Return(returnValue, writer);
            }

            //array
            if (!skipGenericCheck)
            {
                //array
                if (value is Array arr)
                {
                    writer.Write(arr);
                    return Return(returnValue, writer);
                }

                //list, dict
                if (type.IsGenericType)
                {
                    switch (value)
                    {
                        case IList lst:
                            writer.Write(lst);
                            break;
                        case IDictionary dict:
                            writer.Write(dict);
                            break;
                    }

                    return Return(returnValue, writer);
                }
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

            //min, max index
            ushort min = model.Min, max = model.Max;

            //not null
            writer.Write(true);

            //only include all model need this
            if (model.IncludeAll)
            {
                //write len
                writer.CompressAndWrite(model.Members.Count);
            }

            while (min <= max)
            {
                //prevent index not exist
                if (!model.Types.ContainsKey(min))
                {
                    min++;
                    continue;
                }

                //get type of that member
                type = model.Types[min];

                //only include all model need this
                if (model.IncludeAll)
                {
                    var needToStore = model.Members[min];
                    writer.Write(needToStore.Name);
                    writer.Write(type.AssemblyQualifiedName);
                }

                //try code gen, if no code gen then reflection
                object val = GetVal(model.Members[min], value);
                Serialize(type, val, writer, option, false);
                min++;
            }

            return Return(returnValue, writer);
        }

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

        /// <summary>
        /// Get value from MemberInfo
        /// </summary>
        /// <param name="info"></param>
        /// <param name="instance"></param>
        /// <returns></returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static object GetVal(MemberInfo info, object instance)
        {
            switch (info)
            {
                case FieldInfo fo:
                    return fo.GetValue(instance);
                case PropertyInfo po:
                    return po.GetValue(instance);
                default:
                    return null;
            }
        }
    }
    // ReSharper restore UnusedParameter.Local
}