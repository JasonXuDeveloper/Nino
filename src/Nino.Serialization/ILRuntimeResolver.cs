using System;
using System.Text;
using System.Linq;
using Nino.Shared.Mgr;
using System.Reflection;
using System.Collections.Generic;

// ReSharper disable CognitiveComplexity
// ReSharper disable RedundantNameQualifier
// ReSharper disable RedundantExplicitArrayCreation

namespace Nino.Serialization
{
#if ILRuntime
    /// <summary>
    /// ILRuntime helper
    /// </summary>
    public static class ILRuntimeResolver
    {
        internal static ILRuntime.Runtime.Enviorment.AppDomain appDomain;

        private static readonly Dictionary<string, Type> IlRuntimeTypes = new Dictionary<string, Type>();

        /// <summary>
        /// Get ILType
        /// </summary>
        /// <param name="metaName"></param>
        /// <returns></returns>
		public static Type FindType(string metaName)
		{
			IlRuntimeTypes.TryGetValue(metaName, out Type type);
			return type;
		}

        /// <summary>
        /// Create ILTypeInstance
        /// </summary>
        /// <param name="type"></param>
        /// <returns></returns>
		public static object CreateInstance(Type type)
		{
            if (appDomain != null)
            {
                string typeName = type.FullName;
                if (FindType(typeName) != null)
                {
                    return appDomain.Instantiate(typeName);
                }

                if (typeName != null && appDomain.LoadedTypes.ContainsKey(typeName))
                {
                    IlRuntimeTypes[typeName] = type;
                    return appDomain.Instantiate(typeName);
                }
            }
			return Activator.CreateInstance(type);
		}

        /// <summary>
        /// Reg ILRuntime
        /// </summary>
        /// <param name="domain"></param>
        public static unsafe void RegisterILRuntimeClrRedirection(ILRuntime.Runtime.Enviorment.AppDomain domain)
        {
            appDomain = domain;
            //cache types
            IlRuntimeTypes.Clear();
            var allTypes = domain.LoadedTypes.Values.Select(x => x.ReflectionType).ToArray();
            foreach (var t in allTypes)
            {
                if (t.FullName != null) IlRuntimeTypes[t.FullName] = t;
            }
            
            appDomain.RegisterCrossBindingAdaptor(new SerializationHelper1ILTypeInstanceAdapter());
            //reg redirections
            MethodBase method;
            var type = typeof(Nino.Serialization.Serializer);
            var genericMethods = new Dictionary<string, List<MethodInfo>>();
            List<MethodInfo> lst;
            foreach (var m in type.GetMethods())
            {
                if (m.IsGenericMethodDefinition)
                {
                    if (!genericMethods.TryGetValue(m.Name, out lst))
                    {
                        lst = new List<MethodInfo>();
                        genericMethods[m.Name] = lst;
                    }

                    lst.Add(m);
                }
            }

            var args = new Type[] { typeof(ILRuntime.Runtime.Intepreter.ILTypeInstance) };
            if (genericMethods.TryGetValue("Serialize", out lst))
            {
                foreach (var m in lst)
                {
                    if (ILRuntime.Runtime.Extensions.MatchGenericParameters(m, args, typeof(System.Byte[]),
                        args[0], typeof(System.Text.Encoding)))
                    {
                        method = m.MakeGenericMethod(args);
                        appDomain.RegisterCLRMethodRedirection(method, Serialize_0);

                        break;
                    }
                }
            }

            type = typeof(Nino.Serialization.Deserializer);
            genericMethods.Clear();
            lst?.Clear();
            foreach (var m in type.GetMethods())
            {
                if (m.IsGenericMethodDefinition)
                {
                    if (!genericMethods.TryGetValue(m.Name, out lst))
                    {
                        lst = new List<MethodInfo>();
                        genericMethods[m.Name] = lst;
                    }

                    lst.Add(m);
                }
            }

            //Deserialize<HotUpdateType>
            args = new Type[] { typeof(ILRuntime.Runtime.Intepreter.ILTypeInstance) };
            if (genericMethods.TryGetValue("Deserialize", out lst))
            {
                foreach (var m in lst)
                {
                    if (ILRuntime.Runtime.Extensions.MatchGenericParameters(m, args,
                        args[0],
                        typeof(System.Byte[]), typeof(System.Text.Encoding)))
                    {
                        method = m.MakeGenericMethod(args);
                        appDomain.RegisterCLRMethodRedirection(method, Deserialize_0);

                        break;
                    }
                }
            }
            
            //Deserialize<HotUpdateType[]>
            args = new Type[] { typeof(ILRuntime.Runtime.Intepreter.ILTypeInstance[]) };
            if (genericMethods.TryGetValue("Deserialize", out lst))
            {
                foreach (var m in lst)
                {
                    if (ILRuntime.Runtime.Extensions.MatchGenericParameters(m, args,
                        args[0],
                        typeof(System.Byte[]), typeof(System.Text.Encoding)))
                    {
                        method = m.MakeGenericMethod(args);
                        appDomain.RegisterCLRMethodRedirection(method, Deserialize_0);

                        break;
                    }
                }
            }
            
            //Deserialize<List<HotUpdateType>>
            args = new Type[] { typeof(List<ILRuntime.Runtime.Intepreter.ILTypeInstance>) };
            if (genericMethods.TryGetValue("Deserialize", out lst))
            {
                foreach (var m in lst)
                {
                    if (ILRuntime.Runtime.Extensions.MatchGenericParameters(m, args,
                        args[0],
                        typeof(System.Byte[]), typeof(System.Text.Encoding)))
                    {
                        method = m.MakeGenericMethod(args);
                        appDomain.RegisterCLRMethodRedirection(method, Deserialize_0);

                        break;
                    }
                }
            }
            
            //Deserialize<Dictionary<CLRType, HotUpdateType>>
            foreach (var kvp in IlRuntimeTypes)
            {
                if (kvp.Value is ILRuntime.Reflection.ILRuntimeType) continue;
                var t = ConstMgr.DictDefType.MakeGenericType(new Type[]
                    { kvp.Value, typeof(ILRuntime.Runtime.Intepreter.ILTypeInstance) });
                args = new Type[] { t };
                if (genericMethods.TryGetValue("Deserialize", out lst))
                {
                    foreach (var m in lst)
                    {
                        if (ILRuntime.Runtime.Extensions.MatchGenericParameters(m, args,
                            args[0],
                            typeof(System.Byte[]), typeof(System.Text.Encoding)))
                        {
                            method = m.MakeGenericMethod(args);
                            appDomain.RegisterCLRMethodRedirection(method, Deserialize_0);

                            break;
                        }
                    }
                }   
            }
            
            //Deserialize<Dictionary<HotUpdateType, CLRType>>
            foreach (var kvp in IlRuntimeTypes)
            {
                if (kvp.Value is ILRuntime.Reflection.ILRuntimeType) continue;
                var t = ConstMgr.DictDefType.MakeGenericType(new Type[]
                    { typeof(ILRuntime.Runtime.Intepreter.ILTypeInstance), kvp.Value });
                args = new Type[] { t };
                if (genericMethods.TryGetValue("Deserialize", out lst))
                {
                    foreach (var m in lst)
                    {
                        if (ILRuntime.Runtime.Extensions.MatchGenericParameters(m, args,
                            args[0],
                            typeof(System.Byte[]), typeof(System.Text.Encoding)))
                        {
                            method = m.MakeGenericMethod(args);
                            appDomain.RegisterCLRMethodRedirection(method, Deserialize_0);

                            break;
                        }
                    }
                }   
            }
            
            //Deserialize<Dictionary<HotUpdateType, HotUpdateType>>
            args = new Type[] { typeof(Dictionary<ILRuntime.Runtime.Intepreter.ILTypeInstance,ILRuntime.Runtime.Intepreter.ILTypeInstance>) };
            if (genericMethods.TryGetValue("Deserialize", out lst))
            {
                foreach (var m in lst)
                {
                    if (ILRuntime.Runtime.Extensions.MatchGenericParameters(m, args,
                        args[0],
                        typeof(System.Byte[]), typeof(System.Text.Encoding)))
                    {
                        method = m.MakeGenericMethod(args);
                        appDomain.RegisterCLRMethodRedirection(method, Deserialize_0);

                        break;
                    }
                }
            }  
        }

        /// <summary>
        /// Deserialize reg
        /// </summary>
        /// <param name="intp"></param>
        /// <param name="esp"></param>
        /// <param name="mStack"></param>
        /// <param name="method"></param>
        /// <param name="isNewObj"></param>
        /// <returns></returns>
        private static unsafe ILRuntime.Runtime.Stack.StackObject* Deserialize_0(
            ILRuntime.Runtime.Intepreter.ILIntepreter intp, ILRuntime.Runtime.Stack.StackObject* esp,
            IList<object> mStack,
            ILRuntime.CLR.Method.CLRMethod method, bool isNewObj)
        {
            var domain = intp.AppDomain;
            var ret = ILRuntime.Runtime.Intepreter.ILIntepreter.Minus(esp, 2);

            var ptrOfThisMethod = ILRuntime.Runtime.Intepreter.ILIntepreter.Minus(esp, 1);
            var @encoding = (System.Text.Encoding)ILRuntime.CLR.Utils.Extensions.CheckCLRTypes(
                typeof(System.Text.Encoding),
                ILRuntime.Runtime.Stack.StackObject.ToObject(ptrOfThisMethod, domain, mStack),
                0);
            intp.Free(ptrOfThisMethod);

            ptrOfThisMethod = ILRuntime.Runtime.Intepreter.ILIntepreter.Minus(esp, 2);
            var @data = (System.Byte[])ILRuntime.CLR.Utils.Extensions.CheckCLRTypes(typeof(System.Byte[]),
                ILRuntime.Runtime.Stack.StackObject.ToObject(ptrOfThisMethod, domain, mStack),
                0);
            intp.Free(ptrOfThisMethod);

            //获取泛型参数<T>的实际类型
            var genericArguments = method.GenericArguments;
            var t = genericArguments[0];
            object r;
            if (t is ILRuntime.CLR.TypeSystem.CLRType)
            {
                r = Activator.CreateInstance(t.ReflectionType);
            }
            else
            {
                r = ((ILRuntime.CLR.TypeSystem.ILType)t).Instantiate();
            }

            var resultOfThisMethod =
                Nino.Serialization.Deserializer.Deserialize(t.ReflectionType, r, @data,
                    @encoding ?? Encoding.UTF8, null);

            return ILRuntime.Runtime.Intepreter.ILIntepreter.PushObject(ret, mStack, resultOfThisMethod);
        }

        /// <summary>
        /// Serialize reg
        /// </summary>
        /// <param name="intp"></param>
        /// <param name="esp"></param>
        /// <param name="mStack"></param>
        /// <param name="method"></param>
        /// <param name="isNewObj"></param>
        /// <returns></returns>
        private static unsafe ILRuntime.Runtime.Stack.StackObject* Serialize_0(
            ILRuntime.Runtime.Intepreter.ILIntepreter intp, ILRuntime.Runtime.Stack.StackObject* esp,
            IList<object> mStack,
            ILRuntime.CLR.Method.CLRMethod method, bool isNewObj)
        {
            var domain = intp.AppDomain;
            var ret = ILRuntime.Runtime.Intepreter.ILIntepreter.Minus(esp, 2);

            var ptrOfThisMethod = ILRuntime.Runtime.Intepreter.ILIntepreter.Minus(esp, 1);
            var @encoding = (System.Text.Encoding)ILRuntime.CLR.Utils.Extensions.CheckCLRTypes(
                typeof(System.Text.Encoding),
                ILRuntime.Runtime.Stack.StackObject.ToObject(ptrOfThisMethod, domain, mStack),
                0);
            intp.Free(ptrOfThisMethod);

            //获取泛型参数<T>的实际类型
            var genericArguments = method.GenericArguments;
            var t = genericArguments[0];
            ptrOfThisMethod = ILRuntime.Runtime.Intepreter.ILIntepreter.Minus(esp, 2);
            var val = ILRuntime.CLR.Utils.Extensions.CheckCLRTypes(
                t.ReflectionType,
                ILRuntime.Runtime.Stack.StackObject.ToObject(ptrOfThisMethod, domain, mStack),
                0);
            intp.Free(ptrOfThisMethod);

            var resultOfThisMethod =
                Nino.Serialization.Serializer.Serialize(t.ReflectionType, @val, @encoding ?? Encoding.UTF8, null);

            return ILRuntime.Runtime.Intepreter.ILIntepreter.PushObject(ret, mStack, resultOfThisMethod);
        }

        /// <summary>
        /// Resolve real type
        /// </summary>
        /// <param name="type"></param>
        /// <returns></returns>
        public static Type ResolveRealType(this Type type)
        {
            if (type is ILRuntime.Reflection.ILRuntimeWrapperType wt)
            {
                return wt.RealType;
            }

            return type;
        }
    }
    
    public class SerializationHelper1ILTypeInstanceAdapter : ILRuntime.Runtime.Enviorment.CrossBindingAdaptor
    {
        public override Type BaseCLRType
        {
            get
            {
                return typeof(Nino.Serialization.ISerializationHelper<ILRuntime.Runtime.Intepreter.ILTypeInstance>);
            }
        }

        public override Type AdaptorType
        {
            get
            {
                return typeof(Adapter);
            }
        }

        public override object CreateCLRInstance(ILRuntime.Runtime.Enviorment.AppDomain appdomain, ILRuntime.Runtime.Intepreter.ILTypeInstance instance)
        {
            return new Adapter(appdomain, instance);
        }

        public class Adapter : Nino.Serialization.ISerializationHelper<ILRuntime.Runtime.Intepreter.ILTypeInstance>, ILRuntime.Runtime.Enviorment.CrossBindingAdaptorType
        {
            bool isInvokingToString;
            ILRuntime.Runtime.Intepreter.ILTypeInstance instance;
            ILRuntime.Runtime.Enviorment.AppDomain appdomain;

            private ILRuntime.CLR.Method.IMethod write;
            private ILRuntime.CLR.Method.IMethod read;

            public Adapter()
            {

            }

            public Adapter(ILRuntime.Runtime.Enviorment.AppDomain appdomain, ILRuntime.Runtime.Intepreter.ILTypeInstance instance)
            {
                this.appdomain = appdomain;
                this.instance = instance;
            }

            public ILRuntime.Runtime.Intepreter.ILTypeInstance ILInstance { get { return instance; } }

            public void NinoWriteMembers(ILRuntime.Runtime.Intepreter.ILTypeInstance val, Nino.Serialization.Writer writer)
            {
                if (write == null)
                {
                    string Name = nameof(NinoWriteMembers);
                    var ilType = instance.Type;
                    write = ilType.GetMethod(Name, 2);
                }
                using (var ctx = appdomain.BeginInvoke(write))
                {
                    ctx.PushObject(instance);
                    ctx.PushObject(val);
                    ctx.PushObject(writer);
                    ctx.Invoke();
                }
            }

            public void NinoWriteMembers(object val, Nino.Serialization.Writer writer)
            {
	            NinoWriteMembers((ILRuntime.Runtime.Intepreter.ILTypeInstance)val, writer);
            }

            public ILRuntime.Runtime.Intepreter.ILTypeInstance NinoReadMembers(Nino.Serialization.Reader reader)
            {
                if (read == null)
                {
                    string Name = nameof(NinoReadMembers);
                    var ilType = instance.Type;
                    read = ilType.GetMethod(Name, 1);
                }
                using (var ctx = appdomain.BeginInvoke(read))
                {
                    ctx.PushObject(instance);
                    ctx.PushObject(reader);
                    ctx.Invoke();
                    return ctx.ReadObject<ILRuntime.Runtime.Intepreter.ILTypeInstance>();
                }
            }

            object Nino.Serialization.ISerializationHelper.NinoReadMembers(Nino.Serialization.Reader reader)
            {
	            return NinoReadMembers(reader);
            }

            public override string ToString()
            {
                ILRuntime.CLR.Method.IMethod m = appdomain.ObjectType.GetMethod("ToString", 0);
                m = instance.Type.GetVirtualMethod(m);
                if (m == null || m is ILRuntime.CLR.Method.ILMethod)
                {
                    if (!isInvokingToString)
                    {
                        isInvokingToString = true;
                        string res = instance.ToString();
                        isInvokingToString = false;
                        return res;
                    }
                    else
                        return instance.Type.FullName;
                }
                else
                    return instance.Type.FullName;
            }
        }
    }
#endif
}