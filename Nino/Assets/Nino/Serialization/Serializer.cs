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

		#region basic types
		private static readonly Type ByteType = typeof(byte);
		private static readonly Type SbyteType = typeof(sbyte);
		private static readonly Type ShortType = typeof(short);
		private static readonly Type UshortType = typeof(ushort);
		private static readonly Type INTType = typeof(int);
		private static readonly Type UintType = typeof(uint);
		private static readonly Type LongType = typeof(long);
		private static readonly Type UlongType = typeof(ulong);
		private static readonly Type StringType = typeof(string);
		#endregion

		/// <summary>
		/// Whole numbers that can consider compress
		/// </summary>
		private static readonly HashSet<Type> WholeNumToCompressType = new HashSet<Type>()
		{
			ByteType, SbyteType, ShortType, UshortType,
			INTType, UintType, LongType, UlongType
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
			using (var writer = new Writer(encoding))
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
					if (type != StringType)
					{
						WriteCommonVal(writer, type, val, encoding);
					}
					//string
					else
					{
						writer.Write((string)val);
					}
				}

				return writer.ToBytes();
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
				case ushort us:
					CompressAndWrite(writer, us);
					return;
				case byte b:
					CompressAndWrite(writer, b);
					return;
				// with sign
				case long l:
					CompressAndWrite(writer, l);
					return;
				case int i:
					CompressAndWrite(writer, i);
					return;
				case short s:
					CompressAndWrite(writer, s);
					return;
				case sbyte sb:
					CompressAndWrite(writer, sb);
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
					var dt = (byte[])val;
					//write len
					CompressAndWrite(writer, dt.Length);
					//write item
					writer.Write(dt);
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
			writer.Write(Serialize(type, val, encoding));
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
			if (type == ByteType)
			{
				WriteCommonVal(writer, type, (byte)val, encoding);
			}
			else if (type == SbyteType)
			{
				WriteCommonVal(writer, type, (sbyte)val, encoding);
			}
			else if (type == ShortType)
			{
				WriteCommonVal(writer, type, (short)val, encoding);
			}
			else if (type == UshortType)
			{
				WriteCommonVal(writer, type, (ushort)val, encoding);
			}
			else if (type == INTType)
			{
				WriteCommonVal(writer, type, (int)val, encoding);
			}
			else if (type == UintType)
			{
				WriteCommonVal(writer, type, (uint)val, encoding);
			}
			else if (type == LongType)
			{
				WriteCommonVal(writer, type, (long)val, encoding);
			}
			else if (type == UlongType)
			{
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