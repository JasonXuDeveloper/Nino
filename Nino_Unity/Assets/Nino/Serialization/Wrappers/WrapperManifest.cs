using System;
using System.Collections.Generic;

namespace Nino.Serialization
{
    internal static class WrapperManifest
    {
        public static readonly Dictionary<Type, INinoWrapper> Wrappers = new Dictionary<Type, INinoWrapper>()
        {
            { typeof(byte), new ByteWrapper() },
            { typeof(byte[]), new ByteArrWrapper() },
            { typeof(List<byte>), new ByteListWrapper() },
            { typeof(sbyte), new SByteWrapper() },
            { typeof(sbyte[]), new SByteArrWrapper() },
            { typeof(List<sbyte>), new SByteListWrapper() },
            { typeof(short), new ShortWrapper() },
            { typeof(short[]), new ShortArrWrapper() },
            { typeof(List<short>), new ShortListWrapper() },
            { typeof(ushort), new UShortWrapper() },
            { typeof(ushort[]), new UShortArrWrapper() },
            { typeof(List<ushort>), new UShortListWrapper() },
            { typeof(int), new IntWrapper() },
            { typeof(int[]), new IntArrWrapper() },
            { typeof(List<int>), new IntListWrapper() },
            { typeof(uint), new UIntWrapper() },
            { typeof(uint[]), new UIntArrWrapper() },
            { typeof(List<uint>), new UIntListWrapper() },
            { typeof(long), new LongWrapper() },
            { typeof(long[]), new LongArrWrapper() },
            { typeof(List<long>), new LongListWrapper() },
            { typeof(ulong), new ULongWrapper() },
            { typeof(ulong[]), new ULongArrWrapper() },
            { typeof(List<ulong>), new ULongListWrapper() },
            { typeof(float), new FloatWrapper() },
            { typeof(float[]), new FloatArrWrapper() },
            { typeof(List<float>), new FloatListWrapper() },
            { typeof(double), new DoubleWrapper() },
            { typeof(double[]), new DoubleArrWrapper() },
            { typeof(List<double>), new DoubleListWrapper() },
            { typeof(decimal), new DecimalWrapper() },
            { typeof(decimal[]), new DecimalArrWrapper() },
            { typeof(List<decimal>), new DecimalListWrapper() },
            { typeof(string), new StringWrapper() },
            { typeof(string[]), new StringArrWrapper() },
            { typeof(List<string>), new StringListWrapper() },
            { typeof(char), new CharWrapper() },
            { typeof(char[]), new CharArrWrapper() },
            { typeof(List<char>), new CharListWrapper() },
            { typeof(bool), new BoolWrapper() },
            { typeof(bool[]), new BoolArrWrapper() },
            { typeof(List<bool>), new BoolListWrapper() },
            { typeof(DateTime), new DateTimeWrapper() },
            { typeof(DateTime[]), new DateTimeArrWrapper() },
            { typeof(List<DateTime>), new DateTimeListWrapper() },
        };
    }
}