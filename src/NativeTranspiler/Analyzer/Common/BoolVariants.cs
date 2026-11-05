using System.Collections.Generic;
using System.Linq;

namespace NativeTranspiler.Analyzer.Common
{
    internal static class BoolVariants
    {
        public static IEnumerable<(int Mask, List<bool> Values)> Enumerate(int count)
        {
            for (int mask = 0; mask < (1 << count); mask++)
            {
                var values = new List<bool>(count);
                for (int bit = 0; bit < count; bit++)
                    values.Add((mask & (1 << bit)) != 0);
                yield return (mask, values);
            }
        }

        public static string Suffix(IEnumerable<bool> values)
        {
            string suffix = string.Join("_", values.Select(value => value ? "true" : "false"));
            return suffix.Length == 0 ? "" : "_" + suffix;
        }
    }
}
