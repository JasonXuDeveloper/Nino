using System;
using System.Collections.Specialized;
using System.Text;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using System.IO;
using System.Collections;
using System.Runtime.CompilerServices;

namespace Nino.Serialization
{
	public static class Serializer
	{
		/// <summary>
		/// Default Encoding
		/// </summary>
		public static Encoding DefaultEncoding = Encoding.UTF8;

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
			if (!TypeModels.ContainsKey(type))
			{
				NinoSerializeAttribute[] ns =
					(NinoSerializeAttribute[])type.GetCustomAttributes(typeof(NinoSerializeAttribute), false);
				if (ns.Length == 0)
				{
					model = new TypeModel()
					{
						valid = false
					};
					TypeModels.Add(type, model);
					return false;
				}

				model = null;
				return true;
			}

			model = TypeModels[type];
			return true;
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
		public static byte[] Serialize(Type type, object value, Encoding encoding)
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
				model = new TypeModel()
				{
					min = ushort.MaxValue,
					max = ushort.MinValue,
					valid = true
				};

				//fetch members
				model.members = new Dictionary<ushort, MemberInfo>();
				//fetch types
				model.types = new Dictionary<ushort, Type>();

				//store temp attr
				SerializePropertyAttribute sp;
				//flag
				var flags = BindingFlags.Default | BindingFlags.DeclaredOnly | BindingFlags.Public |
							BindingFlags.NonPublic | BindingFlags.Instance;

				//fetch fields (only public and private fields that declaed in the type)
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

				//fetch properties (only public and private properties that declaed in the type)
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

			//temp member
			MemberInfo m;
			//temp val
			object val;

			//start serialize
			using (MemoryStream ms = new MemoryStream())
			{
				using (BinaryWriter bw = new BinaryWriter(ms, encoding))
				{
					for (; min <= max; min++)
					{
						m = model.members[min];
						type = model.types[min];
						val = GetVal(m, value);
						if (val == null)
						{
							throw new NullReferenceException($"{type.FullName}.{m.Name} is null, cannot serialize");
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
		/// Get value from memberinfo
		/// </summary>
		/// <param name="info"></param>
		/// <param name="instance"></param>
		/// <returns></returns>

		private static object GetVal(MemberInfo info, object instance)
		{
			if (info is FieldInfo fo) return fo.GetValue(instance);
			if (info is PropertyInfo po) return po.GetValue(instance);
			return null;
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
			Type elemType;
			if (type.IsArray)
			{
				//byte[] -> write directly
				if (type == typeof(byte[]))
				{
					bw.Write((byte[])val);
					return;
				}

				//other type
				elemType = type.GetElementType();
				var arr = (Array)val;
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
					bw.Write(((List<byte>)val).ToArray());
					return;
				}

				//other
				elemType = type.GenericTypeArguments[0];
				var arr = (ICollection)val;
				foreach (var c in arr)
				{
					WriteCommonVal(bw, ms, elemType, c, encoding);
				}

				return;
			}

			//check recursive
			if (!TryGetModel(type, out _))
			{
				throw new InvalidDataException($"Cannot serialize type: {type.FullName}");
			}
			else
			{
				bw.Write(Serialize(type, val, encoding));
			}
		}


		/// <summary>
		/// Write primitive
		/// </summary>
		/// <param name="bw"></param>
		/// <param name="val"></param>

		private static void WritePrimitive(BinaryWriter bw, object val)
		{
			if (val is bool b)
			{
				bw.Write(b);
			}
			else if (val is double db)
			{
				bw.Write(db);
			}
			else if (val is decimal dc)
			{
				bw.Write(dc);
			}
			else if (val is float fl)
			{
				bw.Write(fl);
			}
			else if (val is char c)
			{
				bw.Write(c);
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
			//without sign
			if (num is ulong ul)
			{
				CompressAndWrite(bw, ms, ul);
			}
			else if (num is uint ui)
			{
				CompressAndWrite(bw, ms, ui);
			}
			else if (num is ushort us)
			{
				CompressAndWrite(bw, ms, us);
			}
			else if (num is byte b)
			{
				CompressAndWrite(bw, ms, b);
			}

			// with sign
			if (num is long l)
			{
				CompressAndWrite(bw, ms, l);
			}
			else if (num is int i)
			{
				CompressAndWrite(bw, ms, i);
			}
			else if (num is short s)
			{
				CompressAndWrite(bw, ms, s);
			}
			else if (num is sbyte sb)
			{
				CompressAndWrite(bw, ms, sb);
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
			//parse to byte
			if (num <= byte.MaxValue)
			{
				CompressAndWrite(bw, ms, (byte)num);
				return;
			}

			if (num <= ushort.MaxValue)
			{
				CompressAndWrite(bw, ms, (ushort)num);
				return;
			}

			if (num <= uint.MaxValue)
			{
				CompressAndWrite(bw, ms, (uint)num);
				return;
			}

			CompressType type = CompressType.UInt64;
			bw.Write((byte)type);
			WriteULong(bw, ms, num);
		}


		private static void CompressAndWrite(BinaryWriter bw, MemoryStream ms, uint num)
		{
			//parse to byte
			if (num <= byte.MaxValue)
			{
				CompressAndWrite(bw, ms, (byte)num);
				return;
			}

			if (num <= ushort.MaxValue)
			{
				CompressAndWrite(bw, ms, (ushort)num);
				return;
			}

			CompressType type = CompressType.UInt32;
			bw.Write((byte)type);
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

			CompressType type = CompressType.UInt16;
			bw.Write((byte)type);
			WriteUShort(bw, ms, num);
		}


		private static void CompressAndWrite(BinaryWriter bw, MemoryStream ms, byte num)
		{
			CompressType type = CompressType.Byte;
			bw.Write((byte)type);
			bw.Write(num);
		}

		#endregion

		#region write whole number with sign

		private static void CompressAndWrite(BinaryWriter bw, MemoryStream ms, long num)
		{
			//parse to byte
			if (num <= sbyte.MaxValue)
			{
				CompressAndWrite(bw, ms, (sbyte)num);
				return;
			}

			if (num <= short.MaxValue)
			{
				CompressAndWrite(bw, ms, (short)num);
				return;
			}

			if (num <= int.MaxValue)
			{
				CompressAndWrite(bw, ms, (int)num);
				return;
			}

			CompressType type = CompressType.Int64;
			bw.Write((byte)type);
			WriteLong(bw, ms, num);
		}


		private static void CompressAndWrite(BinaryWriter bw, MemoryStream ms, int num)
		{
			//parse to byte
			if (num <= sbyte.MaxValue)
			{
				CompressAndWrite(bw, ms, (sbyte)num);
				return;
			}

			if (num <= short.MaxValue)
			{
				CompressAndWrite(bw, ms, (short)num);
				return;
			}

			CompressType type = CompressType.Int32;
			bw.Write((byte)type);
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

			CompressType type = CompressType.Int16;
			bw.Write((byte)type);
			WriteShort(bw, ms, num);
		}


		private static void CompressAndWrite(BinaryWriter bw, sbyte num)
		{
			CompressType type = CompressType.SByte;
			bw.Write((byte)type);
			bw.Write(num);
		}

		#endregion

		/// <summary>
		/// Write int val to binary writer
		/// </summary>
		/// <param name="bw"></param>
		/// <param name="ms"></param>
		/// <param name="num"></param>
		private static unsafe void WriteInt(BinaryWriter bw, MemoryStream ms, int num)
		{
			byte size = sizeof(int);
			if (ms.Length - ms.Position < size)
			{
				ms.SetLength(ms.Length + size);
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
			byte size = sizeof(uint);
			if (ms.Length - ms.Position < size)
			{
				ms.SetLength(ms.Length + size);
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
			byte size = sizeof(short);
			if (ms.Length - ms.Position < size)
			{
				ms.SetLength(ms.Length + size);
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
			byte size = sizeof(ushort);
			if (ms.Length - ms.Position < size)
			{
				ms.SetLength(ms.Length + size);
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
			byte size = sizeof(long);
			if (ms.Length - ms.Position < size)
			{
				ms.SetLength(ms.Length + size);
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
			byte size = sizeof(ulong);
			if (ms.Length - ms.Position < size)
			{
				ms.SetLength(ms.Length + size);
			}

			fixed (byte* p = &ms.GetBuffer()[ms.Position])
			{
				*(ulong*)p = num;
			}
		}
	}
}