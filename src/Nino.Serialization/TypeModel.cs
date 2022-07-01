using System;
using Nino.Shared.Mgr;
using System.Reflection;
using System.Collections.Generic;
// ReSharper disable CognitiveComplexity

namespace Nino.Serialization
{
	/// <summary>
	/// A model of a serialized type
	/// </summary>
	internal class TypeModel
	{
		private const string HelperName = "NinoSerializationHelper";

		private const BindingFlags ReflectionFlags =
			BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Default;
		
		public Dictionary<ushort, MemberInfo> Members;
		public Dictionary<ushort, Type> Types;
		public ushort Min;
		public ushort Max;
		public bool Valid;
		public bool IncludeAll;

		/// <summary>
		/// Get whether or not a type is basic type and can be serialized/deserialized directly
		/// </summary>
		/// <param name="type"></param>
		/// <returns></returns>
		public static bool IsBasicType(Type type)
		{
			if (!type.IsEnum && !type.IsArray)
			{
				switch (Type.GetTypeCode(type))
				{
					//基础类型肯定可以
					case TypeCode.Int32:
					case TypeCode.UInt32:
					case TypeCode.Int64:
					case TypeCode.UInt64:
					case TypeCode.Byte:
					case TypeCode.SByte:
					case TypeCode.Int16:
					case TypeCode.UInt16:
					case TypeCode.String:
					case TypeCode.Boolean:
					case TypeCode.Double:
					case TypeCode.Single:
					case TypeCode.Decimal:
					case TypeCode.Char:
						return true;
					default:
						//看看有没有注册委托，没的话有概率不行
						if (!Serializer.CustomImporter.ContainsKey(type) &&
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
							else
							{
								return false;
							}
						}
						return true;
				}
			}

			return true;
		}
		
		/// <summary>
		/// Cached Models
		/// </summary>
		private static readonly Dictionary<Type, TypeModel> TypeModels = new Dictionary<Type, TypeModel>(10);
		
		/// <summary>
		/// Generated helpers
		/// </summary>
		private static readonly Dictionary<Type, object> GeneratedSerializationHelper = new Dictionary<Type, object>(10);
		
		/// <summary>
		/// Get whether or not a type is a code gen type
		/// </summary>
		/// <param name="type"></param>
		/// <param name="helper"></param>
		/// <returns></returns>
		internal static bool TryGetHelper(Type type, out object helper)
		{
			if (GeneratedSerializationHelper.TryGetValue(type, out helper)) return helper != null;
			
			var field = type.GetField(HelperName, ReflectionFlags | BindingFlags.Static);
			helper = field?.GetValue(null);
			GeneratedSerializationHelper[type] = helper;
			return GeneratedSerializationHelper[type] != null;
		}
		
		/// <summary>
		/// Try get cached model
		/// </summary>
		/// <param name="type"></param>
		/// <param name="model"></param>
		/// <returns></returns>
		internal static void TryGetModel(Type type, out TypeModel model)
		{
			if (TypeModels.TryGetValue(type, out model)) return;
			object[] ns = type.GetCustomAttributes(typeof(NinoSerializeAttribute), false);
			if (ns.Length != 0) return;
			model = new TypeModel()
			{
				Valid = false
			};
			TypeModels.Add(type, model);
			throw new InvalidOperationException(
				$"The type {type.FullName} does not have NinoSerialize attribute or custom importer/exporter");
		}

		/// <summary>
		/// Create a typeModel using given type
		/// </summary>
		/// <param name="type"></param>
		/// <exception cref="InvalidOperationException"></exception>
		// ReSharper disable CognitiveComplexity
		internal static TypeModel CreateModel(Type type)
			// ReSharper restore CognitiveComplexity
		{
			var model = new TypeModel
			{
				Min = ushort.MaxValue,
				Max = ushort.MinValue,
				Valid = true,
				//fetch members
				Members = new Dictionary<ushort, MemberInfo>(10),
				//fetch types
				Types = new Dictionary<ushort, Type>(10)
			};
			
			//include all or not
			object[] ns = type.GetCustomAttributes(typeof(NinoSerializeAttribute), false);
			model.IncludeAll = ((NinoSerializeAttribute)ns[0]).IncludeAll;

			//store temp attr
			object[] sps;
			//flag
			const BindingFlags flags = BindingFlags.Default | BindingFlags.DeclaredOnly | BindingFlags.Public |
			                           BindingFlags.NonPublic | BindingFlags.Instance;
			ushort index;

			//fetch fields (only public and private fields that declared in the type)
			FieldInfo[] fs = type.GetFields(flags);
			//iterate fields
			foreach (var f in fs)
			{
				if (model.IncludeAll)
				{
					//skip nino ignore
					var ig = f.GetCustomAttributes(typeof(NinoIgnoreAttribute), false);
					if (ig.Length > 0) continue;
					index = (ushort)model.Members.Count;
				}
				else
				{
					sps = f.GetCustomAttributes(typeof(NinoMemberAttribute), false);
					//not fetch all and no attribute => skip this member
					if (sps.Length != 1) continue;
					index = ((NinoMemberAttribute)sps[0]).Index;
				}
				//record field
				model.Members.Add(index, f);
#if ILRuntime
				var t = f.FieldType;
				if (t.IsGenericType)
				{
					model.Types.Add(index, t);
				}
				else
				{
					model.Types.Add(index, t.ResolveRealType());
				}
#else
				model.Types.Add(index, f.FieldType);
#endif
				//record min/max
				if (index < model.Min)
				{
					model.Min = index;
				}

				if (index > model.Max)
				{
					model.Max = index;
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
						$"Cannot read or write property {p.Name} in {type.FullName}, cannot Serialize or Deserialize this property");
				}
				
				if (model.IncludeAll)
				{
					//skip nino ignore
					var ig = p.GetCustomAttributes(typeof(NinoIgnoreAttribute), false);
					if (ig.Length > 0) continue;
					index = (ushort)model.Members.Count;
				}
				else
				{
					sps = p.GetCustomAttributes(typeof(NinoMemberAttribute), false);
					//not fetch all and no attribute => skip this member
					if (sps.Length != 1) continue;
					index = ((NinoMemberAttribute)sps[0]).Index;
				}
				//record property
				model.Members.Add(index, p);
#if ILRuntime
				var t = p.PropertyType;
				if (t.IsArray || t.IsGenericType)
				{
					model.Types.Add(index, t);
				}
				else
				{
					model.Types.Add(index, t.ResolveRealType());
				}
#else
				model.Types.Add(index, p.PropertyType);
#endif
				//record min/max
				if (index < model.Min)
				{
					model.Min = index;
				}

				if (index > model.Max)
				{
					model.Max = index;
				}
			}
			
			if (model.Members.Count == 0)
			{
				model.Valid = false;
			}

			TypeModels.Add(type, model);
			return model;
		}
	}
}

