using System;
using EntJoy.ECS;
using Xunit;

namespace EntJoy.ECS.Tests
{
    /// <summary>
    /// slab 账本不变式：**复用「空洞 chunk」后，含活 chunk 的 slab 绝不能被整块归还**。
    ///
    /// 背景（审计 B4）：`Archetype.ReleaseChunkMemory` 用 `ReleasedCount == ChunkCount` 判定
    /// 「该 slab 的所有 chunk 都已释放 → 整块归还给全局池」。而 `AllocateFromSlab` 的空洞复用路径
    /// 曾经直接 `return reused;` 而不把 `ReleasedCount` 还回去 —— 活 chunk 被记成「已释放」，
    /// 于是等式会提前成立：整块 slab（**含仍然存活的 chunk**）被归还，Chunk/EntityInfo 变悬空指针
    /// （UAF），该内存也会被下一个 Archetype/World 复用（跨实体数据别名）。
    ///
    /// 本用例按普通公开 API（CreateEntities/DestroyEntity）复现该场景，并用内部不变式自检
    /// `Archetype.VerifySlabInvariants()` 做判定（不能靠「读到的值没变」——被归还的内存可能还没被覆盖）。
    /// </summary>
    public class SlabReuseInvariantTests
    {
        [Fact]
        public void HoleReuse_ThenAllOtherChunksFreed_MustNotFreeSlabHoldingLiveChunk()
        {
            int savedCapacity = Archetype.ChunkCapacityOverride;
            Archetype.ChunkCapacityOverride = 64; // 固定容量，让「一个 chunk 装 64 个实体」可预测
            try
            {
                using var world = new World("SlabInv" + Guid.NewGuid().ToString("N"));
                var em = world.EntityManager;
                const int C = 64;

                // chunk0 = first[0..63]（满），chunk1 = first[64]
                var first = em.CreateEntities(C + 1, typeof(Position));
                Assert.Equal(C + 1, first.Length);

                // 清空 chunk0 → 它的内存进入该 Archetype 的空洞列表（ReleasedCount=1 / ChunkCount=2）
                for (int i = 0; i < C; i++) em.DestroyEntity(first[i]);

                // 再填 C 个：chunk1 先被填满（63 个），剩下的 second[C-1] 只能从空洞分配 → 复用 chunk0 的内存
                var second = em.CreateEntities(C, typeof(Position));
                Assert.Equal(C, second.Length);

                // 给最终会存活的那个实体写哨兵值（数据完整性旁证）
                em.GetComponent<Position>(second[C - 1]).X = 123f;

                // 清空 chunk1（= first[C] + second[0..C-2]）：
                // 修复前此刻 ReleasedCount(2) == ChunkCount(2) → 整块 slab 被归还，
                // 而复用空洞的那个 chunk（second[C-1]）仍然活着。
                em.DestroyEntity(first[C]);
                for (int i = 0; i < C - 1; i++) em.DestroyEntity(second[i]);

                Archetype? arch = null;
                foreach (var a in em.GetAllArchetypes())
                {
                    if (a.ChunkCount > 0) { arch = a; break; }
                }
                Assert.NotNull(arch);

                // 1) 账本自检：悬空 chunk / ReleasedCount 失配 / 计数越界都算违例
                Assert.Equal(0, arch!.VerifySlabInvariants());

                // 2) 结构旁证：活实体仍在，且仍由 slab 承载
                Assert.Equal(1, arch.EntityCount);
                Assert.True(arch.SlabCount >= 1, "持有活 chunk 的 Archetype 不应把 slab 全部归还");

                // 3) 数据旁证
                Assert.Equal(123f, em.GetComponent<Position>(second[C - 1]).X);
            }
            finally
            {
                Archetype.ChunkCapacityOverride = savedCapacity;
            }
        }
    }
}
