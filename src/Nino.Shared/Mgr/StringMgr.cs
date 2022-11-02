using System;
using Nino.Shared.IO;

namespace Nino.Shared.Mgr
{
    public static class StringMgr
    {
        /// <summary>
        /// Use Span to optimize string split
        /// Useless since .net core 6.0, but useful for .net framework
        /// </summary>
        /// <param name="str"></param>
        /// <param name="separator"></param>
        /// <returns></returns>
        public static unsafe string[] Split(this ReadOnlySpan<char> str, char separator)
        {
            if (str.IsEmpty)
            {
                return Array.Empty<string>();
            }

            var indexes = ObjectPool<ExtensibleBuffer<int>>.Request();
            var index = 0;
            int i = 0;
            int max = str.Length;
            fixed (char* ptr = &str.GetPinnableReference())
            {
                var cPtr = ptr;
                while (i < max)
                {
                    if (*cPtr++ == separator)
                    {
                        indexes[index++] = i;
                    }

                    i++;
                }
            }
            
            string[] ret = new string[index + 1];
            int start = 0;

            for (i = 0; i < index; i++)
            {
                ref int end = ref indexes.Data[i];
                if(start >= max || start == end)
                {
                    ret[i] = string.Empty;
                }
                else
                {
                    ret[i] = str.Slice(start, end - start).ToString();
                }
                start = end + 1;
            }

            if (start < max)
            {
                ret[index] = str.Slice(start).ToString();
            }
            else
            {
                ret[index] = string.Empty;
            }
            
            ObjectPool<ExtensibleBuffer<int>>.Return(indexes);
            return ret;
        }
    }
}