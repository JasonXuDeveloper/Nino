using System;
using System.Collections.Generic;

namespace Nino.Shared.Mgr
{
    public static class StringMgr
    {
        public static List<string> Split(this ReadOnlySpan<char> str, char separator, StringSplitOptions options = StringSplitOptions.None)
        {
            if (str == null) return null;
            List<string> ret = new List<string>();
            if (str.IsEmpty)
            {
                return ret;
            }
            int start = 0;
            int pos = -1;
            while (++pos < str.Length)
            {
                if (str[pos] == separator)
                {
                    var end = pos;
                    if (start < str.Length && end < str.Length && end > start)
                    {
                        ret.Add(str.Slice(start, end - start).ToString());
                    }
                    else if(options != StringSplitOptions.RemoveEmptyEntries)
                    {
                        ret.Add(string.Empty);
                    }
                        
                    start = pos + 1;
                }
            }

            if (start == str.Length)
            {
                if (options == StringSplitOptions.RemoveEmptyEntries)
                {
                    return ret;
                }
            }
            ret.Add(str.Slice(start).ToString());
            return ret;
        }
    }
}