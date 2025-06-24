public partial class EtcMathLib
{
    public static int FastRangeRandom(int seed, int a, int b)
    {
        unchecked
        {
            uint x = (uint)seed;
            x = (x ^ 61) ^ (x >> 16);
            x = x + (x << 3);
            x = x ^ (x >> 4);
            x = x * 0x27d4eb2d;
            x = x ^ (x >> 15);
            
            int range = b - a + 1;
            return a + (int)(x % range);
        }
    }

}