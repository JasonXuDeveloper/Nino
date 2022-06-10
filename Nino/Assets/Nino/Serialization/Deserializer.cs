using System;
using System.IO;
using System.Text;
using Nino.Shared;
using System.Linq;
using System.Reflection;
using System.Collections;
using System.Collections.Generic;

// ReSharper disable UnusedMember.Local

namespace Nino.Serialization
{
	// ReSharper disable UnusedParameter.Local
	public static class Deserializer
	{
		/// <summary>
		/// Default Encoding
		/// </summary>
		private static readonly Encoding DefaultEncoding = Encoding.UTF8;

		/// <summary>
		/// 缓存反射的参数数组
		/// </summary>
		private static readonly Queue<object[]> ReflectionParamPool = new Queue<object[]>();
		
		/// <summary>
		/// Deserialize a NinoSerialize object
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="data"></param>
		/// <param name="encoding"></param>
		/// <returns></returns>
		public static T Deserialize<T>(byte[] data, Encoding encoding = null) where T: new()
		{
			T val = new T();
			return (T)Deserialize(typeof(T), val, data, encoding ?? DefaultEncoding);
		}

		/// <summary>
		/// Deserialize a NinoSerialize object
		/// </summary>
		/// <param name="type"></param>
		/// <param name="val"></param>
		/// <param name="data"></param>
		/// <param name="encoding"></param>
		/// <param name="reader"></param>
		/// <returns></returns>
		/// <exception cref="InvalidOperationException"></exception>
		/// <exception cref="NullReferenceException"></exception>
		private static object Deserialize(Type type, object val, byte[] data, Encoding encoding, Reader reader = null)
		{
			//Get Attribute that indicates a class/struct to be serialized
			if (!TypeModel.TryGetModel(type, out var model))
			{
				Logger.E("Serialization", $"The type {type.FullName} does not have NinoSerialize attribute");
				return ConstMgr.Null;
			}

			//invalid model
			if (model != null)
			{
				if (!model.valid)
				{
					return ConstMgr.Null;
				}
			}

			//generate model
			if (model == null)
			{
				model = TypeModel.CreateModel(type);
			}

			//create type
			if (val == null || val == ConstMgr.Null)
			{
				val = Activator.CreateInstance(type);
			}

			//min, max index
			ushort min = model.min, max = model.max;

			void Read()
			{
				int index = 0;
				bool hasSet = model.ninoSetMembers != null;
				object[] objs = ConstMgr.EmptyParam;
				if (hasSet)
				{
					objs = new object[model.members.Count];
				}

				for (; min <= max; min++)
				{
					//prevent index not exist
					if (!model.types.ContainsKey(min)) continue;
					//get type of that member
					type = model.types[min];
					//try code gen, if no code gen then reflection

					//common
					if (type != ConstMgr.StringType)
					{
						var ret = ReadCommonVal(reader, type, encoding);
						if (hasSet)
						{
							objs[index] = ret;
						}
						else
						{
							SetMember(model.members[min], val, ret);
						}
					}
					//string
					else
					{
						var ret = reader.ReadString();
						if (hasSet)
						{
							objs[index] = ret;
						}
						else
						{
							SetMember(model.members[min], val, ret);
						}
					}

					//add the index, so it will fetch the next member (when code gen exists)
					index++;
				}
				
				//invoke code gen
				if (hasSet)
				{
					object[] p;
					if (ReflectionParamPool.Count > 0)
					{
						p = ReflectionParamPool.Dequeue();
						p[0] = objs;
					}
					else
					{
						p = new object[] { objs };
					}
					model.ninoSetMembers.Invoke(val, p);
					ReflectionParamPool.Enqueue(p);
				}
			}

			//share a reader
			if (reader != null)
			{
				Read();
				return val;
			}

			//start Deserialize
			using (reader = new Reader(CompressMgr.Decompress(data), encoding))
			{
				Read();
				return val;
			}
		}

		/// <summary>
		/// Set value from MemberInfo
		/// </summary>
		/// <param name="info"></param>
		/// <param name="instance"></param>
		/// <param name="val"></param>
		/// <returns></returns>
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

		/// <summary>
		/// Read primitive value from binary writer
		/// </summary>
		/// <param name="reader"></param>
		/// <param name="type"></param>
		/// <param name="encoding"></param>
		/// <exception cref="InvalidDataException"></exception>
		private static object ReadCommonVal(Reader reader, Type type, Encoding encoding)
		{
			//consider to compress (only for whole num and string)
			//typeof(byte), typeof(sbyte), typeof(short), typeof(ushort),
			//typeof(int), typeof(uint), typeof(long), typeof(ulong)
			if (type == ConstMgr.ByteType)
			{
				return reader.ReadByte();
			}

			if (type == ConstMgr.SByteType)
			{
				return reader.ReadSByte();
			}

			if (type == ConstMgr.ShortType)
			{
				return reader.ReadInt16();
			}

			if (type == ConstMgr.UShortType)
			{
				return reader.ReadUInt16();
			}

			//consider decompress
			if (type == ConstMgr.IntType || type == ConstMgr.UIntType || type == ConstMgr.LongType ||
			    type == ConstMgr.ULongType)
			{
				switch (reader.GetCompressType())
				{
					case CompressType.Byte:
						return reader.ReadByte();
					case CompressType.SByte:
						return reader.ReadSByte();
					case CompressType.Int16:
						return reader.ReadInt16();
					case CompressType.UInt16:
						return reader.ReadUInt16();
					case CompressType.Int32:
						return reader.ReadInt32();
					case CompressType.UInt32:
						return reader.ReadUInt32();
					case CompressType.Int64:
						return reader.ReadInt64();
					case CompressType.UInt64:
						return reader.ReadUInt64();
				}
			}
			else if (type == ConstMgr.BoolType)
			{
				return reader.ReadBool();
			}
			else if (type == ConstMgr.DoubleType)
			{
				return reader.ReadDouble();
			}
			else if (type == ConstMgr.DecimalType)
			{
				return reader.ReadDecimal();
			}
			else if (type == ConstMgr.FloatType)
			{
				return reader.ReadSingle();
			}
			else if (type == ConstMgr.CharType)
			{
				return reader.ReadChar();
			}

			//enum
			if (type.IsEnum)
			{
				//try decompress and read
				return DecompressAndReadEnum(reader, type, encoding);
			}

			int GETLen()
			{
				switch (reader.GetCompressType())
				{
					case CompressType.Byte:
						return reader.ReadByte();
					case CompressType.SByte:
						return reader.ReadSByte();
					case CompressType.Int16:
						return reader.ReadInt16();
					case CompressType.UInt16:
						return reader.ReadUInt16();
					case CompressType.Int32:
						return reader.ReadInt32();
				}

				return 0;
			}

			//array/ list -> recursive
			if (type.IsArray)
			{
				//read len
				int len = GETLen();

				//byte[] -> write directly
				if (type == typeof(byte[]))
				{
					//read item
					return reader.ReadBytes(len);
				}

				//other type
				var elemType = type.GetElementType();
				if (elemType == null)
				{
					throw new NullReferenceException("element type is null, can not make array");
				}

				var arr = Array.CreateInstance(elemType, len);
				//read item
				for (int i = 0; i < len; i++)
				{
					arr.SetValue(ReadCommonVal(reader, elemType, encoding), i);
				}

				return arr;
			}

			if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
			{
				//read len
				int len = GETLen();

				//List<byte> -> write directly
				if (type == typeof(List<byte>))
				{
					return reader.ReadBytes(len).ToList();
				}

				//other
				var elemType = type.GenericTypeArguments[0];
				Type listType = typeof(List<>);
				Type newType = listType.MakeGenericType(elemType);
				var arr = Activator.CreateInstance(newType, ConstMgr.EmptyParam) as IList;
				//read item
				for (int i = 0; i < len; i++)
				{
					arr?.Add(ReadCommonVal(reader, elemType, encoding));
				}

				return arr;
			}

			//TODO custom exporter

			//no chance to Deserialize -> see if this type can be serialized in other ways
			//try recursive
			return Deserialize(type, ConstMgr.Null, ConstMgr.Null, encoding, reader);
		}

		/// <summary>
		/// Compress and write enum
		/// </summary>
		/// <param name="reader"></param>
		/// <param name="type"></param>
		/// <param name="encoding"></param>
		private static object DecompressAndReadEnum(Reader reader, Type type,
			Encoding encoding)
		{
			type = Enum.GetUnderlyingType(type);
			//typeof(byte), typeof(sbyte), typeof(short), typeof(ushort),
			//typeof(int), typeof(uint), typeof(long), typeof(ulong)
			return ReadCommonVal(reader, type, encoding);
		}
	}
	// ReSharper restore UnusedParameter.Local
}