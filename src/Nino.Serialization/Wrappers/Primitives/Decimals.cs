using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Nino.Serialization
{
    internal class FloatWrapper : NinoWrapperBase<float>
    {
        public override void Serialize(float val, ref Writer writer)
        {
            writer.Write(val);
        }

        public override float Deserialize(Reader reader)
        {
            return reader.ReadSingle();
        }
        
        public override int GetSize(float val)
        {
            return 4;
        }
    }

    internal class FloatArrWrapper : NinoWrapperBase<float[]>
    {
        public override void Serialize(float[] val, ref Writer writer)
        {
            writer.Write(val.AsSpan());
        }

        public override unsafe float[] Deserialize(Reader reader)
        {
            if (!reader.ReadBool()) return null;
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

            return arr;
        }
        
        public override int GetSize(float[] val)
        {
            if (val is null) return 1;
            return 1 + 4 + val.Length * 4;
        }
    }

    internal class FloatListWrapper : NinoWrapperBase<List<float>>
    {
        public override void Serialize(List<float> val, ref Writer writer)
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

        public override List<float> Deserialize(Reader reader)
        {
            if (!reader.ReadBool()) return null;
            int len = reader.ReadLength();
            var arr = new List<float>(len);
            //read item
            for (int i = 0; i < len; i++)
            {
                arr.Add(reader.ReadSingle());
            }

            return arr;
        }
        
        public override int GetSize(List<float> val)
        {
            if (val is null) return 1;
            int size = 1 + 4 + val.Count * 4;

            return size;
        }
    }

    internal class DoubleWrapper : NinoWrapperBase<double>
    {
        public override void Serialize(double val, ref Writer writer)
        {
            writer.Write(val);
        }

        public override double Deserialize(Reader reader)
        {
            return reader.ReadDouble();
        }
        
        public override int GetSize(double val)
        {
            return 8;
        }
    }

    internal class DoubleArrWrapper : NinoWrapperBase<double[]>
    {
        public override void Serialize(double[] val, ref Writer writer)
        {
            writer.Write(val.AsSpan());
        }

        public override unsafe double[] Deserialize(Reader reader)
        {
            if (!reader.ReadBool()) return null;
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

            return arr;
        }
        
        public override int GetSize(double[] val)
        {
            if (val is null) return 1;
            return 1 + 4 + val.Length * 8;
        }
    }

    internal class DoubleListWrapper : NinoWrapperBase<List<double>>
    {
        public override void Serialize(List<double> val, ref Writer writer)
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

        public override List<double> Deserialize(Reader reader)
        {
            if (!reader.ReadBool()) return null;
            int len = reader.ReadLength();
            var arr = new List<double>(len);
            //read item
            for (int i = 0; i < len; i++)
            {
                arr.Add(reader.ReadDouble());
            }

            return arr;
        }
        
        public override int GetSize(List<double> val)
        {
            if (val is null) return 1;
            int size = 1 + 4 + val.Count * 8;

            return size;
        }
    }

    internal class DecimalWrapper : NinoWrapperBase<decimal>
    {
        public override void Serialize(decimal val, ref Writer writer)
        {
            writer.Write(val);
        }

        public override decimal Deserialize(Reader reader)
        {
            return reader.ReadDecimal();
        }
        
        public override int GetSize(decimal val)
        {
            return 16;
        }
    }

    internal class DecimalArrWrapper : NinoWrapperBase<decimal[]>
    {
        public override void Serialize(decimal[] val, ref Writer writer)
        {
            writer.Write(val.AsSpan());
        }

        public override unsafe decimal[] Deserialize(Reader reader)
        {
            if (!reader.ReadBool()) return null;
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

            return arr;
        }
        
        public override int GetSize(decimal[] val)
        {
            if (val is null) return 1;
            return 1 + 4 + val.Length * 16;
        }
    }

    internal class DecimalListWrapper : NinoWrapperBase<List<decimal>>
    {
        public override void Serialize(List<decimal> val, ref Writer writer)
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

        public override List<decimal> Deserialize(Reader reader)
        {
            if (!reader.ReadBool()) return null;
            int len = reader.ReadLength();
            var arr = new List<decimal>(len);
            //read item
            for (int i = 0; i < len; i++)
            {
                arr.Add(reader.ReadDecimal());
            }

            return arr;
        }
        
        public override int GetSize(List<decimal> val)
        {
            if (val is null) return 1;
            int size = 1 + 4 + val.Count * 16;

            return size;
        }
    }
}