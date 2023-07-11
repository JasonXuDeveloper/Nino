// Serializer.Helper.cs
// 
// Author:
//        JasonXuDeveloper（傑） <jasonxudeveloper@gmail.com>
// 
// Copyright (c) 2023 Nino

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using Nino.Shared.Mgr;

namespace Nino.Serialization
{
    public static partial class Serializer
    {
        private static readonly Dictionary<Type, int> FixedSizeCache = new Dictionary<Type, int>()
        {
            { typeof(bool), 1 },
            { typeof(byte), 1 },
            { typeof(sbyte), 1 },
            { typeof(char), 2 },
            { typeof(short), 2 },
            { typeof(ushort), 2 },
            { typeof(int), 4 },
            { typeof(uint), 4 },
            { typeof(long), 8 },
            { typeof(ulong), 8 },
            { typeof(float), 4 },
            { typeof(double), 8 },
            { typeof(decimal), 16 },
            { typeof(DateTime), 8 },
            { typeof(TimeSpan), 8 },
            { typeof(Guid), 16 },
            { typeof(IntPtr), 8 },
            { typeof(UIntPtr), 8 },
        };

        public static int GetFixedSize<T>() where T : unmanaged
        {
            if (FixedSizeCache.TryGetValue(typeof(T), out var size))
            {
                return size;
            }

            return -1;
        }
        
        public static void SetFixedSize<T>(int size) where T : unmanaged
        {
            FixedSizeCache[typeof(T)] = size;
        }

        public static int GetSize<T>(in T val = default, Dictionary<MemberInfo, object> members = null)
        {
            var type = typeof(T);
            int size = 0;
            if (TypeModel.IsEnum(type)) type = type.GetEnumUnderlyingType();

            //nullable
            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>))
            {
                type = type.GetGenericArguments()[0];
                size += 1;
            }

            if (val == null) return 1;

            if (FixedSizeCache.TryGetValue(type, out var size2))
            {
                return size + size2;
            }
            
            if (!WrapperManifest.TryGetWrapper(type, out var wrapper))
            {
                //code generated type
                if (TypeModel.TryGetWrapper(type, out wrapper))
                {
                    //add wrapper
                    WrapperManifest.AddWrapper(type, wrapper);
                }
            }

            if (wrapper != null)
            {
                if (wrapper is NinoWrapperBase<T> @base)
                {
                    return size + @base.GetSize(val);
                }

                return size + wrapper.GetSize(val);
            }

            return GetSize(type, val, members);
        }

        public static int GetSize(Type type, object obj, Dictionary<MemberInfo, object> members = null)
        {
            if (TypeModel.IsEnum(type)) type = type.GetEnumUnderlyingType();

            int size = 0;
            bool isGeneric = type.IsGenericType;
            Type genericTypeDef = null;
            Type[] genericArgs = null;
            if (isGeneric)
            {
                genericTypeDef = type.GetGenericTypeDefinition();
                genericArgs = type.GetGenericArguments();
            }

            //nullable
            if (genericTypeDef != null && genericTypeDef == typeof(Nullable<>))
            {
                type = genericArgs[0];
                size += 1;
            }

            if (obj == null) return 1;

            if (FixedSizeCache.TryGetValue(type, out var size2))
            {
                return size + size2;
            }

            if (!WrapperManifest.TryGetWrapper(type, out var wrapper))
            {
                //code generated type
                if (TypeModel.TryGetWrapper(type, out wrapper))
                {
                    //add wrapper
                    WrapperManifest.AddWrapper(type, wrapper);
                }
            }

            if (wrapper != null)
            {
                return size + wrapper.GetSize(obj);
            }

            size = 1; //null indicator
            
            if (type == ConstMgr.ObjectType)
            {
                type = obj.GetType();
            }

            if (type.IsArray)
            {
                var elementType = type.GetElementType();
                var array = obj as Array;
                if (array == null)
                {
                    return size;
                }

                size += 4; //length
                if (array.Length == 0) return size;

                if (elementType != null && FixedSizeCache.TryGetValue(elementType, out var eleSize))
                {
                    size += eleSize * array.Length;
                    return size;
                }

                foreach (var item in array)
                {
                    size += GetSize(elementType, item);
                }

                return size;
            }

            if (isGeneric && genericTypeDef != null)
            {
                switch (genericTypeDef)
                {
                    case var _ when genericTypeDef.GetInterface(nameof(IList)) != null:
                        var lstElemType = genericArgs[0];
                        var lst = obj as ICollection;
                        if (lst == null)
                        {
                            return size;
                        }

                        size += 4; //length
                        if (lst.Count == 0) return size;

                        if (FixedSizeCache.TryGetValue(lstElemType, out var lstEleSize))
                        {
                            size += lstEleSize * lst.Count;
                            return size;
                        }

                        foreach (var item in lst)
                        {
                            size += GetSize(lstElemType, item);
                        }

                        return size;
                    case var _ when genericTypeDef.GetInterface(nameof(IDictionary)) != null:
                        var keyType = genericArgs[0];
                        var valueType = genericArgs[1];
                        var dict = obj as IDictionary;
                        if (dict == null)
                        {
                            return size;
                        }

                        size += 4; //length
                        if (dict.Count == 0) return size;

                        bool hasKeySize = FixedSizeCache.TryGetValue(keyType, out var keySize);
                        bool hasValueSize = FixedSizeCache.TryGetValue(valueType, out var valueSize);

                        if (hasKeySize && hasValueSize)
                        {
                            size += (keySize + valueSize) * dict.Count;
                            return size;
                        }

                        if (hasKeySize)
                        {
                            size += keySize * dict.Count;
                            foreach (var item in dict.Values)
                            {
                                size += GetSize(valueType, item);
                            }

                            return size;
                        }

                        if (hasValueSize)
                        {
                            size += valueSize * dict.Count;
                            foreach (var item in dict.Keys)
                            {
                                size += GetSize(keyType, item);
                            }

                            return size;
                        }

                        foreach (DictionaryEntry item in dict)
                        {
                            size += GetSize(keyType, item.Key);
                            size += GetSize(valueType, item.Value);
                        }

                        return size;
                    case var _ when genericTypeDef.GetInterface(nameof(ICollection)) != null:
                        var elementType2 = genericArgs[0];
                        var collection = obj as ICollection;
                        if (collection == null)
                        {
                            return size;
                        }

                        size += 4; //length
                        if (collection.Count == 0) return size;

                        if (FixedSizeCache.TryGetValue(elementType2, out var eleSize))
                        {
                            size += eleSize * collection.Count;
                            return size;
                        }

                        foreach (var item in collection)
                        {
                            size += GetSize(elementType2, item);
                        }

                        return size;
                    case var _ when genericTypeDef.GetInterface(nameof(IEnumerable)) != null:
                        var elementType = genericArgs[0];
                        var enumerable = obj as IEnumerable;
                        if (enumerable == null)
                        {
                            return size;
                        }

                        size += 4; //length

                        var enumerator = enumerable.GetEnumerator();
                        if (!enumerator.MoveNext()) return size;
                        do
                        {
                            size += GetSize(elementType, enumerator.Current);
                        } while (enumerator.MoveNext());

                        return size;
                }
            }

            //Nino serializable type
            TypeModel.TryGetModel(type, out var model);

            //invalid model
            if (model != null && !model.Valid)
            {
                throw new InvalidOperationException($"Invalid model for type {type}");
            }

            //generate model
            if (model == null)
            {
                model = TypeModel.CreateModel(type);
            }

            var isFixed = true;
            foreach (var member in model.Members)
            {
                Type memberType;
                object memberObj;
                switch (member)
                {
                    case FieldInfo fi:
                        memberType = fi.FieldType;
                        memberObj = fi.GetValue(obj);
                        break;
                    case PropertyInfo pi:
                        memberType = pi.PropertyType;
                        memberObj = pi.GetValue(obj);
                        break;
                    default:
                        throw new Exception("Invalid member type");
                }

                if (members != null)
                {
                    members[member] = memberObj;
                }

                size += GetSize(memberType, memberObj);
                if (!FixedSizeCache.ContainsKey(memberType))
                {
                    isFixed = false;
                }
            }

            if (isFixed)
                FixedSizeCache[type] = size;
            return size;
        }
    }
}