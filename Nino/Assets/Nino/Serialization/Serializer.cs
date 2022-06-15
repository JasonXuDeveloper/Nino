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
		internal static Dictionary<Type, ImporterDelegate> CustomImporter =
			new Dictionary<Type, ImporterDelegate>();

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
		/// Serialize a NinoSerialize object
		/// </summary>
		/// <typeparam name="T"></typeparam>
		/// <param name="val"></param>
		/// <param name="encoding"></param>
		/// <param name="writer"></param>
		/// <returns></returns>
		public static byte[] Serialize<T>(T val, Encoding encoding = null, Writer writer = null)
		{
			return Serialize(typeof(T), val, encoding ?? DefaultEncoding, writer);
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
		public static byte[] Serialize(Type type, object value, Encoding encoding, Writer writer = null)
		{
			//Get Attribute that indicates a class/struct to be serialized
			TypeModel.TryGetModel(type, out var model);

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

			//min, max index
			ushort min = model.min, max = model.max;

			void Write()
			{
				if (model.NinoWriteMembers != null)
				{
					var p = ExtensibleObjectPool.RequestObjArr(1);
					p[0] = writer;
					model.NinoWriteMembers.Invoke(value, p);
					ExtensibleObjectPool.ReturnObjArr(p);
					return;
				}
				
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
					if (val == null && type.GetGenericTypeDefinition() != ConstMgr.NullableDefType)
					{
						throw new NullReferenceException(
							$"{type.FullName}.{model.members[min].Name} is null, cannot serialize");
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