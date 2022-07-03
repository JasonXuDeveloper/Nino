using Nino.Shared.IO;

namespace Nino.Serialization
{
    public class Box<T>
    {
        public T Value;

        public T RetrieveValueAndReturn()
        {
            T ret = Value;
            ObjectPool<Box<T>>.Return(this);
            return ret;
        }
    }
}