namespace Nino.Serialization
{
    public interface ISerializationHelper<T>
    {
        void NinoWriteMembers(T val, Nino.Serialization.Writer writer);
        T NinoReadMembers(Nino.Serialization.Reader reader);
    }
}