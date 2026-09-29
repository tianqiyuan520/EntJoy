using System;
using EntJoy.ECS;
using Xunit;

namespace EntJoy.ECS.Tests
{
    /// <summary>
    /// `EntityManager.CompactChunks` 的前置条件（审计 B7）：
    /// 只有当瘦 Chunk 的实体**全部**搬走后才能移除该 Chunk。
    ///
    /// 缺陷形状：`CompactArchetype` 调 `MoveEntitiesTo` 后**无条件** `RemoveEmptyChunkAt`，
    /// 而 `MoveEntitiesTo` 在「thin 前面的 Chunk 都满了」时会提前 `break`（thin 仍有实体）。
    /// 于是被移除的 Chunk 仍承载实体：其 `EntityInfo`/定位表索引被 swap-pop 打乱
    /// ——静默丢实体、把实体指向别的 Chunk，且其 IDisposable 组件的 Dispose 钩子永不执行。
    /// </summary>
    public class CompactChunksInvariantTests
    {
        [Fact]
        public void CompactChunks_WithInsufficientFrontSpace_MustNotDropLiveEntities()
        {
            int savedCapacity = Archetype.ChunkCapacityOverride;
            Archetype.ChunkCapacityOverride = 64;
            try
            {
                using var world = new World("Compact" + Guid.NewGuid().ToString("N"));
                var em = world.EntityManager;
                const int C = 64;

                // chunk0 = 64 个（满），chunk1 = 6 个（6/64 = 9.4% → 瘦 Chunk）
                var all = em.CreateEntities(C + 6, typeof(Position));
                Assert.Equal(C + 6, all.Length);

                // 让 chunk0 留出恰好 4 个空位（60/64）：搬移 4 次后前方不再有空间，thin 仍有 2 个实体
                for (int i = 0; i < 4; i++) em.DestroyEntity(all[i]);

                const int expectedLive = C + 6 - 4; // 66

                Archetype? arch = null;
                foreach (var a in em.GetAllArchetypes())
                {
                    if (a.ChunkCount > 0) { arch = a; break; }
                }
                Assert.NotNull(arch);

                int LiveInChunks()
                {
                    int sum = 0;
                    for (int i = 0; i < arch!.ChunkList.Count; i++)
                        sum += arch.ChunkList[i].EntityCount;
                    return sum;
                }

                Assert.Equal(expectedLive, LiveInChunks());

                em.CompactChunks();

                // 1) 结构不变式：活实体数不得因压缩而减少（修复前 = 64，丢掉 2 个）
                Assert.Equal(expectedLive, LiveInChunks());

                // 2) 承载实体的 Chunk 不得被移除（修复前 ChunkCount 变成 1）
                Assert.True(arch!.ChunkCount >= 2,
                    "前方空间不足时 thin chunk 仍承载实体，不应被移除");

                // 3) 每个实体句柄仍必须能解析到合法槽位（未被指向已移除的 Chunk）
                for (int i = 4; i < all.Length; i++)
                {
                    var e = all[i];
                    _ = em.GetComponent<Position>(e);
                }
            }
            finally
            {
                Archetype.ChunkCapacityOverride = savedCapacity;
            }
        }
    }
}
