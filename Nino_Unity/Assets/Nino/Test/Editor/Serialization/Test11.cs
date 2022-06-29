#if ILRuntime
using System.IO;
using Nino.Shared.Util;
using Nino.Serialization;
using ILRuntime.Runtime.Enviorment;

// ReSharper disable RedundantTypeArgumentsOfMethod
namespace Nino.Test.Editor.Serialization
{
    public class Test11
    {
        private const string SerializationTest11 = "Nino/Test/Serialization/Test11 - ILRuntime";

#if UNITY_2017_1_OR_NEWER
        [UnityEditor.MenuItem(SerializationTest11, priority=11)]
#endif
        public static void Main()
        {
            var buf = File.ReadAllBytes("Assets/Nino/Test/Editor/Serialization/Test11.bytes");
            AppDomain domain = new AppDomain();
            domain.LoadAssembly(new MemoryStream(buf));
            ILRuntimeResolver.RegisterILRuntimeClrRedirection(domain);
            var ret = (byte[])domain.Invoke("Test.Test11", "TestSerialize", null);
            Logger.D($"Serialized as {ret.Length} bytes, {string.Join(",",ret)}");
            var dd = (string)domain.Invoke("Test.Test11", "TestDeserialize", null, ret);
            Logger.D($"Deserialized as: {dd}");
        }
    }
}
// ReSharper restore RedundantTypeArgumentsOfMethod
#endif