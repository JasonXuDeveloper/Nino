using System;
using Nino.Shared.IO;
using System.Collections.Generic;

namespace Nino.Serialization
{
    internal class FloatWrapper : NinoWrapperBase<float>
    {
        public override void Serialize(float val, Writer writer)
        {
            writer.Write(val);
        }

        public override Box<float> Deserialize(Reader reader)
        {
            var ret = ObjectPool<Box<float>>.Request();
            ret.Value = reader.ReadSingle();
            return ret;
        }
    }

    internal class FloatArrWrapper : NinoWrapperBase<float[]>
    {
        public override unsafe void Serialize(float[] val, Writer writer)
        {
            int len = val.Length;
            writer.CompressAndWrite(len);
            if (len > 0)
            {
                fixed (float* ptr = val)
                {
                    writer.Write((byte*)ptr, len * 4);
                }
            }
        }

        public override unsafe Box<float[]> Deserialize(Reader reader)
        {
            var ret = ObjectPool<Box<float[]>>.Request();
            int len = reader.ReadLength();
            float[] arr;
            if (len == 0)
            {
                arr = Array.Empty<float>();
            }
            else
            {
                arr = new float[len];
                fixed (float* arrPtr = arr)
                {
                    reader.ReadToBuffer((byte*)arrPtr, len * 4);
                }
            }
            ret.Value = arr;
            return ret;
        }
    }

    internal class FloatListWrapper : NinoWrapperBase<List<float>>
    {
        public override void Serialize(List<float> val, Writer writer)
        {
            writer.CompressAndWrite(val.Count);
            foreach (var v in val)
            {
                writer.Write(v);
            }
        }

        public override Box<List<float>> Deserialize(Reader reader)
        {
            var ret = ObjectPool<Box<List<float>>>.Request();
            int len = reader.ReadLength();
            var arr = new List<float>(len);
            //read item
            for (int i = 0; i < len; i++)
            {
                arr.Add(reader.ReadSingle());
            }
            ret.Value = arr;
            return ret;
        }
    }

    internal class DoubleWrapper : NinoWrapperBase<double>
    {
        public override void Serialize(double val, Writer writer)
        {
            writer.Write(val);
        }

        public override Box<double> Deserialize(Reader reader)
        {
            var ret = ObjectPool<Box<double>>.Request();
            ret.Value = reader.ReadDouble();
            return ret;
        }
    }

    internal class DoubleArrWrapper : NinoWrapperBase<double[]>
    {
        public override unsafe void Serialize(double[] val, Writer writer)
        {
            int len = val.Length;
            writer.CompressAndWrite(len);
            if (len > 0)
            {
                fixed (double* ptr = val)
                {
                    writer.Write((byte*)ptr, len * 8);
                }
            }
        }

        public override unsafe Box<double[]> Deserialize(Reader reader)
        {
            var ret = ObjectPool<Box<double[]>>.Request();
            int len = reader.ReadLength();
            double[] arr;
            if (len == 0)
            {
                arr = Array.Empty<double>();
            }
            else
            {
                arr = new double[len];
                fixed (double* arrPtr = arr)
                {
                    reader.ReadToBuffer((byte*)arrPtr, len * 8);
                }
            }
            ret.Value = arr;
            return ret;
        }
    }

    internal class DoubleListWrapper : NinoWrapperBase<List<double>>
    {
        public override void Serialize(List<double> val, Writer writer)
        {
            writer.CompressAndWrite(val.Count);
            foreach (var v in val)
            {
                writer.Write(v);
            }
        }

        public override Box<List<double>> Deserialize(Reader reader)
        {
            var ret = ObjectPool<Box<List<double>>>.Request();
            int len = reader.ReadLength();
            var arr = new List<double>(len);
            //read item
            for (int i = 0; i < len; i++)
            {
                arr.Add(reader.ReadDouble());
            }
            ret.Value = arr;
            return ret;
        }
    }

    internal class DecimalWrapper : NinoWrapperBase<decimal>
    {
        public override void Serialize(decimal val, Writer writer)
        {
            writer.Write(val);
        }

        public override Box<decimal> Deserialize(Reader reader)
        {
            var ret = ObjectPool<Box<decimal>>.Request();
            ret.Value = reader.ReadDecimal();
            return ret;
        }
    }

    internal class DecimalArrWrapper : NinoWrapperBase<decimal[]>
    {
        public override unsafe void Serialize(decimal[] val, Writer writer)
        {
            int len = val.Length;
            writer.CompressAndWrite(len);
            if (len > 0)
            {
                fixed (decimal* ptr = val)
                {
                    writer.Write((byte*)ptr, len * 16);
                }
            }
        }

        public override unsafe Box<decimal[]> Deserialize(Reader reader)
        {
            var ret = ObjectPool<Box<decimal[]>>.Request();
            int len = reader.ReadLength();
            decimal[] arr;
            if (len == 0)
            {
                arr = Array.Empty<decimal>();
            }
            else
            {
                arr = new decimal[len];
                fixed (decimal* arrPtr = arr)
                {
                    reader.ReadToBuffer((byte*)arrPtr, len * 16);
                }
            }
            ret.Value = arr;
            return ret;
        }
    }

    internal class DecimalListWrapper : NinoWrapperBase<List<decimal>>
    {
        public override void Serialize(List<decimal> val, Writer writer)
        {
            writer.CompressAndWrite(val.Count);
            foreach (var v in val)
            {
                writer.Write(v);
            }
        }

        public override Box<List<decimal>> Deserialize(Reader reader)
        {
            var ret = ObjectPool<Box<List<decimal>>>.Request();
            int len = reader.ReadLength();
            var arr = new List<decimal>(len);
            //read item
            for (int i = 0; i < len; i++)
            {
                arr.Add(reader.ReadDecimal());
            }
            ret.Value = arr;
            return ret;
        }
    }
}