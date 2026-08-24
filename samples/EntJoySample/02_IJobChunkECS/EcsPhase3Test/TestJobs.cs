using EntJoy;
using EntJoy.JobSystem;

namespace EntJoySample.EcsPhase3Test
{
    /// <summary>
    /// 移动 Job：读写 Position，读 Velocity
    /// </summary>
    public struct MoveJob : IJobChunk
    {
        public float DeltaTime;

        public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
        {
            var positions = chunk.GetComponentDataSpan<Position>();
            var velocities = chunk.GetComponentDataSpan<Velocity>();

            for (int i = 0; i < positions.Length; i++)
            {
                positions[i].X += velocities[i].X * DeltaTime;
                positions[i].Y += velocities[i].Y * DeltaTime;
            }
        }
    }

    /// <summary>
    /// 伤害 Job：读写 Health
    /// 模拟重计算（用于验证 Selective Wait）
    /// </summary>
    public struct DamageJob : IJobChunk
    {
        public float DamageAmount;
        public int Iterations;

        public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
        {
            var healths = chunk.GetComponentDataSpan<Health>();

            for (int i = 0; i < healths.Length; i++)
            {
                // 模拟重计算
                float damage = DamageAmount;
                for (int j = 0; j < Iterations; j++)
                {
                    damage = damage * 0.99f + 0.001f;
                }
                healths[i].Current -= damage;
                if (healths[i].Current < 0)
                    healths[i].Current = 0;
            }
        }
    }

    /// <summary>
    /// 护甲 Job：读写 Armor
    /// 用于验证不同 Archetype 的 Job 互不干扰
    /// </summary>
    public struct ArmorJob : IJobChunk
    {
        public float Bonus;

        public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
        {
            var armors = chunk.GetComponentDataSpan<Armor>();

            for (int i = 0; i < armors.Length; i++)
            {
                armors[i].Value += Bonus;
            }
        }
    }
}
