using Nino.Shared.IO;

namespace Nino.Serialization
{
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
            var v = Deserialize(reader);
            var ret = v.Value;
            ObjectPool<Box<T>>.Return(v);
            return ret;
        }
    }
}