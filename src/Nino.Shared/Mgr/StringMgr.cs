using System;

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
            if (str == null) return null;
            if (str.IsEmpty)
            {
                return Array.Empty<string>();
            }
            
            var indexes = stackalloc int[str.Length];
            var index = 0;
            for (int i = 0; i < str.Length; i++)
            {
                if (str[i] == separator)
                {
                    indexes[index++] = i;
                }
            }

            var entries = index + 1;
            string[] ret = new string[entries];
            
            int start = 0;
            index = 0;

            for (int i = 0; i < entries - 1; i++)
            {
                var len = indexes[i] - start;
                if(start >= str.Length || len == 0)
                {
                    ret[index++] = string.Empty;
                    start = indexes[i] + 1;
                    continue;
                }
                ret[index++] = str.Slice(start, len).ToString();
                start = indexes[i] + 1;
            }

            if (start < str.Length)
            {
                ret[index] = str.Slice(start).ToString();
            }
            else
            {
                ret[index] = string.Empty;
            }
            
            return ret;
        }
    }
}