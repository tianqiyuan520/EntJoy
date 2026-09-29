using System;
using EntJoy.ECS;
using Xunit;

namespace EntJoy.ECS.Tests
{
    /// <summary>
    /// Observer 重入契约（审计 B13 / R1）：
    /// **回调内不得做结构变更**，框架必须拦下（而不是放行后把已释放的 chunk 内存交给后续回调）。
    ///
    /// 缺陷形状：重入保护此前只写在 `CompleteActiveJobs`，而逐实体结构变更（AddComponent/
    /// RemoveComponent/Set/DestroyEntity）走的是 `CompleteEntityJobs` —— 那条路径没有闸门，
    /// 而 `_structuralLock` 是可重入 Monitor，所以回调内的结构变更会真的执行：
    /// Added/Set 派发把指向 chunk 组件列的指针以 ReadOnlySpan 交给回调，observer #1 一旦迁移/销毁，
    /// observer #2 就读到已归还 slab 的内存（use-after-free）。
    /// </summary>
    public class ObserverReentrancyTests
    {
        private struct Tag : IComponentData { public int V; }

        [Fact]
        public void StructuralChangeInsideObserverCallback_MustBeRejected()
        {
            using var world = new World("ObsReentry" + Guid.NewGuid().ToString("N"));
            var em = world.EntityManager;

            var target = em.NewEntity(typeof(Position));
            bool innerRejected = false;

            world.AddObserver<Tag>(ObserverEvents.Added, (entities, values) =>
            {
                // 回调内做结构变更 → 契约要求被拦下
                try
                {
                    em.AddComponent<Tag>(target, new Tag { V = 7 });
                }
                catch (InvalidOperationException)
                {
                    innerRejected = true; // 拦截成功（吞掉，便于断言而不依赖派发层异常策略）
                }
            });

            // 触发 Added 派发
            em.AddComponent<Tag>(target, new Tag { V = 1 });

            Assert.True(innerRejected,
                "observer 回调内的结构变更必须抛 InvalidOperationException（否则后续 observer 可能读到已释放 chunk 内存）");
        }

        [Fact]
        public void StructuralChangeInsideObserverCallback_OutsideCallbackStillWorks()
        {
            // 反向守卫：闸门只对「回调内」生效，回调结束后结构变更必须恢复正常
            using var world = new World("ObsReentry2" + Guid.NewGuid().ToString("N"));
            var em = world.EntityManager;

            var e = em.NewEntity(typeof(Position));
            int calls = 0;
            world.AddObserver<Tag>(ObserverEvents.Added, (entities, values) => calls++);

            em.AddComponent<Tag>(e, new Tag { V = 1 });   // 派发 1 次
            em.AddComponent<Tag>(e, new Tag { V = 2 });   // 覆盖写：Set 桶未订阅 Added → 不派发
            Assert.Equal(1, calls);
            Assert.Equal(2, em.GetComponent<Tag>(e).V);  // 结构变更在回调外照常生效
        }
    }
}
