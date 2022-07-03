using System;
using Nino.Shared.IO;
using System.Collections.Generic;

namespace Nino.Serialization
{
    internal class BoolWrapper : NinoWrapperBase<bool>
    {
        public override void Serialize(bool val, Writer writer)
        {
            writer.Write(val);
        }

        public override Box<bool> Deserialize(Reader reader)
        {
            var ret = ObjectPool<Box<bool>>.Request();
            ret.Value = reader.ReadBool();
            return ret;
        }
    }

    internal class BoolArrWrapper : NinoWrapperBase<bool[]>
    {
        public override unsafe void Serialize(bool[] val, Writer writer)
        {
            int len = val.Length;
            writer.CompressAndWrite(len);
            if (len > 0)
            {
                fixed (bool* ptr = val)
                {
                    writer.Write((byte*)ptr, len);
                }
            }
        }

        public override unsafe Box<bool[]> Deserialize(Reader reader)
        {
            var ret = ObjectPool<Box<bool[]>>.Request();
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
            ret.Value = arr;
            return ret;
        }
    }

    internal class BoolListWrapper : NinoWrapperBase<List<bool>>
    {
        public override void Serialize(List<bool> val, Writer writer)
        {
            writer.CompressAndWrite(val.Count);
            foreach (var v in val)
            {
                writer.Write(v);
            }
        }

        public override Box<List<bool>> Deserialize(Reader reader)
        {
            var ret = ObjectPool<Box<List<bool>>>.Request();
            int len = reader.ReadLength();
            var arr = new List<bool>(len);
            //read item
            int i = 0;
            while (i++ < len)
            {
                arr.Add(reader.ReadBool());
            }
            ret.Value = arr;
            return ret;
        }
    }

    internal class CharWrapper : NinoWrapperBase<char>
    {
        public override void Serialize(char val, Writer writer)
        {
            writer.Write(val);
        }

        public override Box<char> Deserialize(Reader reader)
        {
            var ret = ObjectPool<Box<char>>.Request();
            ret.Value = reader.ReadChar();
            return ret;
        }
    }

    internal class CharArrWrapper : NinoWrapperBase<char[]>
    {
        public override unsafe void Serialize(char[] val, Writer writer)
        {
            int len = val.Length;
            writer.CompressAndWrite(len);
            if (len > 0)
            {
                fixed (char* ptr = val)
                {
                    writer.Write((byte*)ptr, len * 2);
                }
            }
        }

        public override unsafe Box<char[]> Deserialize(Reader reader)
        {
            var ret = ObjectPool<Box<char[]>>.Request();
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
            ret.Value = arr;
            return ret;
        }
    }

    internal class CharListWrapper : NinoWrapperBase<List<char>>
    {
        public override void Serialize(List<char> val, Writer writer)
        {
            writer.CompressAndWrite(val.Count);
            foreach (var v in val)
            {
                writer.Write(v);
            }
        }

        public override Box<List<char>> Deserialize(Reader reader)
        {
            var ret = ObjectPool<Box<List<char>>>.Request();
            int len = reader.ReadLength();
            var arr = new List<char>(len);
            //read item
            int i = 0;
            while (i++ < len)
            {
                arr.Add(reader.ReadChar());
            }
            ret.Value = arr;
            return ret;
        }
    }

    internal class StringWrapper : NinoWrapperBase<string>
    {
        public override void Serialize(string val, Writer writer)
        {
            writer.Write(val);
        }

        public override Box<string> Deserialize(Reader reader)
        {
            var ret = ObjectPool<Box<string>>.Request();
            ret.Value = reader.ReadString();
            return ret;
        }
    }

    internal class StringArrWrapper : NinoWrapperBase<string[]>
    {
        public override void Serialize(string[] val, Writer writer)
        {
            writer.CompressAndWrite(val.Length);
            foreach (var v in val)
            {
                writer.Write(v);
            }
        }

        public override Box<string[]> Deserialize(Reader reader)
        {
            var ret = ObjectPool<Box<string[]>>.Request();
            int len = reader.ReadLength();
            var arr = new string[len];
            int i = 0;
            while (i < len)
            {
                arr[i++] = reader.ReadString();
            }
            ret.Value = arr;
            return ret;
        }
    }

    internal class StringListWrapper : NinoWrapperBase<List<string>>
    {
        public override void Serialize(List<string> val, Writer writer)
        {
            writer.CompressAndWrite(val.Count);
            foreach (var v in val)
            {
                writer.Write(v);
            }
        }

        public override Box<List<string>> Deserialize(Reader reader)
        {
            var ret = ObjectPool<Box<List<string>>>.Request();
            int len = reader.ReadLength();
            var arr = new List<string>(len);
            //read item
            int i = 0;
            while (i++ < len)
            {
                arr.Add(reader.ReadString());
            }
            ret.Value = arr;
            return ret;
        }
    }

    internal class DateTimeWrapper : NinoWrapperBase<DateTime>
    {
        public override void Serialize(DateTime val, Writer writer)
        {
            writer.Write(val);
        }

        public override Box<DateTime> Deserialize(Reader reader)
        {
            var ret = ObjectPool<Box<DateTime>>.Request();
            ret.Value = reader.ReadDateTime();
            return ret;
        }
    }

    internal class DateTimeArrWrapper : NinoWrapperBase<DateTime[]>
    {
        public override void Serialize(DateTime[] val, Writer writer)
        {
            writer.CompressAndWrite(val.Length);
            foreach (var v in val)
            {
                writer.Write(v);
            }
        }

        public override Box<DateTime[]> Deserialize(Reader reader)
        {
            var ret = ObjectPool<Box<DateTime[]>>.Request();
            int len = reader.ReadLength();
            var arr = new DateTime[len];
            int i = 0;
            while (i < len)
            {
                arr[i++] = reader.ReadDateTime();
            }
            ret.Value = arr;
            return ret;
        }
    }

    internal class DateTimeListWrapper : NinoWrapperBase<List<DateTime>>
    {
        public override void Serialize(List<DateTime> val, Writer writer)
        {
            writer.CompressAndWrite(val.Count);
            foreach (var v in val)
            {
                writer.Write(v);
            }
        }

        public override Box<List<DateTime>> Deserialize(Reader reader)
        {
            var ret = ObjectPool<Box<List<DateTime>>>.Request();
            int len = reader.ReadLength();
            var arr = new List<DateTime>(len);
            int i = 0;
            while (i++ < len)
            {
                arr.Add(reader.ReadDateTime());
            }
            ret.Value = arr;
            return ret;
        }
    }

    internal class GenericWrapper<T> : NinoWrapperBase<T>
    {
        public Serializer.ImporterDelegate<T> Importer;
        public Deserializer.ExporterDelegate<T> Exporter;
        
        public override void Serialize(T val, Writer writer)
        {
            if(Importer == null)
                throw new InvalidOperationException($"Importer is null for type: {typeof(T)}");
            Importer(val, writer);
        }

        public override Box<T> Deserialize(Reader reader)
        {
            if(Exporter == null)
                throw new InvalidOperationException($"Exporter is null for type: {typeof(T)}");
            var ret = ObjectPool<Box<T>>.Request();
            var val = Exporter(reader);
            ret.Value = val;
            return ret;
        }
    }
}