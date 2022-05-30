using System;
using System.IO;
using System.Text;
using System.Reflection;
using System.Collections;
using System.Collections.Generic;

namespace Nino.Serialization
{
	// ReSharper disable UnusedParameter.Local
	public static class Serializer
	{
		/// <summary>
		/// Default Encoding
		/// </summary>
		// ReSharper disable MemberCanBePrivate.Global
		// ReSharper disable FieldCanBeMadeReadOnly.Global
		public static Encoding DefaultEncoding = Encoding.UTF8;
		// ReSharper restore FieldCanBeMadeReadOnly.Global
		// ReSharper restore MemberCanBePrivate.Global

		/// <summary>
		/// Null value
		/// </summary>
		private static readonly byte[] Null = Array.Empty<byte>();

		/// <summary>
		/// Whole numbers that can consider compress
		/// </summary>
		private static readonly HashSet<Type> WholeNumToCompressType = new HashSet<Type>()
		{
			typeof(byte), typeof(sbyte), typeof(short), typeof(ushort),
			typeof(int), typeof(uint), typeof(long), typeof(ulong)
		};

		/// <summary>
		/// Other system types to serialize
		/// </summary>
		private static readonly HashSet<Type> PrimitiveType = new HashSet<Type>()
		{
			typeof(double), typeof(decimal), typeof(float), typeof(bool), typeof(char)
		};

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
		/// <returns></returns>
		/// <exception cref="InvalidOperationException"></exception>
		/// <exception cref="NullReferenceException"></exception>
		private static byte[] Serialize(Type type, object value, Encoding encoding)
		{
			//Get Attribute that indicates a class/struct to be serialized
			if (!TryGetModel(type, out var model))
			{
				return Null;
			}

			//invalid model
			if (model != null)
			{
				if (!model.valid)
				{
					return Null;
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
				SerializePropertyAttribute sp;
				//flag
				const BindingFlags flags = BindingFlags.Default | BindingFlags.DeclaredOnly | BindingFlags.Public |
				                           BindingFlags.NonPublic | BindingFlags.Instance;

				//fetch fields (only public and private fields that declared in the type)
				FieldInfo[] fs = type.GetFields(flags);
				//iterate fields
				foreach (var f in fs)
				{
					sp = f.GetCustomAttribute(typeof(SerializePropertyAttribute), false) as SerializePropertyAttribute;
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
					//has to have reader and setter
					if (!(p.CanRead && p.CanWrite))
					{
						throw new InvalidOperationException(
							$"Cannot read or write property {p.Name} in {type.FullName}, cannot serialize this property");
					}

					sp = p.GetCustomAttribute(typeof(SerializePropertyAttribute), false) as SerializePropertyAttribute;
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

				TypeModels.Add(type, model);
			}

			//min, max index
			ushort min = model.min, max = model.max;

			//start serialize
			using (var ms = new MemoryStream())
			{
				using (var bw = new BinaryWriter(ms, encoding))
				{
					for (; min <= max; min++)
					{
						type = model.types[min];
						var val = GetVal(model.members[min], value);
						if (val == null)
						{
							throw new NullReferenceException(
								$"{type.FullName}.{model.members[min].Name} is null, cannot serialize");
						}

						//common
						if (type != typeof(string))
						{
							WriteCommonVal(bw, ms, type, val, encoding);
						}
						//string
						else
						{
							WriteStringVal(bw, ms, (string)val, encoding);
						}
					}

					return ms.ToArray();
				}
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
		/// Write string
		/// </summary>
		/// <param name="bw"></param>
		/// <param name="ms"></param>
		/// <param name="val"></param>
		/// <param name="encoding"></param>
		private static void WriteStringVal(BinaryWriter bw, MemoryStream ms, string val, Encoding encoding)
		{
			var len = encoding.GetByteCount(val);
			if (len <= byte.MaxValue)
			{
				bw.Write((byte)CompressType.ByteString);
				bw.Write((byte)len);
			}
			else if (len <= ushort.MaxValue)
			{
				bw.Write((byte)CompressType.UInt16String);
				WriteUShort(bw, ms, (ushort)len);
			}
			else
			{
				throw new InvalidDataException($"string is too long, len:{len}");
			}

			//write directly
			bw.Write(encoding.GetBytes(val));
		}

		/// <summary>
		/// Write primitive value to binary writer
		/// </summary>
		/// <param name="bw"></param>
		/// <param name="ms"></param>
		/// <param name="type"></param>
		/// <param name="val"></param>
		/// <param name="encoding"></param>
		/// <exception cref="InvalidDataException"></exception>
		private static void WriteCommonVal(BinaryWriter bw, MemoryStream ms, Type type, object val, Encoding encoding)
		{
			//consider to compress (only for whole num and string)
			if (WholeNumToCompressType.Contains(type))
			{
				//try compress
				CompressAndWriteMinNum(bw, ms, val);
				return;
			}

			//enum
			if (type.IsEnum)
			{
				//try compress and write
				CompressAndWriteEnum(bw, ms, type, val, encoding);
				return;
			}

			//typeof(double), typeof(decimal), typeof(float), typeof(bool), typeof(char)
			if (PrimitiveType.Contains(type))
			{
				//try write primitive
				WritePrimitive(bw, val);
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
					CompressAndWrite(bw, ms, dt.Length);
					//write item
					bw.Write(dt);
					return;
				}

				//other type
				var elemType = type.GetElementType();
				var arr = (Array)val;
				//write len
				CompressAndWrite(bw, ms, arr.Length);
				//write item
				foreach (var c in arr)
				{
					WriteCommonVal(bw, ms, elemType, c, encoding);
				}

				return;
			}

			if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
			{
				//List<byte> -> write directly
				if (type == typeof(List<byte>))
				{
					var dt = (byte[])val;
					//write len
					CompressAndWrite(bw, ms, dt.Length);
					//write item
					bw.Write(dt);
					return;
				}

				//other
				var elemType = type.GenericTypeArguments[0];
				var arr = (ICollection)val;
				//write len
				CompressAndWrite(bw, ms, arr.Count);
				//write item
				foreach (var c in arr)
				{
					WriteCommonVal(bw, ms, elemType, c, encoding);
				}

				return;
			}

			//TODO custom exporter

			//no chance to serialize -> see if this type can be serialized in other ways
			//try recursive
			bw.Write(Serialize(type, val, encoding));
		}


		/// <summary>
		/// Write primitive
		/// </summary>
		/// <param name="bw"></param>
		/// <param name="val"></param>
		private static void WritePrimitive(BinaryWriter bw, object val)
		{
			switch (val)
			{
				case bool b:
					bw.Write(b);
					break;
				case double db:
					bw.Write(db);
					break;
				case decimal dc:
					bw.Write(dc);
					break;
				case float fl:
					bw.Write(fl);
					break;
				case char c:
					bw.Write(c);
					break;
			}
		}

		/// <summary>
		/// Compress whole number and write
		/// </summary>
		/// <param name="bw"></param>
		/// <param name="ms"></param>
		/// <param name="num"></param>
		private static void CompressAndWriteMinNum(BinaryWriter bw, MemoryStream ms, object num)
		{
			switch (num)
			{
				//without sign
				case ulong ul:
					CompressAndWrite(bw, ms, ul);
					break;
				case uint ui:
					CompressAndWrite(bw, ms, ui);
					break;
				case ushort us:
					CompressAndWrite(bw, ms, us);
					break;
				case byte b:
					CompressAndWrite(bw, ms, b);
					break;
				// with sign
				case long l:
					CompressAndWrite(bw, ms, l);
					break;
				case int i:
					CompressAndWrite(bw, ms, i);
					break;
				case short s:
					CompressAndWrite(bw, ms, s);
					break;
				case sbyte sb:
					CompressAndWrite(bw, ms, sb);
					break;
			}
		}

		/// <summary>
		/// Compress and write enum
		/// </summary>
		/// <param name="bw"></param>
		/// <param name="ms"></param>
		/// <param name="type"></param>
		/// <param name="val"></param>
		/// <param name="encoding"></param>

		private static void CompressAndWriteEnum(BinaryWriter bw, MemoryStream ms, Type type, object val,
			Encoding encoding)
		{
			type = Enum.GetUnderlyingType(type);
			//typeof(byte), typeof(sbyte), typeof(short), typeof(ushort),
			//typeof(int), typeof(uint), typeof(long), typeof(ulong)
			if (type == typeof(byte))
			{
				WriteCommonVal(bw, ms, type, (byte)val, encoding);
			}
			else if (type == typeof(sbyte))
			{
				WriteCommonVal(bw, ms, type, (sbyte)val, encoding);
			}
			else if (type == typeof(short))
			{
				WriteCommonVal(bw, ms, type, (short)val, encoding);
			}
			else if (type == typeof(ushort))
			{
				WriteCommonVal(bw, ms, type, (ushort)val, encoding);
			}
			else if (type == typeof(int))
			{
				WriteCommonVal(bw, ms, type, (int)val, encoding);
			}
			else if (type == typeof(uint))
			{
				WriteCommonVal(bw, ms, type, (uint)val, encoding);
			}
			else if (type == typeof(long))
			{
				WriteCommonVal(bw, ms, type, (long)val, encoding);
			}
			else if (type == typeof(ulong))
			{
				WriteCommonVal(bw, ms, type, (ulong)val, encoding);
			}
		}

		#region write whole number without sign

		private static void CompressAndWrite(BinaryWriter bw, MemoryStream ms, ulong num)
		{
			if (num <= uint.MaxValue)
			{
				CompressAndWrite(bw, ms, (uint)num);
				return;
			}

			bw.Write((byte)CompressType.UInt64);
			WriteULong(bw, ms, num);
		}


		private static void CompressAndWrite(BinaryWriter bw, MemoryStream ms, uint num)
		{
			if (num <= ushort.MaxValue)
			{
				CompressAndWrite(bw, ms, (ushort)num);
				return;
			}

			bw.Write((byte)CompressType.UInt32);
			WriteUInt(bw, ms, num);
		}


		private static void CompressAndWrite(BinaryWriter bw, MemoryStream ms, ushort num)
		{
			//parse to byte
			if (num <= byte.MaxValue)
			{
				CompressAndWrite(bw, ms, (byte)num);
				return;
			}

			bw.Write((byte)CompressType.UInt16);
			WriteUShort(bw, ms, num);
		}
		private static void CompressAndWrite(BinaryWriter bw, MemoryStream ms, byte num)
		{
			bw.Write((byte)CompressType.Byte);
			bw.Write(num);
		}

		#endregion

		#region write whole number with sign

		private static void CompressAndWrite(BinaryWriter bw, MemoryStream ms, long num)
		{
			if (num <= int.MaxValue)
			{
				CompressAndWrite(bw, ms, (int)num);
				return;
			}

			bw.Write((byte)CompressType.Int64);
			WriteLong(bw, ms, num);
		}


		private static void CompressAndWrite(BinaryWriter bw, MemoryStream ms, int num)
		{
			if (num <= short.MaxValue)
			{
				CompressAndWrite(bw, ms, (short)num);
				return;
			}

			bw.Write((byte)CompressType.Int32);
			WriteInt(bw, ms, num);
		}


		private static void CompressAndWrite(BinaryWriter bw, MemoryStream ms, short num)
		{
			//parse to byte
			if (num <= sbyte.MaxValue)
			{
				CompressAndWrite(bw, ms, (sbyte)num);
				return;
			}

			bw.Write((byte)CompressType.Int16);
			WriteShort(bw, ms, num);
		}


		private static void CompressAndWrite(BinaryWriter bw, MemoryStream ms, sbyte num)
		{
			bw.Write((byte)CompressType.SByte);
			bw.Write(num);
		}

		#endregion


		#region write whole num

		private const byte SizeOfUInt = sizeof(uint);
		private const byte SizeOfInt = sizeof(int);
		private const byte SizeOfUShort = sizeof(ushort);
		private const byte SizeOfShort = sizeof(short);
		private const byte SizeOfULong = sizeof(ulong);
		private const byte SizeOfLong = sizeof(long);

		/// <summary>
		/// Write int val to binary writer
		/// </summary>
		/// <param name="bw"></param>
		/// <param name="ms"></param>
		/// <param name="num"></param>
		private static unsafe void WriteInt(BinaryWriter bw, MemoryStream ms, int num)
		{
			bw.Write(num);
			return;
			if (ms.Length - ms.Position < SizeOfInt)
			{
				ms.SetLength(ms.Length + SizeOfInt);
			}

			fixed (byte* p = &ms.GetBuffer()[ms.Position])
			{
				*(int*)p = num;
			}
		}

		/// <summary>
		/// Write uint val to binary writer
		/// </summary>
		/// <param name="bw"></param>
		/// <param name="ms"></param>
		/// <param name="num"></param>
		private static unsafe void WriteUInt(BinaryWriter bw, MemoryStream ms, uint num)
		{
			bw.Write(num);
			return;
			if (ms.Length - ms.Position < SizeOfUInt)
			{
				ms.SetLength(ms.Length + SizeOfUInt);
			}

			fixed (byte* p = &ms.GetBuffer()[ms.Position])
			{
				*(uint*)p = num;
			}
		}

		/// <summary>
		/// Write short val to binary writer
		/// </summary>
		/// <param name="bw"></param>
		/// <param name="ms"></param>
		/// <param name="num"></param>
		private static unsafe void WriteShort(BinaryWriter bw, MemoryStream ms, short num)
		{
			bw.Write(num);
			return;
			if (ms.Length - ms.Position < SizeOfShort)
			{
				ms.SetLength(ms.Length + SizeOfShort);
			}

			fixed (byte* p = &ms.GetBuffer()[ms.Position])
			{
				*(short*)p = num;
			}
		}

		/// <summary>
		/// Write ushort val to binary writer
		/// </summary>
		/// <param name="bw"></param>
		/// <param name="ms"></param>
		/// <param name="num"></param>
		private static unsafe void WriteUShort(BinaryWriter bw, MemoryStream ms, ushort num)
		{
			bw.Write(num);
			return;
			if (ms.Length - ms.Position < SizeOfUShort)
			{
				ms.SetLength(ms.Length + SizeOfUShort);
			}

			fixed (byte* p = &ms.GetBuffer()[ms.Position])
			{
				*(ushort*)p = num;
			}
		}

		/// <summary>
		/// Write long val to binary writer
		/// </summary>
		/// <param name="bw"></param>
		/// <param name="ms"></param>
		/// <param name="num"></param>
		private static unsafe void WriteLong(BinaryWriter bw, MemoryStream ms, long num)
		{
			bw.Write(num);
			return;
			if (ms.Length - ms.Position < SizeOfLong)
			{
				ms.SetLength(ms.Length + SizeOfLong);
			}

			fixed (byte* p = &ms.GetBuffer()[ms.Position])
			{
				*(long*)p = num;
			}
		}

		/// <summary>
		/// Write ulong val to binary writer
		/// </summary>
		/// <param name="bw"></param>
		/// <param name="ms"></param>
		/// <param name="num"></param>
		private static unsafe void WriteULong(BinaryWriter bw, MemoryStream ms, ulong num)
		{
			bw.Write(num);
			return;
			if (ms.Length - ms.Position < SizeOfULong)
			{
				ms.SetLength(ms.Length + SizeOfULong);
			}

			fixed (byte* p = &ms.GetBuffer()[ms.Position])
			{
				*(ulong*)p = num;
			}
		}

		#endregion
	}
	// ReSharper restore UnusedParameter.Local
}