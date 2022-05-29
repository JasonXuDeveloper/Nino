using System;
namespace Nino.Serialization.Enum
{
    /// <summary>
    /// Compress type when serializing and deserializing
    /// 序列化或反序列化时的压缩类型
    /// </summary>
    public enum CompressType : byte
    {
        /// <summary>
        /// A string has a length of 0 to 255 (byte) 8 bit
        /// 一个只有0到255长度的字符串
        /// </summary>
        ByteString = 0,
        /// <summary>
        /// A string has a length of 256 to 65535 (byte to word) 16 bit
        /// 一个只有256到65535长度的字符串
        /// </summary>
        UInt16String = 1,
        /// <summary>
        /// A string has a length of 65536 to 2,147,483,647 (word to integer) 32 bit
        /// 一个只有65536到2,147,483,647长度的字符串
        /// </summary>
        Int32String = 2,
        /// <summary>
        /// A number has a length of 0 to 255 (byte) 8 bit
        /// 一个在0到255之间的数字
        /// </summary>
        Byte = 3,
        /// <summary>
        /// A number has a length of -128 to 127 (sbyte) 8 bit
        /// 一个在-128到127之间的数字
        /// </summary>
        SByte = 4,
        /// <summary>
        /// A number has a length of -32,768 to 32,767 (short) 16 bit
        /// 一个在-32,768到32,767之间的数字
        /// </summary>
        Int16 = 5,
        /// <summary>
        /// A number has a length of 0 to 65,535 (ushort) 16 bit
        /// 一个在0到65536之间的数字
        /// </summary>
        UInt16 = 6,
        /// <summary>
        /// A number has a length of -2,147,483,648 to 2,147,483,647 (int) 32 bit
        /// 一个在-2,147,483,648到2,147,483,647之间的数字
        /// </summary>
        Int32 = 7,
        /// <summary>
        /// A number has a length of 0 to 4,294,967,295 (uint) 32 bit
        /// 一个在0到4,294,967,295之间的数字
        /// </summary>
        UInt32 = 8,
        /// <summary>
        /// A number has a length of -9,223,372,036,854,775,808 to 9,223,372,036,854,775,807 (long) 64 bit
        /// 一个在-9,223,372,036,854,775,808到9,223,372,036,854,775,807之间的数字
        /// </summary>
        Int64 = 9,
        /// <summary>
        /// A number has a length of 0 to 18,446,744,073,709,551,615 (ulong) 64 bit
        /// 一个在0到18,446,744,073,709,551,615之间的数字
        /// </summary>
        UInt64 = 10,
    }
}

