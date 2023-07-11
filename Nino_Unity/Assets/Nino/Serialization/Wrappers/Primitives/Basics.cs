using System;
using System.Collections.Generic;

namespace Nino.Serialization
{
    internal class BoolWrapper : NinoWrapperBase<bool>
    {
        public override void Serialize(bool val, ref Writer writer)
        {
            writer.Write(val);
        }

        public override bool Deserialize(Reader reader)
        {
            return reader.ReadBool();
        }

        public override int GetSize(bool val)
        {
            return 1;
        }
    }

    internal class BoolArrWrapper : NinoWrapperBase<bool[]>
    {
        public override void Serialize(bool[] val, ref Writer writer)
        {
            writer.Write(val.AsSpan());
        }

        public override unsafe bool[] Deserialize(Reader reader)
        {
            if (!reader.ReadBool()) return null;
            int len = reader.ReadLength();
            bool[] arr;
            if (len == 0)
            {
                arr = Array.Empty<bool>();
            }
            else
            {
                arr = new bool[len];
                fixed (bool* arrPtr = arr)
                {
                    reader.ReadToBuffer((byte*)arrPtr, len);
                }
            }

            return arr;
        }

        public override int GetSize(bool[] val)
        {
            if (val is null) return 1;
            return 1 + 4 + val.Length;
        }
    }

    internal class BoolListWrapper : NinoWrapperBase<List<bool>>
    {
        public override void Serialize(List<bool> val, ref Writer writer)
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

        public override List<bool> Deserialize(Reader reader)
        {
            if (!reader.ReadBool()) return null;
            int len = reader.ReadLength();
            var arr = new List<bool>(len);
            //read item
            int i = 0;
            while (i++ < len)
            {
                arr.Add(reader.ReadBool());
            }

            return arr;
        }

        public override int GetSize(List<bool> val)
        {
            if (val is null) return 1;
            return 1 + 4 + val.Count;
        }
    }

    internal class CharWrapper : NinoWrapperBase<char>
    {
        public override void Serialize(char val, ref Writer writer)
        {
            writer.Write(val);
        }

        public override char Deserialize(Reader reader)
        {
            return reader.ReadChar();
        }

        public override int GetSize(char val)
        {
            return 2;
        }
    }

    internal class CharArrWrapper : NinoWrapperBase<char[]>
    {
        public override void Serialize(char[] val, ref Writer writer)
        {
            writer.Write(val.AsSpan());
        }

        public override unsafe char[] Deserialize(Reader reader)
        {
            if (!reader.ReadBool()) return null;
            int len = reader.ReadLength();
            char[] arr;
            if (len == 0)
            {
                arr = Array.Empty<char>();
            }
            else
            {
                arr = new char[len];
                fixed (char* arrPtr = arr)
                {
                    reader.ReadToBuffer((byte*)arrPtr, len * 2);
                }
            }

            return arr;
        }

        public override int GetSize(char[] val)
        {
            if (val is null) return 1;
            return 1 + 4 + val.Length * 2;
        }
    }

    internal class CharListWrapper : NinoWrapperBase<List<char>>
    {
        public override void Serialize(List<char> val, ref Writer writer)
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

        public override List<char> Deserialize(Reader reader)
        {
            if (!reader.ReadBool()) return null;
            int len = reader.ReadLength();
            var arr = new List<char>(len);
            //read item
            int i = 0;
            while (i++ < len)
            {
                arr.Add(reader.ReadChar());
            }

            return arr;
        }

        public override int GetSize(List<char> val)
        {
            if (val is null) return 1;
            return 1 + 4 + val.Count * 2;
        }
    }

    internal class StringWrapper : NinoWrapperBase<string>
    {
        public override void Serialize(string val, ref Writer writer)
        {
            writer.Write(val);
        }

        public override string Deserialize(Reader reader)
        {
            return reader.ReadString();
        }

        public override int GetSize(string val)
        {
            if (val is null) return 1;
            return 1 + 4 + val.Length * 2;
        }
    }

    internal class StringArrWrapper : NinoWrapperBase<string[]>
    {
        public override void Serialize(string[] val, ref Writer writer)
        {
            if (val is null)
            {
                writer.Write(false);
                return;
            }

            writer.Write(true);
            writer.Write(val.Length);
            foreach (var v in val)
            {
                writer.Write(v);
            }
        }

        public override string[] Deserialize(Reader reader)
        {
            if (!reader.ReadBool()) return null;
            int len = reader.ReadLength();
            var arr = new string[len];
            int i = 0;
            while (i < len)
            {
                arr[i++] = reader.ReadString();
            }

            return arr;
        }

        public override int GetSize(string[] val)
        {
            if (val is null) return 1;
            int size = 1 + 4;
            foreach (var v in val)
            {
                size += 1 + 4 + v.Length * 2;
            }

            return size;
        }
    }

    internal class StringListWrapper : NinoWrapperBase<List<string>>
    {
        public override void Serialize(List<string> val, ref Writer writer)
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

        public override List<string> Deserialize(Reader reader)
        {
            if (!reader.ReadBool()) return null;
            int len = reader.ReadLength();
            var arr = new List<string>(len);
            //read item
            int i = 0;
            while (i++ < len)
            {
                arr.Add(reader.ReadString());
            }

            return arr;
        }

        public override int GetSize(List<string> val)
        {
            if (val is null) return 1;
            int size = 1 + 4;
            foreach (var v in val)
            {
                size += 1 + 4 + v.Length * 2;
            }

            return size;
        }
    }

    internal class DateTimeWrapper : NinoWrapperBase<DateTime>
    {
        public override void Serialize(DateTime val, ref Writer writer)
        {
            writer.Write(val);
        }

        public override DateTime Deserialize(Reader reader)
        {
            return reader.ReadDateTime();
        }

        public override int GetSize(DateTime val)
        {
            return 8;
        }
    }

    internal class DateTimeArrWrapper : NinoWrapperBase<DateTime[]>
    {
        public override void Serialize(DateTime[] val, ref Writer writer)
        {
            writer.Write(val.AsSpan());
        }

        public override unsafe DateTime[] Deserialize(Reader reader)
        {
            if (!reader.ReadBool()) return null;
            int len = reader.ReadLength();
            DateTime[] arr;
            if (len == 0)
            {
                arr = Array.Empty<DateTime>();
            }
            else
            {
                arr = new DateTime[len];
                fixed (DateTime* arrPtr = arr)
                {
                    reader.ReadToBuffer((byte*)arrPtr, len * 8);
                }
            }

            return arr;
        }

        public override int GetSize(DateTime[] val)
        {
            if (val is null) return 1;
            return 1 + 4 + val.Length * 8;
        }
    }

    internal class DateTimeListWrapper : NinoWrapperBase<List<DateTime>>
    {
        public override void Serialize(List<DateTime> val, ref Writer writer)
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

        public override List<DateTime> Deserialize(Reader reader)
        {
            if (!reader.ReadBool()) return null;
            int len = reader.ReadLength();
            var arr = new List<DateTime>(len);
            int i = 0;
            while (i++ < len)
            {
                arr.Add(reader.ReadDateTime());
            }

            return arr;
        }

        public override int GetSize(List<DateTime> val)
        {
            if (val is null) return 1;
            int size = 1 + 4 + val.Count * 8;

            return size;
        }
    }
}