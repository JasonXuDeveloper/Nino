using System;

namespace Nino.Shared
{
    public static class PowerOf2
    {
        /// <summary>
        /// Get whether or not n is power of 2
        /// </summary>
        /// <param name="n"></param>
        /// <returns></returns>
        public static bool IsPowerOf2(int n)
        {
            /*
             * 4 -> 100
             * 3 -> 011
             * 4 & 3 -> 000
             *
             * 8 -> 1000
             * 7 -> 0111
             * 8 & 7 -> 0000
             *
             * 15 -> 1111
             * 14 -> 1110
             * 15 & 14 -> 1110
             */
            return (n & (n - 1)) == 0;
        }

        /// <summary>
        /// if n is power of 2, get its power
        /// </summary>
        /// <param name="n"></param>
        /// <returns></returns>
        public static byte GetPower(int n)
        {
            byte ret = 0;
            while (n >> 1 != 0)
            {
                ret++;
                n = n >> 1;
            }

            return ret;
        }

        /// <summary>
        /// if n is not power of 2, get the next power of 2 from n
        /// </summary>
        /// <param name="n"></param>
        /// <returns></returns>
        /// <exception cref="ArgumentOutOfRangeException"></exception>
        public static int RoundUpToPowerOf2(int n)
        {
            if (n < 0 || n > 0x40000000)
            {
                throw new ArgumentOutOfRangeException("n");
            }

            n = n - 1;
            n = n | (n >> 1);
            n = n | (n >> 2);
            n = n | (n >> 4);
            n = n | (n >> 8);
            n = n | (n >> 16);
            return n + 1;
        }
    }
}