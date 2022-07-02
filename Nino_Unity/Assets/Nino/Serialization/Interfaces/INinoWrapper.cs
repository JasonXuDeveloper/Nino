using Nino.Shared.IO;

namespace Nino.Serialization
{
    internal interface INinoWrapper<T>
    {
        void Serialize(T val, Writer writer);
        Box<T> Deserialize(Reader reader);
    }

    internal interface INinoWrapper
    {
        void Serialize(object val, Writer writer);
        object Deserialize(Reader reader);
    }

    public abstract class NinoWrapperBase<T> : INinoWrapper<T>, INinoWrapper
    {
        public abstract void Serialize(T val, Writer writer);
        public abstract Box<T> Deserialize(Reader reader);

        public void Serialize(object val, Writer writer)
        {
            Serialize((T)val, writer);
        }
        
        object INinoWrapper.Deserialize(Reader reader)
        {
            if (!typeof(T).IsClass)
            {
                var v = Deserialize(reader);
                var ret = v.Value;
                ObjectPool<Box<T>>.Return(v);
                return ret;
            }
            return Deserialize(reader).Value;
        }
    }
}