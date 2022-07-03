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
}