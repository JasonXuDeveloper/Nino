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
            ret.Value = len != 0
                ? (float[])reader.TryGetBasicTypeArray(typeof(float), len, out _)
                : Array.Empty<float>();
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
            ret.Value = (List<float>)reader.TryGetBasicTypeList(typeof(float), len, out _);
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
            ret.Value = len != 0
                ? (double[])reader.TryGetBasicTypeArray(typeof(double), len, out _)
                : Array.Empty<double>();
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
            ret.Value = (List<double>)reader.TryGetBasicTypeList(typeof(double), len, out _);
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
            ret.Value = len != 0
                ? (decimal[])reader.TryGetBasicTypeArray(typeof(decimal), len, out _)
                : Array.Empty<decimal>();
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
            ret.Value = (List<decimal>)reader.TryGetBasicTypeList(typeof(decimal), len, out _);
            return ret;
        }
    }
}