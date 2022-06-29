using Nino.Shared.Util;
using Nino.Serialization;
using System.Diagnostics;

// ReSharper disable RedundantTypeArgumentsOfMethod
namespace Nino.Test.Editor.Serialization
{
    public class Test8
    {
        private const string SerializationTest8 = "Nino/Test/Serialization/Test8 - Include All Member Class";

#if UNITY_2017_1_OR_NEWER
        [UnityEditor.MenuItem(SerializationTest8,priority=8)]
#endif
        public static void Main()
        {
            Stopwatch sw = new Stopwatch();
            
            IncludeAllClass c = new IncludeAllClass()
            {
                a = 100,
                b = 199,
                c = 5.5f,
                d = 1.23456
            };
            Logger.D(
                "serialize an 'include all' class will make serialization " +
                "and deserialization result larger and slower, it is recommended to use NinoMember Attributes");
            Logger.D($"will serialize c: {c}");
            sw.Reset();
            sw.Start();
            var bs = Serializer.Serialize(c);
            sw.Stop();
            Logger.D(
                $"serialized to {bs.Length} bytes in {((float)sw.ElapsedTicks / Stopwatch.Frequency) * 1000} ms: {string.Join(",", bs)}");

            Logger.D("will deserialize");
            sw.Reset();
            sw.Start();
            var cc = Deserializer.Deserialize<IncludeAllClass>(bs);
            sw.Stop();
            Logger.D($"deserialized as cc in {((float)sw.ElapsedTicks / Stopwatch.Frequency) * 1000} ms: {cc}");
            
            IncludeAllClassCodeGen codeGen = new IncludeAllClassCodeGen()
            {
                a = 100,
                b = 199,
                c = 5.5f,
                d = 1.23456
            };
            Logger.D(
                "serialize an 'include all' class with code gen, this will not make the serialization result larger or slower, if and only if code gen occurs for an include all class");
            Logger.D($"will serialize codeGen: {codeGen}");
            sw.Reset();
            sw.Start();
            bs = Serializer.Serialize(codeGen);
            sw.Stop();
            Logger.D($"serialized to {bs.Length} bytes in {((float)sw.ElapsedTicks / Stopwatch.Frequency) * 1000} ms: {string.Join(",", bs)}");

            Logger.D("will deserialize");
            sw.Reset();
            sw.Start();
            var codeGenR = Deserializer.Deserialize<IncludeAllClassCodeGen>(bs);
            sw.Stop();
            Logger.D($"deserialized as codeGenR in {((float)sw.ElapsedTicks / Stopwatch.Frequency) * 1000} ms: {codeGenR}");
            
            NotIncludeAllClass d = new NotIncludeAllClass()
            {
                a = 100,
                b = 199,
                c = 5.5f,
                d = 1.23456
            };
            Logger.D(
                "Now in comparison, we serialize a class with the same structure and same value");
            Logger.D($"will serialize d in {((float)sw.ElapsedTicks / Stopwatch.Frequency) * 1000} ms: {d}");
            sw.Reset();
            sw.Start();
            bs = Serializer.Serialize(d);
            sw.Stop();
            Logger.D($"serialized to {bs.Length} bytes: {string.Join(",", bs)}");

            Logger.D("will deserialize");
            sw.Reset();
            sw.Start();
            var dd = Deserializer.Deserialize<NotIncludeAllClass>(bs);
            sw.Stop();
            Logger.D($"deserialized as dd in {((float)sw.ElapsedTicks / Stopwatch.Frequency) * 1000} ms: {dd}");
        }
    }
}
// ReSharper restore RedundantTypeArgumentsOfMethod