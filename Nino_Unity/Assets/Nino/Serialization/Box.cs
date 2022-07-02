namespace Nino.Serialization
{
    public class Box<T> : Box
    {
        public new T Value;
    }

    public class Box
    {
        public object Value;
    }
}