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
	public static class Serializer
	{
		/// <summary>
		/// Default Encoding
		/// </summary>
		private static readonly Encoding DefaultEncoding = Encoding.UTF8;

		/// <summary>
		/// Custom importer
		/// </summary>
		internal static readonly Dictionary<Type, ImporterDelegate> CustomImporter =
			new Dictionary<Type, ImporterDelegate>(5);

		/// <summary>
		/// Custom importer delegate that writes object to writer
		/// </summary>
		internal delegate void ImporterDelegate(object val, Writer writer);

		/// <summary>
		/// Add custom importer of all type T objects
		/// </summary>
		/// <param name="action"></param>
		/// <typeparam name="T"></typeparam>
		public static void AddCustomImporter<T>(Action<T, Writer> action)
		{
			var type = typeof(T);
			if (CustomImporter.ContainsKey(type))
			{
				Logger.W($"already added custom importer for: {type}");
				return;
			}
			CustomImporter.Add(typeof(T), (val, writer) => { action.Invoke((T)val, writer); });
		}

		/// <summary>
		/// Write basic type to writer
		/// </summary>
		/// <param name="val"></param>
		/// <param name="writer"></param>
		/// <typeparam name="T"></typeparam>
		// ReSharper disable CognitiveComplexity
		private static bool AttemptWriteBasicType<T>(T val, Writer writer)
			// ReSharper restore CognitiveComplexity
		{
			switch (val)
			{
				//without sign
				case ulong ul:
					writer.CompressAndWrite(ul);
					return true;
				case uint ui:
					writer.CompressAndWrite(ui);
					return true;
				case ushort us: //unnecessary to compress
					writer.Write(us);
					return true;
				case byte b: //unnecessary to compress
					writer.Write(b);
					return true;
				// with sign
				case long l:
					writer.CompressAndWrite(l);
					return true;
				case int i:
					writer.CompressAndWrite(i);
					return true;
				case short s: //unnecessary to compress
					writer.Write(s);
					return true;
				case sbyte sb: //unnecessary to compress
					writer.Write(sb);
					return true;
				case bool b:
					writer.Write(b);
					return true;
				case double db:
					writer.Write(db);
					return true;
				case decimal dc:
					writer.Write(dc);
					return true;
				case float fl:
					writer.Write(fl);
					return true;
				case char c:
					writer.Write(c);
					return true;
				case string s:
					writer.Write(s);
					return true;
				case DateTime dt:
					writer.Write(dt);
					return true;
				default:
					var type = typeof(T);
					if (type == ConstMgr.ObjectType)
					{
						//unbox
						type = val.GetType();
						//failed to unbox
						if (type == ConstMgr.ObjectType)
							return false;
					}
					//basic type
					//看看有没有注册委托，没的话有概率不行
					if (!CustomImporter.ContainsKey(type) &&
					    !Deserializer.CustomExporter.ContainsKey(type))
					{
						//比如泛型，只能list和dict
						if (type.IsGenericType)
						{
							var genericDefType = type.GetGenericTypeDefinition();
							//不是list和dict就再见了
							if (genericDefType != ConstMgr.ListDefType && genericDefType != ConstMgr.DictDefType)
							{
								return false;
							}
						}
						//其他类型也不行
						else if(!type.IsArray && !type.IsEnum)
						{
							return false;
						}
					}
					//list/array/dict/enum/已注册类型才会走这里
					writer.WriteCommonVal(type, val);
					return true;
			}
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
			Type type = typeof(T);
			Writer writer = new Writer(encoding ?? DefaultEncoding);

			//code generated type
			if (TypeModel.TryGetHelper(type, out var helperObj))
			{
				ISerializationHelper<T> helper = (ISerializationHelper<T>)helperObj;
				if (helper != null)
				{
					//start serialize
					helper.NinoWriteMembers(val, writer);
					//compress it
					var ret = writer.ToCompressedBytes();
					writer.Dispose();
					return ret;
				}
			}
			//basic type
			if (AttemptWriteBasicType(val, writer))
			{
				//compress it
				var ret = writer.ToCompressedBytes();
				writer.Dispose();
				return ret;
			}
			//reflection type
			return Serialize(type, val, encoding ?? DefaultEncoding, writer);
		}

		/// <summary>
		/// Serialize a NinoSerialize object
		/// </summary>
		/// <param name="type"></param>
		/// <param name="value"></param>
		/// <param name="encoding"></param>
		/// <param name="writer"></param>
		/// <param name="returnValue"></param>
		/// <returns></returns>
		/// <exception cref="InvalidOperationException"></exception>
		/// <exception cref="NullReferenceException"></exception>
		// ReSharper disable CognitiveComplexity
		internal static byte[] Serialize(Type type, object value, Encoding encoding, Writer writer, bool returnValue = true)
			// ReSharper restore CognitiveComplexity
		{
			//prevent null encoding
			encoding = encoding ?? DefaultEncoding;
			
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
				writer = new Writer(encoding);
			}

			//code generated type
			if (TypeModel.TryGetHelper(type, out var helperObj))
			{
				ISerializationHelper helper = (ISerializationHelper)helperObj;
				if (helper != null)
				{
					//start serialize
					helper.NinoWriteMembers(value, writer);
					var ret = returnValue ? writer.ToCompressedBytes() : ConstMgr.Null;
					if(returnValue)
						writer.Dispose();
					return ret;
				}
			}
			
			//basic type
			if (AttemptWriteBasicType(value, writer))
			{
				var ret = returnValue ? writer.ToCompressedBytes() : ConstMgr.Null;
				if(returnValue)
					writer.Dispose();
				return ret;
			}

			//Get Attribute that indicates a class/struct to be serialized
			TypeModel.TryGetModel(type, out var model);

			//invalid model
			if (model != null && !model.Valid)
			{
				return ConstMgr.Null;
			}

			//generate model
			if (model == null)
			{
				model = TypeModel.CreateModel(type);
			}

			//min, max index
			ushort min = model.Min, max = model.Max;

			void Write()
			{
				//only include all model need this
				if (model.IncludeAll)
				{
					//write len
					writer.CompressAndWrite(model.Members.Count);
				}
				
				for (; min <= max; min++)
				{
					//prevent index not exist
					if (!model.Types.ContainsKey(min)) continue;
					//get type of that member
					type = model.Types[min];
					//try code gen, if no code gen then reflection
					object val = GetVal(model.Members[min], value);
					//string/list/dict can be null, other cannot
					//nullable need to register custom importer
					if (val == null)
					{
						if (type == ConstMgr.StringType)
						{
							writer.Write((byte)CompressType.ByteString);
							writer.Write((byte)0);
							continue;
						}
						//list & dict
						else if (type.IsGenericType &&
							type.GetGenericTypeDefinition() == ConstMgr.ListDefType || type.GetGenericTypeDefinition() == ConstMgr.DictDefType)
						{
							//empty list or dict
							writer.CompressAndWrite(0);
							continue;
						}
						else
						{
							throw new NullReferenceException(
								$"{type.FullName}.{model.Members[min].Name} is null, cannot serialize");
						}
					}

					//only include all model need this
					if (model.IncludeAll)
					{
						var needToStore = model.Members[min];
						writer.Write(needToStore.Name);
						writer.Write(type.FullName);
					}

					writer.WriteCommonVal(type, val);
				}
			}

			Write();
			var buf = returnValue ? writer.ToCompressedBytes() : ConstMgr.Null;
			if(returnValue)
				writer.Dispose();
			return buf;
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