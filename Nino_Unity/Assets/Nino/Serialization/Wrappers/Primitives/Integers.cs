using System;
using Nino.Shared.IO;
using System.Collections.Generic;

namespace Nino.Serialization
{
    internal class ByteWrapper : NinoWrapperBase<byte>
    {
        public override void Serialize(byte val, Writer writer)
        {
            writer.Write(val);
        }

        public override Box<byte> Deserialize(Reader reader)
        {
            var ret = ObjectPool<Box<byte>>.Request();
            ret.Value = reader.ReadByte();
            return ret;
        }
    }

    internal class ByteArrWrapper : NinoWrapperBase<byte[]>
    {
        public override void Serialize(byte[] val, Writer writer)
        {
            writer.Write(val);
        }

        public override Box<byte[]> Deserialize(Reader reader)
        {
            var ret = ObjectPool<Box<byte[]>>.Request();
            int len = reader.ReadLength();
            ret.Value = len != 0 ? reader.ReadBytes(len) : Array.Empty<byte>();
            return ret;
        }
    }

    internal class ByteListWrapper : NinoWrapperBase<List<byte>>
    {
        public override void Serialize(List<byte> val, Writer writer)
        {
            writer.Write(val);
        }

        public override Box<List<byte>> Deserialize(Reader reader)
        {
            var ret = ObjectPool<Box<List<byte>>>.Request();
            int len = reader.ReadLength();
            var arr = new List<byte>(len);
            //read item
            for (int i = 0; i < len; i++)
            {
                arr.Add(reader.ReadByte());
            }
            ret.Value = arr;
            return ret;
        }
    }

    internal class SByteWrapper : NinoWrapperBase<sbyte>
    {
        public override void Serialize(sbyte val, Writer writer)
        {
            writer.Write(val);
        }

        public override Box<sbyte> Deserialize(Reader reader)
        {
            var ret = ObjectPool<Box<sbyte>>.Request();
            ret.Value = reader.ReadSByte();
            return ret;
        }
    }

    internal class SByteArrWrapper : NinoWrapperBase<sbyte[]>
    {
        public override void Serialize(sbyte[] val, Writer writer)
        {
            writer.CompressAndWrite(val.Length);
            foreach (var v in val)
            {
                writer.Write(v);
            }
        }

        public override Box<sbyte[]> Deserialize(Reader reader)
        {
            var ret = ObjectPool<Box<sbyte[]>>.Request();
            int len = reader.ReadLength();
            var arr = new sbyte[len];
            //read item
            for (int i = 0; i < len; i++)
            {
                arr[i] = reader.ReadSByte();
            }
            ret.Value = arr;
            return ret;
        }
    }

    internal class SByteListWrapper : NinoWrapperBase<List<sbyte>>
    {
        public override void Serialize(List<sbyte> val, Writer writer)
        {
            writer.CompressAndWrite(val.Count);
            foreach (var v in val)
            {
                writer.Write(v);
            }
        }

        public override Box<List<sbyte>> Deserialize(Reader reader)
        {
            var ret = ObjectPool<Box<List<sbyte>>>.Request();
            int len = reader.ReadLength();
            var arr = new List<sbyte>(len);
            //read item
            for (int i = 0; i < len; i++)
            {
                arr.Add(reader.ReadSByte());
            }
            ret.Value = arr;
            return ret;
        }
    }

    internal class ShortWrapper : NinoWrapperBase<short>
    {
        public override void Serialize(short val, Writer writer)
        {
            writer.Write(val);
        }

        public override Box<short> Deserialize(Reader reader)
        {
            var ret = ObjectPool<Box<short>>.Request();
            ret.Value = reader.ReadInt16();
            return ret;
        }
    }

    internal class ShortArrWrapper : NinoWrapperBase<short[]>
    {
        public override void Serialize(short[] val, Writer writer)
        {
            writer.CompressAndWrite(val.Length);
            foreach (var v in val)
            {
                writer.Write(v);
            }
        }

        public override Box<short[]> Deserialize(Reader reader)
        {
            var ret = ObjectPool<Box<short[]>>.Request();
            int len = reader.ReadLength();
            var arr = new short[len];
            //read item
            for (int i = 0; i < len; i++)
            {
                arr[i] = reader.ReadInt16();
            }
            ret.Value = arr;
            return ret;
        }
    }

    internal class ShortListWrapper : NinoWrapperBase<List<short>>
    {
        public override void Serialize(List<short> val, Writer writer)
        {
            writer.CompressAndWrite(val.Count);
            foreach (var v in val)
            {
                writer.Write(v);
            }
        }

        public override Box<List<short>> Deserialize(Reader reader)
        {
            var ret = ObjectPool<Box<List<short>>>.Request();
            int len = reader.ReadLength();
            var arr = new List<short>(len);
            //read item
            for (int i = 0; i < len; i++)
            {
                arr.Add(reader.ReadInt16());
            }
            ret.Value = arr;
            return ret;
        }
    }

    internal class UShortWrapper : NinoWrapperBase<ushort>
    {
        public override void Serialize(ushort val, Writer writer)
        {
            writer.Write(val);
        }

        public override Box<ushort> Deserialize(Reader reader)
        {
            var ret = ObjectPool<Box<ushort>>.Request();
            ret.Value = reader.ReadUInt16();
            return ret;
        }
    }

    internal class UShortArrWrapper : NinoWrapperBase<ushort[]>
    {
        public override void Serialize(ushort[] val, Writer writer)
        {
            writer.CompressAndWrite(val.Length);
            foreach (var v in val)
            {
                writer.Write(v);
            }
        }

        public override Box<ushort[]> Deserialize(Reader reader)
        {
            var ret = ObjectPool<Box<ushort[]>>.Request();
            int len = reader.ReadLength();
            var arr = new ushort[len];
            //read item
            for (int i = 0; i < len; i++)
            {
                arr[i] = reader.ReadUInt16();
            }
            ret.Value = arr;
            return ret;
        }
    }

    internal class UShortListWrapper : NinoWrapperBase<List<ushort>>
    {
        public override void Serialize(List<ushort> val, Writer writer)
        {
            writer.CompressAndWrite(val.Count);
            foreach (var v in val)
            {
                writer.Write(v);
            }
        }

        public override Box<List<ushort>> Deserialize(Reader reader)
        {
            var ret = ObjectPool<Box<List<ushort>>>.Request();
            int len = reader.ReadLength();
            var arr = new List<ushort>(len);
            //read item
            for (int i = 0; i < len; i++)
            {
                arr.Add(reader.ReadUInt16());
            }
            ret.Value = arr;
            return ret;
        }
    }

    internal class IntWrapper : NinoWrapperBase<int>
    {
        public override void Serialize(int val, Writer writer)
        {
            writer.CompressAndWrite(val);
        }

        public override Box<int> Deserialize(Reader reader)
        {
            var ret = ObjectPool<Box<int>>.Request();
            ret.Value = (int)reader.DecompressAndReadNumber();
            return ret;
        }
    }

    internal class IntArrWrapper : NinoWrapperBase<int[]>
    {
        public override void Serialize(int[] val, Writer writer)
        {
            writer.CompressAndWrite(val.Length);
            foreach (var v in val)
            {
                writer.CompressAndWrite(v);
            }
        }

        public override Box<int[]> Deserialize(Reader reader)
        {
            var ret = ObjectPool<Box<int[]>>.Request();
            int len = reader.ReadLength();
            var arr = new int[len];
            //read item
            for (int i = 0; i < len; i++)
            {
                arr[i] = (int)reader.DecompressAndReadNumber();
            }
            ret.Value = arr;
            return ret;
        }
    }

    internal class IntListWrapper : NinoWrapperBase<List<int>>
    {
        public override void Serialize(List<int> val, Writer writer)
        {
            writer.CompressAndWrite(val.Count);
            foreach (var v in val)
            {
                writer.CompressAndWrite(v);
            }
        }

        public override Box<List<int>> Deserialize(Reader reader)
        {
            var ret = ObjectPool<Box<List<int>>>.Request();
            int len = reader.ReadLength();
            var arr = new List<int>(len);
            //read item
            for (int i = 0; i < len; i++)
            {
                arr.Add((int)reader.DecompressAndReadNumber());
            }
            ret.Value = arr;
            return ret;
        }
    }

    internal class UIntWrapper : NinoWrapperBase<uint>
    {
        public override void Serialize(uint val, Writer writer)
        {
            writer.CompressAndWrite(val);
        }

        public override Box<uint> Deserialize(Reader reader)
        {
            var ret = ObjectPool<Box<uint>>.Request();
            ret.Value = (uint)reader.DecompressAndReadNumber();
            return ret;
        }
    }

    internal class UIntArrWrapper : NinoWrapperBase<uint[]>
    {
        public override void Serialize(uint[] val, Writer writer)
        {
            writer.CompressAndWrite(val.Length);
            foreach (var v in val)
            {
                writer.CompressAndWrite(v);
            }
        }

        public override Box<uint[]> Deserialize(Reader reader)
        {
            var ret = ObjectPool<Box<uint[]>>.Request();
            int len = reader.ReadLength();
            var arr = new uint[len];
            //read item
            for (int i = 0; i < len; i++)
            {
                arr[i] = (uint)reader.DecompressAndReadNumber();
            }
            ret.Value = arr;
            return ret;
        }
    }

    internal class UIntListWrapper : NinoWrapperBase<List<uint>>
    {
        public override void Serialize(List<uint> val, Writer writer)
        {
            writer.CompressAndWrite(val.Count);
            foreach (var v in val)
            {
                writer.CompressAndWrite(v);
            }
        }

        public override Box<List<uint>> Deserialize(Reader reader)
        {
            var ret = ObjectPool<Box<List<uint>>>.Request();
            int len = reader.ReadLength();
            var arr = new List<uint>(len);
            //read item
            for (int i = 0; i < len; i++)
            {
                arr.Add((uint)reader.DecompressAndReadNumber());
            }
            ret.Value = arr;
            return ret;
        }
    }

    internal class LongWrapper : NinoWrapperBase<long>
    {
        public override void Serialize(long val, Writer writer)
        {
            writer.CompressAndWrite(val);
        }

        public override Box<long> Deserialize(Reader reader)
        {
            var ret = ObjectPool<Box<long>>.Request();
            ret.Value = (long)reader.DecompressAndReadNumber();
            return ret;
        }
    }

    internal class LongArrWrapper : NinoWrapperBase<long[]>
    {
        public override void Serialize(long[] val, Writer writer)
        {
            writer.CompressAndWrite(val.Length);
            foreach (var v in val)
            {
                writer.CompressAndWrite(v);
            }
        }

        public override Box<long[]> Deserialize(Reader reader)
        {
            var ret = ObjectPool<Box<long[]>>.Request();
            int len = reader.ReadLength();
            var arr = new long[len];
            //read item
            for (int i = 0; i < len; i++)
            {
                arr[i] = (long)reader.DecompressAndReadNumber();
            }
            ret.Value = arr;
            return ret;
        }
    }

    internal class LongListWrapper : NinoWrapperBase<List<long>>
    {
        public override void Serialize(List<long> val, Writer writer)
        {
            writer.CompressAndWrite(val.Count);
            foreach (var v in val)
            {
                writer.CompressAndWrite(v);
            }
        }

        public override Box<List<long>> Deserialize(Reader reader)
        {
            var ret = ObjectPool<Box<List<long>>>.Request();
            int len = reader.ReadLength();
            var arr = new List<long>(len);
            //read item
            for (int i = 0; i < len; i++)
            {
                arr.Add((long)reader.DecompressAndReadNumber());
            }
            ret.Value = arr;
            return ret;
        }
    }

    internal class ULongWrapper : NinoWrapperBase<ulong>
    {
        public override void Serialize(ulong val, Writer writer)
        {
            writer.CompressAndWrite(val);
        }

        public override Box<ulong> Deserialize(Reader reader)
        {
            var ret = ObjectPool<Box<ulong>>.Request();
            ret.Value = reader.DecompressAndReadNumber();
            return ret;
        }
    }

    internal class ULongArrWrapper : NinoWrapperBase<ulong[]>
    {
        public override void Serialize(ulong[] val, Writer writer)
        {
            writer.CompressAndWrite(val.Length);
            foreach (var v in val)
            {
                writer.CompressAndWrite(v);
            }
        }

        public override Box<ulong[]> Deserialize(Reader reader)
        {
            var ret = ObjectPool<Box<ulong[]>>.Request();
            int len = reader.ReadLength();
            var arr = new ulong[len];
            //read item
            for (int i = 0; i < len; i++)
            {
                arr[i] = reader.DecompressAndReadNumber();
            }
            ret.Value = arr;
            return ret;
        }
    }

    internal class ULongListWrapper : NinoWrapperBase<List<ulong>>
    {
        public override void Serialize(List<ulong> val, Writer writer)
        {
            writer.CompressAndWrite(val.Count);
            foreach (var v in val)
            {
                writer.CompressAndWrite(v);
            }
        }

        public override Box<List<ulong>> Deserialize(Reader reader)
        {
            var ret = ObjectPool<Box<List<ulong>>>.Request();
            int len = reader.ReadLength();
            var arr = new List<ulong>(len);
            //read item
            for (int i = 0; i < len; i++)
            {
                arr.Add(reader.DecompressAndReadNumber());
            }
            ret.Value = arr;
            return ret;
        }
    }
}