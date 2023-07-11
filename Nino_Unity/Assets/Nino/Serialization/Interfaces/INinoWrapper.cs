namespace Nino.Serialization
{
    public interface INinoWrapper<T>
    {
        void Serialize(T val, ref Writer writer);
        T Deserialize(Reader reader);
        int GetSize(T val);
    }

    public interface INinoWrapper
    {
        void Serialize(object val, ref Writer writer);
        object Deserialize(Reader reader);
        int GetSize(object val);
    }
}