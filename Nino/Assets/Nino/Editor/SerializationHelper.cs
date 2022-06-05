#if UNITY_2017_1_OR_NEWER
using Nino.Serialization;
using UnityEditor;

namespace Nino.Editor
{
    public static class SerializationHelper
    {
        /// <summary>
        /// Export path, you can change this to export it anywhere under Assets directory
        /// If you want to export outside of Assets, you can use ../
        /// 导出目录，你可以修改这个路径，代码会生成到Assets目录下你指定的这个路径内
        /// 如果需要导出到Assets外部，可以在路径内写入../
        /// </summary>
        private const string ExportPath = "Nino/Generated";
        
        [MenuItem("Nino/Generator/Serialization Code")]
        public static void GenerateSerializationCode()
        {
            // ReSharper disable RedundantArgumentDefaultValue
            CodeGenerator.GenerateSerializeCodeForAllTypePossible(ExportPath);
            // ReSharper restore RedundantArgumentDefaultValue
        }
    }
}
#endif