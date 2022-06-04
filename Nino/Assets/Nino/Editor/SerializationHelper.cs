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