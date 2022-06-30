using System;
using System.Text;
using Nino.Shared.IO;
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
		private static void WriteBasicType<T>(T val, Writer writer)
		{
			switch (val)
			{
				//without sign
				case ulong ul:
					writer.CompressAndWrite(ul);
					return;
				case uint ui:
					writer.CompressAndWrite(ui);
					return;
				case ushort us: //unnecessary to compress
					writer.Write(us);
					return;
				case byte b: //unnecessary to compress
					writer.Write(b);
					return;
				// with sign
				case long l:
					writer.CompressAndWrite(l);
					return;
				case int i:
					writer.CompressAndWrite(i);
					return;
				case short s: //unnecessary to compress
					writer.Write(s);
					return;
				case sbyte sb: //unnecessary to compress
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
				case string s:
					writer.Write(s);
					return;
				default:
					var type = typeof(T);
					if (type == ConstMgr.ObjectType)
					{
						//unbox
						type = val.GetType();
					}
					writer.WriteCommonVal(type, val);
					return;
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
			Type t = typeof(T);
			//basic type
			if (TypeModel.IsBasicType(t))
			{
				//start serialize
				using (var writer = new Writer(encoding ?? DefaultEncoding))
				{
					WriteBasicType(val, writer);
					//compress it
					return writer.ToCompressedBytes();
				}
			}
			//code generated type
			if (TypeModel.TryGetHelper(t, out var helperObj))
			{
				ISerializationHelper<T> helper = (ISerializationHelper<T>)helperObj;
				if (helper != null)
				{
					//record
					TypeModel.AddSerializeAction(t, (obj, writer) =>
					{
						helper.NinoWriteMembers((T)obj, writer);
					});
					//start serialize
					using (var writer = new Writer(encoding ?? DefaultEncoding))
					{
						helper.NinoWriteMembers(val, writer);
						//compress it
						return writer.ToCompressedBytes();
					}
				}
			}
			//reflection type
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
		// ReSharper disable CognitiveComplexity
		internal static byte[] Serialize(Type type, object value, Encoding encoding, Writer writer = null)
			// ReSharper restore CognitiveComplexity
		{
			//prevent null encoding
			encoding = encoding == null ? DefaultEncoding : encoding;
			
			//ILRuntime
#if ILRuntime
			if(value is ILRuntime.Runtime.Intepreter.ILTypeInstance ins)
			{
				type = ins.Type.ReflectionType;
			}

			type = type.ResolveRealType();
#endif
			
			//basic type
			if (TypeModel.IsBasicType(type))
			{
				//share a writer
				if (writer != null)
				{
					WriteBasicType(value, writer);
					return ConstMgr.Null;
				}

				//start serialize
				using (writer = new Writer(encoding))
				{
					WriteBasicType(value, writer);
					//compress it
					return writer.ToCompressedBytes();
				}
			}

			//try code gen
			if (TypeModel.TryGetSerializeAction(type, out var action))
			{
				//share a writer
				if (writer != null)
				{
					action(value, writer);
					return ConstMgr.Null;
				}

				//start serialize
				using (writer = new Writer(encoding))
				{
					action(value, writer);
					//compress it
					return writer.ToCompressedBytes();
				}
			}
			
			//another attempt
			if (TypeModel.TryGetHelperMethodInfo(type, out var helper, out var sm, out _))
			{
				//reflect generic method
				//share a writer
				if (writer != null)
				{
#if ILRuntime
					if (value is ILRuntime.Runtime.Intepreter.ILTypeInstance instance)
					{
						((SerializationHelper1ILTypeInstanceAdapter.Adapter)helper).NinoWriteMembers(
							instance, writer);
						return ConstMgr.Null;
					}
#endif
					var objs = ArrayPool<object>.Request(2);
					objs[0] = value;
					objs[1] = writer;
					sm.Invoke(helper, objs);
					ArrayPool<object>.Return(objs);
					return ConstMgr.Null;
				}

				//start serialize
				using (writer = new Writer(encoding))
				{
#if ILRuntime
					if (value is ILRuntime.Runtime.Intepreter.ILTypeInstance instance)
					{
						((SerializationHelper1ILTypeInstanceAdapter.Adapter)helper).NinoWriteMembers(
							instance, writer);
						return ConstMgr.Null;
					}
#endif
					var objs = ArrayPool<object>.Request(2);
					objs[0] = value;
					objs[1] = writer;
					sm.Invoke(helper, objs);
					ArrayPool<object>.Return(objs);
					//compress it
					return writer.ToCompressedBytes();
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

			//min, max index
			ushort min = model.min, max = model.max;

			void Write()
			{
				//only include all model need this
				if (model.includeAll)
				{
					//write len
					writer.CompressAndWrite(model.members.Count);
				}
				
				for (; min <= max; min++)
				{
					//prevent index not exist
					if (!model.types.ContainsKey(min)) continue;
					//get type of that member
					type = model.types[min];
					//try code gen, if no code gen then reflection
					object val = GetVal(model.members[min], value);
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
								$"{type.FullName}.{model.members[min].Name} is null, cannot serialize");
						}
					}

					//only include all model need this
					if (model.includeAll)
					{
						var needToStore = model.members[min];
						writer.Write(needToStore.Name);
						writer.Write(type.FullName);
					}

					writer.WriteCommonVal(type, val);
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
				return writer.ToCompressedBytes();
			}
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