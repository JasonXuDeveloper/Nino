using System;
using System.Text;
using Nino.Shared.Mgr;
using Nino.Shared.Util;
using System.Reflection;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

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
		/// Custom exporter
		/// </summary>
		internal static readonly Dictionary<Type, ExporterDelegate> CustomExporter =
			new Dictionary<Type, ExporterDelegate>(5);

		/// <summary>
		/// Custom Exporter delegate that reads bytes to object
		/// </summary>
		internal delegate object ExporterDelegate(Reader reader);

		/// <summary>
		/// Add custom Exporter of all type T objects
		/// </summary>
		/// <param name="func"></param>
		/// <typeparam name="T"></typeparam>
		public static void AddCustomExporter<T>(Func<Reader, T> func)
		{
			var type = typeof(T);
			if (CustomExporter.ContainsKey(type))
			{
				Logger.W($"already added custom exporter for: {type}");
				return;
			}

			CustomExporter.Add(typeof(T), (reader) => func.Invoke(reader));
		}


		/// <summary>
		/// Read basic type from reader
		/// </summary>
		/// <param name="type"></param>
		/// <param name="reader"></param>
		private static object ReadBasicType(Type type, Reader reader)
		{
			switch (Type.GetTypeCode(type))
			{
				case TypeCode.Byte:
					return reader.ReadByte();
				case TypeCode.SByte:
					return reader.ReadSByte();
				case TypeCode.Int16:
					return reader.ReadInt16();
				case TypeCode.UInt16:
					return reader.ReadUInt16();
				case TypeCode.Int32:
					return (int)reader.DecompressAndReadNumber();
				case TypeCode.UInt32:
					return (uint)reader.DecompressAndReadNumber();
				case TypeCode.Int64:
					return (long)reader.DecompressAndReadNumber();
				case TypeCode.UInt64:
					return (ulong)reader.DecompressAndReadNumber();
				case TypeCode.String:
					return reader.ReadString();
				case TypeCode.Boolean:
					return reader.ReadBool();
				case TypeCode.Double:
					return reader.ReadDouble();
				case TypeCode.Single:
					return reader.ReadSingle();
				case TypeCode.Decimal:
					return reader.ReadDecimal();
				case TypeCode.Char:
					return reader.ReadChar();
				default:
					return reader.ReadCommonVal(type);
			}
		}

		/// <summary>
		/// Deserialize a NinoSerialize object
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="data"></param>
		/// <param name="encoding"></param>
		/// <returns></returns>
		public static T Deserialize<T>(byte[] data, Encoding encoding = null)
		{
			Type t = typeof(T);
			//basic type
			if (TypeModel.IsBasicType(t))
			{
				//start Deserialize
				using (var reader = new Reader(CompressMgr.Decompress(data, out var len), len, encoding ?? DefaultEncoding))
				{
					return (T)ReadBasicType(t, reader);
				}
			}
			//code generated type
			if (TypeModel.TryGetHelper(t, out var helperObj))
			{
				ISerializationHelper<T> helper = (ISerializationHelper<T>)helperObj;
				if (helper != null)
				{
					//record
					TypeModel.AddDeserializeAction(t, reader => helper.NinoReadMembers(reader));
					//start Deserialize
					using (var reader = new Reader(CompressMgr.Decompress(data, out var len), len, encoding ?? DefaultEncoding))
					{
						return helper.NinoReadMembers(reader);
					}
				}
			}

			T val = Activator.CreateInstance<T>();
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
		// ReSharper disable CognitiveComplexity
		internal static object Deserialize(Type type, object val, byte[] data, Encoding encoding, Reader reader = null)
			// ReSharper restore CognitiveComplexity
		{
			//try code gen
			if (TypeModel.TryGetDeserializeAction(type, out var func))
			{
				//share a reader
				if (reader != null)
				{
					return func(reader);
				}

				//start Deserialize
				using (reader = new Reader(CompressMgr.Decompress(data, out var len), len, encoding))
				{
					return func(reader);
				}
			}
			
			//Get Attribute that indicates a class/struct to be serialized
			TypeModel.TryGetModel(type, out var model);

			//invalid model
			if (model != null && !model.valid)
			{
				return ConstMgr.Null;
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
				//only include all model need this
				if (model.includeAll)
				{
					//read len
					var len = reader.ReadLength();
					Dictionary<string, object> values = new Dictionary<string, object>(len);
					//read elements key by key
					for (int i = 0; i < len; i++)
					{
						var key = reader.ReadString();
						var typeFullName = reader.ReadString();
						var value = reader.ReadCommonVal(Type.GetType(typeFullName));
						values.Add(key, value);
					}

					//set elements
					for (; min <= max; min++)
					{
						//prevent index not exist
						if (!model.types.ContainsKey(min)) continue;
						//get the member
						var member = model.members[min];
						//member type
						type = model.types[min];
						//try get same member and set it
						if (values.TryGetValue(member.Name, out var ret))
						{
							//type check
							if (ret.GetType() != type)
							{
								ret = type.IsEnum ? Enum.ToObject(type, ret) : Convert.ChangeType(ret, type);
							}

							SetMember(model.members[min], val, ret);
						}
					}
				}
				else
				{
					for (; min <= max; min++)
					{
						//if end, skip
						if (reader.EndOfReader) continue;
						//prevent index not exist
						if (!model.types.ContainsKey(min)) continue;
						//get type of that member
						type = model.types[min];
						//try code gen, if no code gen then reflection

						//read basic values
						var ret = reader.ReadCommonVal(type);
						//type check
						if (ret.GetType() != type)
						{
							ret = type.IsEnum ? Enum.ToObject(type, ret) : Convert.ChangeType(ret, type);
						}

						SetMember(model.members[min], val, ret);
					}
				}
			}

			//share a reader
			if (reader != null)
			{
				Read();
				return val;
			}

			//start Deserialize
			using (reader = new Reader(CompressMgr.Decompress(data, out var len), len, encoding))
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
	// ReSharper restore UnusedParameter.Local
}