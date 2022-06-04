using System;
#if UNITY_2017_1_OR_NEWER
using UnityEngine;
#endif

namespace Nino.Shared
{
    public static class ConstMgr
    {
#if UNITY_2017_1_OR_NEWER
        /// <summary>
        /// Asset path
        /// </summary>
        public static string AssetPath => Application.dataPath;
#else
        /// <summary>
        /// Asset path
        /// </summary>
        public static string AssetPath => System.IO.Directory.GetCurrentDirectory();
#endif

        /// <summary>
        /// Null value
        /// </summary>
        public static readonly byte[] Null = Array.Empty<byte>();

        /// <summary>
        /// Empty param
        /// </summary>
        public static readonly object[] EmptyParam = Array.Empty<object>();

        #region basic types

        public static readonly Type ByteType = typeof(byte);
        public static readonly Type SByteType = typeof(sbyte);
        public static readonly Type ShortType = typeof(short);
        public static readonly Type UShortType = typeof(ushort);
        public static readonly Type IntType = typeof(int);
        public static readonly Type UIntType = typeof(uint);
        public static readonly Type LongType = typeof(long);
        public static readonly Type ULongType = typeof(ulong);
        public static readonly Type StringType = typeof(string);

        #endregion

        public static readonly byte SizeOfUInt = sizeof(uint);
        public static readonly byte SizeOfInt = sizeof(int);
        public static readonly byte SizeOfUShort = sizeof(ushort);
        public static readonly byte SizeOfShort = sizeof(short);
        public static readonly byte SizeOfULong = sizeof(ulong);
        public static readonly byte SizeOfLong = sizeof(long);
    }
}