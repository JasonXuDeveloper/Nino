using System;
using System.Reflection;
using System.Collections.Generic;

namespace Nino.Serialization
{
	/// <summary>
	/// A model of a serialized type
	/// </summary>
	internal class TypeModel
	{
		public Dictionary<ushort, MemberInfo> members;
		public Dictionary<ushort, Type> types;
		public ushort min;
		public ushort max;
		public bool valid;
		public MethodInfo ninoGetMembers;
		public MethodInfo ninoSetMembers;
		public bool includeAll;

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
		public static void TryGetModel(Type type, out TypeModel model)
		{
			if (TypeModels.TryGetValue(type, out model)) return;
			NinoSerializeAttribute[] ns =
				(NinoSerializeAttribute[])type.GetCustomAttributes(typeof(NinoSerializeAttribute), false);
			if (ns.Length != 0) return;
			model = new TypeModel()
			{
				valid = false
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
		public static TypeModel CreateModel(Type type)
		{
			var model = new TypeModel
			{
				min = ushort.MaxValue,
				max = ushort.MinValue,
				valid = true,
				//fetch members
				members = new Dictionary<ushort, MemberInfo>(),
				//fetch types
				types = new Dictionary<ushort, Type>()
			};
			
			//include all or not
			NinoSerializeAttribute[] ns =
				(NinoSerializeAttribute[])type.GetCustomAttributes(typeof(NinoSerializeAttribute), false);
			model.includeAll = ns[0].IncludeAll;

			//store temp attr
			NinoMemberAttribute sp;
			//flag
			const BindingFlags flags = BindingFlags.Default | BindingFlags.DeclaredOnly | BindingFlags.Public |
			                           BindingFlags.NonPublic | BindingFlags.Instance;
			ushort index;

			//fetch fields (only public and private fields that declared in the type)
			FieldInfo[] fs = type.GetFields(flags);
			//iterate fields
			foreach (var f in fs)
			{
				if (model.includeAll)
				{
					index = (ushort)model.members.Count;
				}
				else
				{
					sp = f.GetCustomAttribute(typeof(NinoMemberAttribute), false) as NinoMemberAttribute;
					//not fetch all and no attribute => skip this member
					if (sp == null) continue;
					index = sp.Index;
				}
				//record field
				model.members.Add(index, f);
				model.types.Add(index, f.FieldType);
				//record min/max
				if (index < model.min)
				{
					model.min = index;
				}

				if (index > model.max)
				{
					model.max = index;
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
						$"Cannot read or write property {p.Name} in {type.FullName}, cannot Deserialize this property");
				}
				
				if (model.includeAll)
				{
					index = (ushort)model.members.Count;
				}
				else
				{
					sp = p.GetCustomAttribute(typeof(NinoMemberAttribute), false) as NinoMemberAttribute;
					//not fetch all and no attribute => skip this member
					if (sp == null) continue;
					index = sp.Index;
				}
				//record property
				model.members.Add(index, p);
				model.types.Add(index, p.PropertyType);
				//record min/max
				if (index < model.min)
				{
					model.min = index;
				}

				if (index > model.max)
				{
					model.max = index;
				}
			}
			
			if (model.members.Count == 0)
			{
				model.valid = false;
			}
			else
			{
				//try code gen
				model.ninoGetMembers = type.GetMethod("NinoGetMembers", flags);
				model.ninoSetMembers = type.GetMethod("NinoSetMembers", flags);
			}

			TypeModels.Add(type, model);
			return model;
		}
	}
}

