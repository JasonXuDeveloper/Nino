using System;
using System.Collections.Generic;

namespace Nino.Serialization
{
    internal class ByteWrapper : NinoWrapperBase<byte>
    {
        public override void Serialize(byte val, ref Writer writer)
        {
            writer.Write(val);
        }

        public override byte Deserialize(Reader reader)
        {
            return reader.ReadByte();
        }
        
        public override int GetSize(byte val)
        {
            return 1;
        }
    }

    internal class ByteArrWrapper : NinoWrapperBase<byte[]>
    {
        public override void Serialize(byte[] val, ref Writer writer)
        {
            writer.Write(val.AsSpan());
        }

        public override byte[] Deserialize(Reader reader)
        {
            if (!reader.ReadBool()) return null;
            int len = reader.ReadLength();
            return len != 0 ? reader.ReadBytes(len) : Array.Empty<byte>();
        }
        
        public override int GetSize(byte[] val)
        {
            if (val is null) return 1;
            return 1 + 4 + val.Length;
        }
    }

    internal class ByteListWrapper : NinoWrapperBase<List<byte>>
    {
        public override void Serialize(List<byte> val, ref Writer writer)
        {
            if (val is null)
            {
                writer.Write(false);
                return;
            }

            writer.Write(true);
            writer.Write(val.Count);
            foreach (var v in val)
            {
                writer.Write(v);
            }
        }

        public override List<byte> Deserialize(Reader reader)
        {
            if (!reader.ReadBool()) return null;
            int len = reader.ReadLength();
            var arr = new List<byte>(len);
            //read item
            int i = 0;
            while (i++ < len)
            {
                arr.Add(reader.ReadByte());
            }

            return arr;
        }
        
        public override int GetSize(List<byte> val)
        {
            if (val is null) return 1;
            return 1 + 4 + val.Count;
        }
    }

    internal class SByteWrapper : NinoWrapperBase<sbyte>
    {
        public override void Serialize(sbyte val, ref Writer writer)
        {
            writer.Write(val);
        }

        public override sbyte Deserialize(Reader reader)
        {
            return reader.ReadSByte();
        }
        
        public override int GetSize(sbyte val)
        {
            return 1;
        }
    }

    internal class SByteArrWrapper : NinoWrapperBase<sbyte[]>
    {
        public override void Serialize(sbyte[] val, ref Writer writer)
        {
            writer.Write(val.AsSpan());
        }

        public override unsafe sbyte[] Deserialize(Reader reader)
        {
            if (!reader.ReadBool()) return null;
            int len = reader.ReadLength();
            sbyte[] arr;
            if (len == 0)
            {
                arr = Array.Empty<sbyte>();
            }
            else
            {
                arr = new sbyte[len];
                fixed (sbyte* arrPtr = arr)
                {
                    reader.ReadToBuffer((byte*)arrPtr, len);
                }
            }

            return arr;
        }
        
        public override int GetSize(sbyte[] val)
        {
            if (val is null) return 1;
            return 1 + 4 + val.Length;
        }
    }

    internal class SByteListWrapper : NinoWrapperBase<List<sbyte>>
    {
        public override void Serialize(List<sbyte> val, ref Writer writer)
        {
            if (val is null)
            {
                writer.Write(false);
                return;
            }

            writer.Write(true);
            writer.Write(val.Count);
            foreach (var v in val)
            {
                writer.Write(v);
            }
        }

        public override List<sbyte> Deserialize(Reader reader)
        {
            if (!reader.ReadBool()) return null;
            int len = reader.ReadLength();
            var arr = new List<sbyte>(len);
            //read item
            int i = 0;
            while (i++ < len)
            {
                arr.Add(reader.ReadSByte());
            }

            return arr;
        }
        
        public override int GetSize(List<sbyte> val)
        {
            if (val is null) return 1;
            return 1 + 4 + val.Count;
        }
    }

    internal class ShortWrapper : NinoWrapperBase<short>
    {
        public override void Serialize(short val, ref Writer writer)
        {
            writer.Write(val);
        }

        public override short Deserialize(Reader reader)
        {
            return reader.ReadInt16();
        }
        
        public override int GetSize(short val)
        {
            return 2;
        }
    }

    internal class ShortArrWrapper : NinoWrapperBase<short[]>
    {
        public override void Serialize(short[] val, ref Writer writer)
        {
            writer.Write(val.AsSpan());
        }

        public override unsafe short[] Deserialize(Reader reader)
        {
            if (!reader.ReadBool()) return null;
            int len = reader.ReadLength();
            short[] arr;
            if (len == 0)
            {
                arr = Array.Empty<short>();
            }
            else
            {
                arr = new short[len];
                fixed (short* arrPtr = arr)
                {
                    reader.ReadToBuffer((byte*)arrPtr, len * 2);
                }
            }

            return arr;
        }
        
        public override int GetSize(short[] val)
        {
            if (val is null) return 1;
            return 1 + 4 + val.Length * 2;
        }
    }

    internal class ShortListWrapper : NinoWrapperBase<List<short>>
    {
        public override void Serialize(List<short> val, ref Writer writer)
        {
            if (val is null)
            {
                writer.Write(false);
                return;
            }

            writer.Write(true);
            writer.Write(val.Count);
            foreach (var v in val)
            {
                writer.Write(v);
            }
        }

        public override List<short> Deserialize(Reader reader)
        {
            if (!reader.ReadBool()) return null;
            int len = reader.ReadLength();
            var arr = new List<short>(len);
            //read item
            int i = 0;
            while (i++ < len)
            {
                arr.Add(reader.ReadInt16());
            }

            return arr;
        }
        
        public override int GetSize(List<short> val)
        {
            if (val is null) return 1;
            return 1 + 4 + val.Count * 2;
        }
    }

    internal class UShortWrapper : NinoWrapperBase<ushort>
    {
        public override void Serialize(ushort val, ref Writer writer)
        {
            writer.Write(val);
        }

        public override ushort Deserialize(Reader reader)
        {
            return reader.ReadUInt16();
        }
        
        public override int GetSize(ushort val)
        {
            return 2;
        }
    }

    internal class UShortArrWrapper : NinoWrapperBase<ushort[]>
    {
        public override void Serialize(ushort[] val, ref Writer writer)
        {
            writer.Write(val.AsSpan());
        }

        public override unsafe ushort[] Deserialize(Reader reader)
        {
            if (!reader.ReadBool()) return null;
            int len = reader.ReadLength();
            ushort[] arr;
            if (len == 0)
            {
                arr = Array.Empty<ushort>();
            }
            else
            {
                arr = new ushort[len];
                fixed (ushort* arrPtr = arr)
                {
                    reader.ReadToBuffer((byte*)arrPtr, len * 2);
                }
            }

            return arr;
        }
        
        public override int GetSize(ushort[] val)
        {
            if (val is null) return 1;
            return 1 + 4 + val.Length * 2;
        }
    }

    internal class UShortListWrapper : NinoWrapperBase<List<ushort>>
    {
        public override void Serialize(List<ushort> val, ref Writer writer)
        {
            if (val is null)
            {
                writer.Write(false);
                return;
            }

            writer.Write(true);
            writer.Write(val.Count);
            foreach (var v in val)
            {
                writer.Write(v);
            }
        }

        public override List<ushort> Deserialize(Reader reader)
        {
            if (!reader.ReadBool()) return null;
            int len = reader.ReadLength();
            var arr = new List<ushort>(len);
            //read item
            int i = 0;
            while (i++ < len)
            {
                arr.Add(reader.ReadUInt16());
            }

            return arr;
        }
        
        public override int GetSize(List<ushort> val)
        {
            if (val is null) return 1;
            return 1 + 4 + val.Count * 2;
        }
    }

    internal class IntWrapper : NinoWrapperBase<int>
    {
        public override void Serialize(int val, ref Writer writer)
        {
            writer.Write(val);
        }

        public override int Deserialize(Reader reader)
        {
            return reader.ReadInt32();
        }
        
        public override int GetSize(int val)
        {
            return 4;
        }
    }

    internal class IntArrWrapper : NinoWrapperBase<int[]>
    {
        public override void Serialize(int[] val, ref Writer writer)
        {
            writer.Write(val.AsSpan());
        }

        public override unsafe int[] Deserialize(Reader reader)
        {
            if (!reader.ReadBool()) return null;
            int len = reader.ReadLength();
            var arr = new int[len];
            //read item
            fixed (int* arrPtr = arr)
            {
                reader.ReadToBuffer((byte*)arrPtr, len * 4);
            }

            return arr;
        }
        
        public override int GetSize(int[] val)
        {
            if (val is null) return 1;
            return 1 + 4 + val.Length * 4;
        }
    }

    internal class IntListWrapper : NinoWrapperBase<List<int>>
    {
        public override void Serialize(List<int> val, ref Writer writer)
        {
            if (val is null)
            {
                writer.Write(false);
                return;
            }

            writer.Write(true);
            writer.Write(val.Count);
            foreach (var v in val)
            {
                writer.Write(v);
            }
        }

        public override List<int> Deserialize(Reader reader)
        {
            if (!reader.ReadBool()) return null;
            int len = reader.ReadLength();
            var arr = new List<int>(len);
            //read item
            int i = 0;
            while (i++ < len)
            {
                arr.Add(reader.ReadInt32());
            }

            return arr;
        }
        
        public override int GetSize(List<int> val)
        {
            if (val is null) return 1;
            return 1 + 4 + val.Count * 4;
        }
    }

    internal class UIntWrapper : NinoWrapperBase<uint>
    {
        public override void Serialize(uint val, ref Writer writer)
        {
            writer.Write(val);
        }

        public override uint Deserialize(Reader reader)
        {
            return reader.ReadUInt32();
        }
        
        public override int GetSize(uint val)
        {
            return 4;
        }
    }

    internal class UIntArrWrapper : NinoWrapperBase<uint[]>
    {
        public override void Serialize(uint[] val, ref Writer writer)
        {
            writer.Write(val.AsSpan());
        }

        public override unsafe uint[] Deserialize(Reader reader)
        {
            if (!reader.ReadBool()) return null;
            int len = reader.ReadLength();
            var arr = new uint[len];
            //read item
            fixed (uint* arrPtr = arr)
            {
                reader.ReadToBuffer((byte*)arrPtr, len * 4);
            }

            return arr;
        }
        
        public override int GetSize(uint[] val)
        {
            if (val is null) return 1;
            return 1 + 4 + val.Length * 4;
        }
    }

    internal class UIntListWrapper : NinoWrapperBase<List<uint>>
    {
        public override void Serialize(List<uint> val, ref Writer writer)
        {
            if (val is null)
            {
                writer.Write(false);
                return;
            }

            writer.Write(true);
            writer.Write(val.Count);
            foreach (var v in val)
            {
                writer.Write(v);
            }
        }

        public override List<uint> Deserialize(Reader reader)
        {
            if (!reader.ReadBool()) return null;
            int len = reader.ReadLength();
            var arr = new List<uint>(len);
            //read item
            int i = 0;
            while (i++ < len)
            {
                arr.Add(reader.ReadUInt32());
            }

            return arr;
        }
        
        public override int GetSize(List<uint> val)
        {
            if (val is null) return 1;
            return 1 + 4 + val.Count * 4;
        }
    }

    internal class LongWrapper : NinoWrapperBase<long>
    {
        public override void Serialize(long val, ref Writer writer)
        {
            writer.Write(val);
        }

        public override long Deserialize(Reader reader)
        {
            return reader.ReadInt64();
        }
        
        public override int GetSize(long val)
        {
            return 8;
        }
    }

    internal class LongArrWrapper : NinoWrapperBase<long[]>
    {
        public override void Serialize(long[] val, ref Writer writer)
        {
            writer.Write(val.AsSpan());
        }

        public override unsafe long[] Deserialize(Reader reader)
        {
            if (!reader.ReadBool()) return null;
            int len = reader.ReadLength();
            var arr = new long[len];
            //read item
            fixed (long* arrPtr = arr)
            {
                reader.ReadToBuffer((byte*)arrPtr, len * 8);
            }

            return arr;
        }
        
        public override int GetSize(long[] val)
        {
            if (val is null) return 1;
            return 1 + 4 + val.Length * 8;
        }
    }

    internal class LongListWrapper : NinoWrapperBase<List<long>>
    {
        public override void Serialize(List<long> val, ref Writer writer)
        {
            if (val is null)
            {
                writer.Write(false);
                return;
            }

            writer.Write(true);
            writer.Write(val.Count);
            foreach (var v in val)
            {
                writer.Write(v);
            }
        }

        public override List<long> Deserialize(Reader reader)
        {
            if (!reader.ReadBool()) return null;
            int len = reader.ReadLength();
            var arr = new List<long>(len);
            //read item
            int i = 0;
            while (i++ < len)
            {
                arr.Add(reader.ReadInt64());
            }

            return arr;
        }
        
        public override int GetSize(List<long> val)
        {
            if (val is null) return 1;
            return 1 + 4 + val.Count * 8;
        }
    }

    internal class ULongWrapper : NinoWrapperBase<ulong>
    {
        public override void Serialize(ulong val, ref Writer writer)
        {
            writer.Write(val);
        }

        public override ulong Deserialize(Reader reader)
        {
            return reader.ReadUInt64();
        }
        
        public override int GetSize(ulong val)
        {
            return 8;
        }
    }

    internal class ULongArrWrapper : NinoWrapperBase<ulong[]>
    {
        public override void Serialize(ulong[] val, ref Writer writer)
        {
            writer.Write(val.AsSpan());
        }

        public override unsafe ulong[] Deserialize(Reader reader)
        {
            if (!reader.ReadBool()) return null;
            int len = reader.ReadLength();
            var arr = new ulong[len];
            //read item
            fixed (ulong* arrPtr = arr)
            {
                reader.ReadToBuffer((byte*)arrPtr, len * 8);
            }

            return arr;
        }
        
        public override int GetSize(ulong[] val)
        {
            if (val is null) return 1;
            return 1 + 4 + val.Length * 8;
        }
    }

    internal class ULongListWrapper : NinoWrapperBase<List<ulong>>
    {
        public override void Serialize(List<ulong> val, ref Writer writer)
        {
            if (val is null)
            {
                writer.Write(false);
                return;
            }

            writer.Write(true);
            writer.Write(val.Count);
            foreach (var v in val)
            {
                writer.Write(v);
            }
        }

        public override List<ulong> Deserialize(Reader reader)
        {
            if (!reader.ReadBool()) return null;
            int len = reader.ReadLength();
            var arr = new List<ulong>(len);
            //read item
            int i = 0;
            while (i++ < len)
            {
                arr.Add(reader.ReadUInt64());
            }

            return arr;
        }
        
        public override int GetSize(List<ulong> val)
        {
            if (val is null) return 1;
            return 1 + 4 + val.Count * 8;
        }
    }
}