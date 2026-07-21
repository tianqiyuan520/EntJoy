using EntJoySample.HintLikelyTest;

namespace EntJoySample.HintLikelyTest
{
    public class Program
    {
        public static void Main()
        {
            HintLikely.Run();
            HintLikelyBenchmark.Run();
            HintLikelyUopCacheTest.Run();
        }
    }
}
