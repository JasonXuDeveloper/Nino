using System;
using System.Collections.Specialized;
using System.Text;
using System.Linq;
using Nino.Serialization.Attributes;
using System.Reflection;
using System.Collections.Generic;
using System.IO;
using Nino.Serialization.Enum;
using System.Collections;

namespace Nino.Serialization
{
	public static class Serializer
	{
		public static Encoding DefaultEncoding = Encoding.Default;
		private static byte[] Null = Array.Empty<byte>();
		private static HashSet<Type> WholeNumToCompressType = new HashSet<Type>()
		{
			typeof(byte), typeof(sbyte), typeof(short), typeof(ushort),
			typeof(int), typeof(uint), typeof(long), typeof(ulong)
		};
		private static HashSet<Type> PrimitiveType = new HashSet<Type>()
		{
			typeof(double), typeof(decimal), typeof(float), typeof(bool), typeof(char)
		};

		public static byte[] Serialize<T>(T val, Encoding encoding = null)
		{
			return Serialize(typeof(T), val, encoding == null ? DefaultEncoding : encoding);
		}

		private static Dictionary<Type, bool> validTypes = new Dictionary<Type, bool>();

		private static bool CheckValidType(Type type)
		{
			if (!validTypes.ContainsKey(type))
			{
				NinoSerializeAttribute[] ns = (NinoSerializeAttribute[])type.GetCustomAttributes(typeof(NinoSerializeAttribute), false);
				if (ns?.Length == 0)
				{
					return false;
				}
				validTypes.Add(type, true);
			}
			return true;
		}

		//TODO type model

		public static byte[] Serialize(Type type, object value, Encoding encoding)
		{
			//Get Attribute that indicates a class/struct to be serialized
			if (!CheckValidType(type))
			{
				return Null;
			}

			//dict to store all members
			Dictionary<ushort, MemberInfo> members = new Dictionary<ushort, MemberInfo>();
			//store temp attr
			SerializePropertyAttribute sp;
			//min, max index
			ushort min = 0, max = 0;
			//flag
			var flags = BindingFlags.Default | BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

			//fetch fields (only public and private fields that declaed in the type)
			FieldInfo[] fs = type.GetFields(flags);
			//iterate fields
			foreach (var f in fs)
			{
				sp = f.GetCustomAttribute(typeof(SerializePropertyAttribute), false) as SerializePropertyAttribute;
				//not fetch all and no attribute => skip this member
				if (sp == null) continue;
				//record field
				members.Add(sp.Index, f);
				//record min/max
				if (sp.Index < min)
				{
					min = sp.Index;
				}
				if (sp.Index > max)
				{
					max = sp.Index;
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
					throw new InvalidOperationException($"Cannot read or write property {p.Name} in {type.FullName}, cannot serialize this property");
				}
				sp = p.GetCustomAttribute(typeof(SerializePropertyAttribute), false) as SerializePropertyAttribute;
				//not fetch all and no attribute => skip this member
				if (sp == null) continue;
				//record property
				members.Add(sp.Index, p);
				//record min/max
				if (sp.Index < min)
				{
					min = sp.Index;
				}
				if (sp.Index > max)
				{
					max = sp.Index;
				}
			}

			//temp member
			MemberInfo m;
			//temp val
			object val;

			//no chance to serialize
			if (min == max) return Null;

			//start serialize
			using (MemoryStream ms = new MemoryStream())
			{
				using (BinaryWriter bw = new BinaryWriter(ms))
				{
					for (; min <= max; min++)
					{
						m = members[min];
						type = m is FieldInfo f ? f.FieldType : ((PropertyInfo)m).PropertyType;
						val = GetVal(m, value);
						if (val == null)
						{
							throw new NullReferenceException($"{type.FullName}.{m.Name} is null, cannot serialize");
						}
						//consider to compress (only for whole num and string)
						if (WholeNumToCompressType.Contains(type))
						{
							//try compress
							CompressAndWriteMinNum(bw, val);
						}
						//primitive
						else if (type != typeof(string))
						{
							WritePrimitiveVal(bw, type, val, encoding);
						}
						//string
						else
						{
							WriteStringVal(bw, (string)val, encoding);
						}
					}
					return ms.ToArray();
				}
			}
		}

		/// <summary>
		/// Write string
		/// </summary>
		/// <param name="bw"></param>
		/// <param name="val"></param>
		/// <param name="encoding"></param>
		private static void WriteStringVal(BinaryWriter bw, string val, Encoding encoding)
		{
			var bs = encoding.GetBytes(val);
			var len = bs.Length;
			if (len <= byte.MaxValue)
			{
				bw.Write((byte)CompressType.ByteString);
				bw.Write((byte)len);
			}
			else if (len <= ushort.MaxValue)
			{
				bw.Write((byte)CompressType.UInt16String);
				bw.Write((ushort)len);
			}
			else if (len <= int.MaxValue)
			{
				bw.Write((byte)CompressType.Int32String);
				bw.Write((int)len);
			}
			bw.Write(bs);
		}

		/// <summary>
		/// Write primitive value to binary writer
		/// </summary>
		/// <param name="bw"></param>
		/// <param name="type"></param>
		/// <param name="val"></param>
		/// <exception cref="InvalidDataException"></exception>
		private static void WritePrimitiveVal(BinaryWriter bw, Type type, object val, Encoding encoding)
		{
			//array/ list -> recursive
			Type elemType;
			if (type.IsArray)
			{
				elemType = type.GetElementType();
				var arr = (Array)val;
				foreach (var c in arr)
				{
					WritePrimitiveVal(bw, elemType, c, encoding);
				}
				return;
			}
			if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>))
			{
				elemType = type.GenericTypeArguments[0];
				var arr = (ICollection)val;
				foreach (var c in arr)
				{
					WritePrimitiveVal(bw, elemType, c, encoding);
				}
				return;
			}

			//typeof(double), typeof(decimal), typeof(float), typeof(bool)
			if (PrimitiveType.Contains(type))
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
			else
			{
				//check recursive
				if (!CheckValidType(type))
				{
					throw new InvalidDataException($"Cannot serialize type: {type.FullName}");
				}
				else
				{
					bw.Write(Serialize(type, val, encoding));
				}
			}
		}

		/// <summary>
		/// Compress whole number and write
		/// </summary>
		/// <param name="num"></param>
		private static void CompressAndWriteMinNum(BinaryWriter bw, object num)
		{
			CompressType type = CompressType.Byte;
			// >=0 case
			if (num is ulong || num is uint || num is ushort || num is byte)
			{
				var n = Convert.ToUInt64(num);
				GetCompressType(ref type, n);
				//write
				WriteMinNum(bw, type, n);
			}
			// <0 case
			else
			{
				var n = Convert.ToInt64(num);
				GetCompressType(ref type, n);
				//write
				WriteMinNum(bw, type, n);
			}
		}

		/// <summary>
		/// Get compress type
		/// </summary>
		/// <param name="type"></param>
		/// <param name="num"></param>
		private static void GetCompressType(ref CompressType type, ulong num)
		{
			type = CompressType.UInt64;
			//parse to byte
			if (num <= byte.MaxValue)
			{
				type = CompressType.Byte;
			}
			//parse to ushort
			else if (num <= ushort.MaxValue)
			{
				type = CompressType.UInt16;
			}
			//parse to uint
			else if (num <= uint.MaxValue)
			{
				type = CompressType.UInt32;
			}
		}

		/// <summary>
		/// Write num
		/// </summary>
		/// <param name="bw"></param>
		/// <param name="type"></param>
		/// <param name="num"></param>
		private static void WriteMinNum(BinaryWriter bw, CompressType type, long num)
		{
			bw.Write((byte)type);
			switch (type)
			{
				case CompressType.Int64:
					bw.Write(num);
					//WriteLong(bw, num);
					break;
				case CompressType.Int32:
					bw.Write((int)num);
					//WriteInt(bw, (int)num);
					break;
				case CompressType.Int16:
					bw.Write((short)num);
					//WriteShort(bw, (short)num);
					break;
				case CompressType.SByte:
					bw.Write((byte)num);
					break;
			}
		}

		/// <summary>
		/// Write num
		/// </summary>
		/// <param name="bw"></param>
		/// <param name="type"></param>
		/// <param name="num"></param>
		private static void WriteMinNum(BinaryWriter bw, CompressType type, ulong num)
		{
			bw.Write((byte)type);
			switch (type)
			{
				case CompressType.UInt64:
					bw.Write(num);
					break;
				case CompressType.UInt32:
					bw.Write((uint)num);
					break;
				case CompressType.UInt16:
					bw.Write((ushort)num);
					break;
				case CompressType.Byte:
					bw.Write((byte)num);
					break;
			}
		}

		/// <summary>
		/// Get compress type
		/// </summary>
		/// <param name="type"></param>
		/// <param name="num"></param>
		private static void GetCompressType(ref CompressType type, long num)
		{
			type = CompressType.Int64;
			//parse to sbyte
			if (num >= sbyte.MinValue)
			{
				type = CompressType.SByte;
			}
			//parse to short
			else if (num >= short.MinValue)
			{
				type = CompressType.Int16;
			}
			//parse to int
			else if (num >= int.MinValue)
			{
				type = CompressType.Int32;
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
		/// Write int val to binary writer
		/// </summary>
		/// <param name="bw"></param>
		/// <param name="num"></param>
		private unsafe static void WriteInt(BinaryWriter bw, int num)
		{
			// a faster way -> implement in future
			//fixed(byte* p = &bytes[index])
			//{
			//	*(int*)p = num;
			//}
			bw.Write((byte)(num & 0xFF));
			bw.Write((byte)(num >> 8 & 0xFF));
			bw.Write((byte)(num >> 16 & 0xFF));
			bw.Write((byte)(num >> 24 & 0xFF));
		}

		/// <summary>
		/// Write short val to binary writer
		/// </summary>
		/// <param name="bw"></param>
		/// <param name="num"></param>
		private unsafe static void WriteShort(BinaryWriter bw, short num)
		{
			bw.Write((byte)(num & 0xFF));
			bw.Write((byte)(num >> 8 & 0xFF));
		}

		/// <summary>
		/// Write long val to binary writer
		/// </summary>
		/// <param name="bw"></param>
		/// <param name="num"></param>
		private unsafe static void WriteLong(BinaryWriter bw, long num)
		{
			bw.Write((byte)(num & 0xFF));
			bw.Write((byte)(num >> 8 & 0xFF));
			bw.Write((byte)(num >> 16 & 0xFF));
			bw.Write((byte)(num >> 24 & 0xFF));
			bw.Write((byte)(num >> 32 & 0xFF));
			bw.Write((byte)(num >> 40 & 0xFF));
			bw.Write((byte)(num >> 48 & 0xFF));
			bw.Write((byte)(num >> 56 & 0xFF));
		}
	}
}