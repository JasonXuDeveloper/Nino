using System;
using System.IO;
using System.Text;
using Nino.Shared;
using System.Reflection;
using System.Collections;
using System.Collections.Generic;
using System.IO.Compression;

// ReSharper disable UnusedMember.Local

namespace Nino.Serialization
{
	// ReSharper disable UnusedParameter.Local
	public static class Serializer
	{
		/// <summary>
		/// Default Encoding
		/// </summary>
		private static readonly Encoding DefaultEncoding = Encoding.UTF8;

		/// <summary>
		/// Cached Models
		/// </summary>
		private static readonly Dictionary<Type, TypeModel> TypeModels = new Dictionary<Type, TypeModel>();

		/// <summary>
		/// Try get cached model
		/// </summary>
		/// <param name="type"></param>
		/// <param name="model"></param>
		/// <returns></returns>
		private static bool TryGetModel(Type type, out TypeModel model)
		{
			if (TypeModels.TryGetValue(type, out model)) return true;
			NinoSerializeAttribute[] ns =
				(NinoSerializeAttribute[])type.GetCustomAttributes(typeof(NinoSerializeAttribute), false);
			if (ns.Length != 0) return true;
			model = new TypeModel()
			{
				valid = false
			};
			TypeModels.Add(type, model);
			return false;
		}

		/// <summary>
		/// Serialize a NinoSerialize object
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="val"></param>
		/// <param name="encoding"></param>
		/// <returns></returns>
		public static byte[] Serialize<T>(T val, Encoding encoding = null)
		{
			return Serialize(typeof(T), val, encoding ?? DefaultEncoding);
		}

		/// <summary>
		/// Serialize a NinoSerialize object
		/// </summary>
		/// <param name="type"></param>
		/// <param name="value"></param>
		/// <param name="encoding"></param>
		/// <param name="writer"></param>
		/// <returns></returns>
		/// <exception cref="InvalidOperationException"></exception>
		/// <exception cref="NullReferenceException"></exception>
		private static byte[] Serialize(Type type, object value, Encoding encoding, Writer writer = null)
		{
			//Get Attribute that indicates a class/struct to be serialized
			if (!TryGetModel(type, out var model))
			{
				Logger.E("Serialization",$"The type {type.FullName} does not have NinoSerialize attribute");
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
				model = new TypeModel
				{
					min = ushort.MaxValue,
					max = ushort.MinValue,
					valid = true,
					//fetch members
					members = new Dictionary<ushort, MemberInfo>(),
					//fetch types
					types = new Dictionary<ushort, Type>()
				};

				//store temp attr
				NinoMemberAttribute sp;
				//flag
				const BindingFlags flags = BindingFlags.Default | BindingFlags.DeclaredOnly | BindingFlags.Public |
				                           BindingFlags.NonPublic | BindingFlags.Instance;

				//fetch fields (only public and private fields that declared in the type)
				FieldInfo[] fs = type.GetFields(flags);
				//iterate fields
				foreach (var f in fs)
				{
					sp = f.GetCustomAttribute(typeof(NinoMemberAttribute), false) as NinoMemberAttribute;
					//not fetch all and no attribute => skip this member
					if (sp == null) continue;
					//record field
					model.members.Add(sp.Index, f);
					model.types.Add(sp.Index, f.FieldType);
					//record min/max
					if (sp.Index < model.min)
					{
						model.min = sp.Index;
					}

					if (sp.Index > model.max)
					{
						model.max = sp.Index;
					}
				}

				//fetch properties (only public and private properties that declared in the type)
				PropertyInfo[] ps = type.GetProperties(flags);
				//iterate properties
				foreach (var p in ps)
				{
					//has to have getter and setter
					if (!(p.CanRead && p.CanWrite))
					{
						throw new InvalidOperationException(
							$"Cannot read or write property {p.Name} in {type.FullName}, cannot serialize this property");
					}

					sp = p.GetCustomAttribute(typeof(NinoMemberAttribute), false) as NinoMemberAttribute;
					//not fetch all and no attribute => skip this member
					if (sp == null) continue;
					//record property
					model.members.Add(sp.Index, p);
					model.types.Add(sp.Index, p.PropertyType);
					//record min/max
					if (sp.Index < model.min)
					{
						model.min = sp.Index;
					}

					if (sp.Index > model.max)
					{
						model.max = sp.Index;
					}
				}

				if (model.members.Count == 0)
				{
					model.valid = false;
				}
				else
				{
					//try code gen
					model.ninoGetMembers = type.GetMethod("NinoGetMembers", flags);
				}

				TypeModels.Add(type, model);
			}

			//min, max index
			ushort min = model.min, max = model.max;

			void Write()
			{
				int index = 0;
				object[] objs = null;
				if (model.ninoGetMembers != null)
				{
					objs = (object[])model.ninoGetMembers.Invoke(value, ConstMgr.EmptyParam);
				}

				for (; min <= max; min++)
				{
					//prevent index not exist
					if (!model.types.ContainsKey(min)) continue;
					//get type of that member
					type = model.types[min];
					//try code gen, if no code gen then reflection
					object val = objs != null ? objs[index] : GetVal(model.members[min], value);
					if (val == null)
					{
						throw new NullReferenceException(
							$"{type.FullName}.{model.members[min].Name} is null, cannot serialize");
					}

					//common
					if (type != ConstMgr.StringType)
					{
						WriteCommonVal(writer, type, val, encoding);
					}
					//string
					else
					{
						writer.Write((string)val);
					}

					//add the index, so it will fetch the next member (when code gen exists)
					index++;
				}
			}

			//share a writer
			if (writer != null)
			{
				Write();
				return ConstMgr.Null;
			}

			//start serialize
			using (writer = new Writer(encoding))
			{
				Write();
				//compress it
				return Compress(writer.ToBytes());
			}
		}

		/// <summary>
		/// Compress the given bytes
		/// </summary>
		/// <param name="data"></param>
		/// <returns></returns>
		private static byte[] Compress(byte[] data)
		{
			using (var compressedStream = new MemoryStream())
			using (var zipStream = new GZipStream(compressedStream, CompressionMode.Compress))
			{
				zipStream.Write(data, 0, data.Length);
				zipStream.Close();
				return compressedStream.ToArray();
			}
		}

		/// <summary>
		/// Decompress thr given bytes
		/// </summary>
		/// <param name="data"></param>
		/// <returns></returns>
		private static byte[] Decompress(byte[] data)
		{
			using (var compressedStream = new MemoryStream(data))
			using (var zipStream = new GZipStream(compressedStream, CompressionMode.Decompress))
			using (var resultStream = new MemoryStream())
			{
				zipStream.CopyTo(resultStream);
				return resultStream.ToArray();
			}
		}

		/// <summary>
		/// Get value from MemberInfo
		/// </summary>
		/// <param name="info"></param>
		/// <param name="instance"></param>
		/// <returns></returns>
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

		/// <summary>
		/// Write primitive value to binary writer
		/// </summary>
		/// <param name="writer"></param>
		/// <param name="type"></param>
		/// <param name="val"></param>
		/// <param name="encoding"></param>
		/// <exception cref="InvalidDataException"></exception>
		private static void WriteCommonVal(Writer writer, Type type, object val, Encoding encoding)
		{
			//consider to compress (only for whole num and string)
			switch (val)
			{
				//without sign
				case ulong ul:
					CompressAndWrite(writer, ul);
					return;
				case uint ui:
					CompressAndWrite(writer, ui);
					return;
				case ushort us://unnecessary to compress
					writer.Write(us);
					return;
				case byte b://unnecessary to compress
					writer.Write(b);
					return;
				// with sign
				case long l:
					CompressAndWrite(writer, l);
					return;
				case int i:
					CompressAndWrite(writer, i);
					return;
				case short s://unnecessary to compress
					writer.Write(s);
					return;
				case sbyte sb://unnecessary to compress
					writer.Write(sb);
					return;
				case bool b:
					writer.Write(b);
					return;
				case double db:
					writer.Write(db);
					return;
				case decimal dc:
					writer.Write(dc);
					return;
				case float fl:
					writer.Write(fl);
					return;
				case char c:
					writer.Write(c);
					return;
			}

			//enum
			if (type.IsEnum)
			{
				//try compress and write
				CompressAndWriteEnum(writer, type, val, encoding);
				return;
			}

			//array/ list -> recursive
			if (type.IsArray)
			{
				//byte[] -> write directly
				if (type == typeof(byte[]))
				{
					var dt = (byte[])val;
					//write len
					CompressAndWrite(writer, dt.Length);
					//write item
					writer.Write(dt);
					return;
				}

				//other type
				var elemType = type.GetElementType();
				var arr = (Array)val;
				//write len
				CompressAndWrite(writer, arr.Length);
				//write item
				foreach (var c in arr)
				{
					WriteCommonVal(writer, elemType, c, encoding);
				}

				return;
			}

			if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
			{
				//List<byte> -> write directly
				if (type == typeof(List<byte>))
				{
					var dt = (List<byte>)val;
					//write len
					CompressAndWrite(writer, dt.Count);
					//write item
					writer.Write(dt.ToArray());
					return;
				}

				//other
				var elemType = type.GenericTypeArguments[0];
				var arr = (ICollection)val;
				//write len
				CompressAndWrite(writer, arr.Count);
				//write item
				foreach (var c in arr)
				{
					WriteCommonVal(writer, elemType, c, encoding);
				}

				return;
			}

			//TODO custom exporter

			//no chance to serialize -> see if this type can be serialized in other ways
			//try recursive
			Serialize(type, val, encoding, writer);
		}

		/// <summary>
		/// Compress and write enum
		/// </summary>
		/// <param name="writer"></param>
		/// <param name="type"></param>
		/// <param name="val"></param>
		/// <param name="encoding"></param>
		private static void CompressAndWriteEnum(Writer writer, Type type, object val,
			Encoding encoding)
		{
			type = Enum.GetUnderlyingType(type);
			//typeof(byte), typeof(sbyte), typeof(short), typeof(ushort),
			//typeof(int), typeof(uint), typeof(long), typeof(ulong)
			if (type == ConstMgr.ByteType)
			{
				WriteCommonVal(writer, type, (byte)val, encoding);
			}
			else if (type == ConstMgr.SByteType)
			{
				WriteCommonVal(writer, type, (sbyte)val, encoding);
			}
			else if (type == ConstMgr.ShortType)
			{
				WriteCommonVal(writer, type, (short)val, encoding);
			}
			else if (type == ConstMgr.UShortType)
			{
				WriteCommonVal(writer, type, (ushort)val, encoding);
			}
			else if (type == ConstMgr.IntType)
			{
				WriteCommonVal(writer, type, (int)val, encoding);
			}
			else if (type == ConstMgr.UIntType)
			{
				WriteCommonVal(writer, type, (uint)val, encoding);
			}
			else if (type == ConstMgr.LongType)
			{
				WriteCommonVal(writer, type, (long)val, encoding);
			}
			else if (type == ConstMgr.ULongType)
			{
				WriteCommonVal(writer, type, (ulong)val, encoding);
			}
		}

		#region write whole number without sign

		private static void CompressAndWrite(Writer writer, ulong num)
		{
			if (num <= uint.MaxValue)
			{
				CompressAndWrite(writer, (uint)num);
				return;
			}

			writer.Write((byte)(CompressType.UInt64));
			writer.Write(num);
		}

		private static void CompressAndWrite(Writer writer, uint num)
		{
			if (num <= ushort.MaxValue)
			{
				CompressAndWrite(writer, (ushort)num);
				return;
			}

			writer.Write((byte)CompressType.UInt32);
			writer.Write(num);
		}

		private static void CompressAndWrite(Writer writer, ushort num)
		{
			//parse to byte
			if (num <= byte.MaxValue)
			{
				CompressAndWrite(writer, (byte)num);
				return;
			}

			writer.Write((byte)CompressType.UInt16);
			writer.Write(num);
		}

		private static void CompressAndWrite(Writer writer, byte num)
		{
			writer.Write((byte)CompressType.Byte);
			writer.Write(num);
		}

		#endregion

		#region write whole number with sign

		private static void CompressAndWrite(Writer writer, long num)
		{
			if (num <= int.MaxValue)
			{
				CompressAndWrite(writer, (int)num);
				return;
			}

			writer.Write((byte)CompressType.Int64);
			writer.Write(num);
		}

		private static void CompressAndWrite(Writer writer, int num)
		{
			if (num <= short.MaxValue)
			{
				CompressAndWrite(writer, (short)num);
				return;
			}

			writer.Write((byte)CompressType.Int32);
			writer.Write(num);
		}

		private static void CompressAndWrite(Writer writer, short num)
		{
			//parse to byte
			if (num <= sbyte.MaxValue)
			{
				CompressAndWrite(writer, (sbyte)num);
				return;
			}

			writer.Write((byte)CompressType.Int16);
			writer.Write(num);
		}

		private static void CompressAndWrite(Writer writer, sbyte num)
		{
			writer.Write((byte)CompressType.SByte);
			writer.Write(num);
		}

		#endregion
	}
	// ReSharper restore UnusedParameter.Local
}