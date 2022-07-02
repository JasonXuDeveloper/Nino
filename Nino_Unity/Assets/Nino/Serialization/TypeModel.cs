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
		/// Cached Models
		/// </summary>
		private static readonly Dictionary<Type, TypeModel> TypeModels = new Dictionary<Type, TypeModel>(10);

		/// <summary>
		/// Cached Models
		/// </summary>
		private static readonly Dictionary<Type, TypeCode> TypeCodes = new Dictionary<Type, TypeCode>(30)
		{
			{ typeof(byte), TypeCode.Byte },
			{ typeof(sbyte), TypeCode.SByte },
			{ typeof(short), TypeCode.Int16 },
			{ typeof(ushort), TypeCode.UInt16 },
			{ typeof(int), TypeCode.Int32 },
			{ typeof(uint), TypeCode.UInt32 },
			{ typeof(long), TypeCode.Int64 },
			{ typeof(ulong), TypeCode.UInt64 },
			{ typeof(float), TypeCode.Single },
			{ typeof(double), TypeCode.Double },
			{ typeof(decimal), TypeCode.Decimal },
			{ typeof(char), TypeCode.Char },
			{ typeof(bool), TypeCode.Boolean },
			{ typeof(string), TypeCode.String },
			{ typeof(object), TypeCode.Object },
			{ typeof(DateTime), TypeCode.DateTime },
		};

		/// <summary>
		/// Generated helpers
		/// </summary>
		private static readonly Dictionary<Type, object> GeneratedSerializationHelper = new Dictionary<Type, object>(50)
		{
			{ typeof(byte), null },
			{ typeof(sbyte), null },
			{ typeof(short), null },
			{ typeof(ushort), null },
			{ typeof(int), null },
			{ typeof(uint), null },
			{ typeof(long), null },
			{ typeof(ulong), null },
			{ typeof(bool), null },
			{ typeof(float), null },
			{ typeof(double), null },
			{ typeof(decimal), null },
			{ typeof(char), null },
			{ typeof(string), null },
			{ typeof(DateTime), null },
		};

		/// <summary>
		/// No compression typecodes
		/// </summary>
		internal static readonly HashSet<TypeCode> NoCompressionTypeCodes = new HashSet<TypeCode>()
		{
			TypeCode.Int32,
			TypeCode.UInt32,
			TypeCode.Int64,
			TypeCode.UInt64,
			TypeCode.Byte,
			TypeCode.SByte,
			TypeCode.Int16,
			TypeCode.UInt16,
			TypeCode.Boolean,
			TypeCode.Char,
			TypeCode.Decimal,
			TypeCode.Double,
			TypeCode.Single,
			TypeCode.DateTime,
		};

		/// <summary>
		/// No compression types
		/// </summary>
		internal static readonly HashSet<Type> NoCompressionTypes = new HashSet<Type>()
		{
			typeof(int),
			typeof(uint),
			typeof(long),
			typeof(ulong),
			typeof(byte),
			typeof(sbyte),
			typeof(short),
			typeof(ushort),
			typeof(bool),
			typeof(char),
			typeof(decimal),
			typeof(double),
			typeof(float),
			typeof(DateTime),
		};

		/// <summary>
		/// Get a type code
		/// </summary>
		/// <param name="type"></param>
		/// <returns></returns>
		internal static TypeCode GetTypeCode(Type type)
		{
			if (TypeCodes.TryGetValue(type, out var ret))
			{
				return ret;
			}
			TypeCodes[type] = ret = Type.GetTypeCode(type);
			return ret;
		}
		
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

