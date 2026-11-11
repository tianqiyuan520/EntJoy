using System;
using EntJoy.ECS;
using Xunit;

namespace EntJoy.ECS.Tests
{
    /// <summary>
    /// 对抗「无序遍历哈希 + 无序多重集比较」这一对契约：哈希必须与相等判定同号。
    /// 直接构造任意 Id 的 <see cref="ComponentType"/>，不依赖组件注册顺序。
    /// </summary>
    public class UnorderedHashContractTests
    {
        private static ComponentType[] Ids(params int[] ids)
        {
            var types = new ComponentType[ids.Length];
            for (int i = 0; i < ids.Length; i++) types[i] = new ComponentType(ids[i]);
            return types;
        }

        private static int Hash(params int[] ids) => Utils.CalculateUnorderedHash(Ids(ids));

        private static bool SetEquals(int[] a, int[] b) => Utils.SetEquals(Ids(a), Ids(b));

        [Fact]
        public void Hash_IsOrderIndependentAndMatchesEquality()
        {
            int[] canonical = { 7, 3, 11, 5 };
            int expected = Hash(canonical);
            var permutations = new[]
            {
                new[] { 3, 7, 5, 11 },
                new[] { 11, 5, 3, 7 },
                new[] { 5, 11, 7, 3 },
            };
            foreach (var permutation in permutations)
            {
                Assert.True(SetEquals(canonical, permutation));
                Assert.Equal(expected, Hash(permutation));
            }
        }

        [Fact]
        public void SetEquals_IsMultisetComparison()
        {
            Assert.True(SetEquals(new[] { 1, 1, 2 }, new[] { 2, 1, 1 }));
            Assert.False(SetEquals(new[] { 1, 1, 2 }, new[] { 1, 2, 2 }));
            Assert.False(SetEquals(new[] { 1, 2 }, new[] { 1, 2, 3 }));
            Assert.False(SetEquals(new[] { 1, 2 }, new[] { 1 }));
            Assert.True(SetEquals(Array.Empty<int>(), Array.Empty<int>()));
        }

        [Fact]
        public void SetEquals_LargeSets_UseCountingPath()
        {
            // 超过 16 项才走 stackalloc 计数分支；两种分支必须等价。
            var small = new int[16];
            var large = new int[20];
            for (int i = 0; i < small.Length; i++) small[i] = i;
            for (int i = 0; i < large.Length; i++) large[i] = i;

            Assert.True(SetEquals(small, small));
            Assert.True(SetEquals(large, large));
            Assert.Equal(Hash(large), Hash(large));

            var reordered = (int[])large.Clone();
            Array.Reverse(reordered);
            Assert.True(SetEquals(large, reordered));
            Assert.Equal(Hash(large), Hash(reordered));

            var withDuplicate = (int[])large.Clone();
            withDuplicate[19] = withDuplicate[18];
            Assert.False(SetEquals(large, withDuplicate));
        }

        [Fact]
        public void Hash_DistinguishesSetsWithEqualIdSum()
        {
            // 反例：旧实现是 Σ(id·k)，{1,4} 与 {2,3} 的 id 和相等 ⇒ 哈希恒等 ⇒ 每次查找走线性兜底。
            // 终混之后两者必须给出不同哈希（相等判定照旧为 false）。
            Assert.False(SetEquals(new[] { 1, 4 }, new[] { 2, 3 }));
            Assert.NotEqual(Hash(1, 4), Hash(2, 3));

            Assert.False(SetEquals(new[] { 5, 2, 7 }, new[] { 1, 6, 7 }));
            Assert.NotEqual(Hash(5, 2, 7), Hash(1, 6, 7));
        }

        [Fact]
        public void Hash_DistinguishesEmptySetFromSingleIdZero()
        {
            // Id 0 是 splitmix32 的不动点（Mix(0) == 0）；集合长度先混入，空集与 {0} 仍可区分。
            Assert.False(SetEquals(Array.Empty<int>(), new[] { 0 }));
            Assert.NotEqual(Hash(), Hash(0));
        }
    }
}
