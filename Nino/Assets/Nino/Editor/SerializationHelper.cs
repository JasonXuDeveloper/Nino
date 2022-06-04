#if UNITY_2017_1_OR_NEWER
using Nino.Serialization;
using UnityEditor;

namespace Nino.Editor
{
    public static class SerializationHelper
    {
        [MenuItem("Nino/Generator/Serialization Code")]
        public static void GenerateSerializationCode()
        {
            CodeGenerator.GenerateSerializeCodeForAllTypePossible();
        }
    }
}
#endif