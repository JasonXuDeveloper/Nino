using System;
using System.Collections.Generic;
using System.Reflection;

namespace Nino.Serialization
{
	/// <summary>
	/// A model of a serialized type
	/// </summary>
	public class TypeModel
	{
		public Dictionary<ushort, MemberInfo> members;
		public Dictionary<ushort, Type> types;
		public ushort min;
		public ushort max;
		public bool valid;
		public MethodInfo ninoGetMembers;
	}
}

