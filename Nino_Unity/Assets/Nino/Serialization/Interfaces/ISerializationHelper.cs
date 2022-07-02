namespace Nino.Serialization
{
    public interface ISerializationHelper<T> : ISerializationHelper
    {
        void NinoWriteMembers(T val, Nino.Serialization.Writer writer);
        new T NinoReadMembers(Nino.Serialization.Reader reader);
    }

    public interface ISerializationHelper
    {
        void NinoWriteMembers(object val, Nino.Serialization.Writer writer);
        object NinoReadMembers(Nino.Serialization.Reader reader);
    }
}