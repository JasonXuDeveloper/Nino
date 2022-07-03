using System;
using System.Collections.Generic;
using Nino.Shared.IO;

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
        public override void Serialize(float[] val, Writer writer)
        {
            writer.CompressAndWrite(val.Length);
            foreach (var v in val)
            {
                writer.Write(v);
            }
        }

        public override Box<float[]> Deserialize(Reader reader)
        {
            var ret = ObjectPool<Box<float[]>>.Request();
            int len = reader.ReadLength();
            var arr = new float[len];
            //read item
            for (int i = 0; i < len; i++)
            {
                arr[i] = reader.ReadSingle();
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
        public override void Serialize(double[] val, Writer writer)
        {
            writer.CompressAndWrite(val.Length);
            foreach (var v in val)
            {
                writer.Write(v);
            }
        }

        public override Box<double[]> Deserialize(Reader reader)
        {
            var ret = ObjectPool<Box<double[]>>.Request();
            int len = reader.ReadLength();
            var arr = new double[len];
            //read item
            for (int i = 0; i < len; i++)
            {
                arr[i] = reader.ReadDouble();
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
        public override void Serialize(decimal[] val, Writer writer)
        {
            writer.CompressAndWrite(val.Length);
            foreach (var v in val)
            {
                writer.Write(v);
            }
        }

        public override Box<decimal[]> Deserialize(Reader reader)
        {
            var ret = ObjectPool<Box<decimal[]>>.Request();
            int len = reader.ReadLength();
            var arr = new decimal[len];
            //read item
            for (int i = 0; i < len; i++)
            {
                arr[i] = reader.ReadDecimal();
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