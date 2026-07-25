using EntJoy;
using EntJoy.Collections;
using EntJoy.Mathematics;

namespace EntJoySample.AutoSIMDTest
{
    // ── Component types ──
    public struct MovePosition : IComponentData { public float2 Value; }
    public struct MoveVelocity : IComponentData { public float2 Value; }

    // ────────────────────────────────────────────
    // Light IJobChunk: Position += Velocity * dt
    // ────────────────────────────────────────────

    [NativeTranspiler.NativeTranspile]
    public struct MoveJobChunk_Cpp : IJobChunk
    {
        public float DeltaTime;

        public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
        {
            NativeArray<MovePosition> positions = chunk.GetComponentDataNativeArray<MovePosition>();
            NativeArray<MoveVelocity> velocities = chunk.GetComponentDataNativeArray<MoveVelocity>();

            for (int index = 0; index < positions.Length; index++)
            {
                MovePosition position = positions[index];
                position.Value += velocities[index].Value * DeltaTime;
                positions[index] = position;
            }
        }
    }

    [NativeTranspiler.NativeTranspile(AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
    public struct MoveJobChunk_SIMD : IJobChunk
    {
        public float DeltaTime;

        public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
        {
            NativeArray<MovePosition> positions = chunk.GetComponentDataNativeArray<MovePosition>();
            NativeArray<MoveVelocity> velocities = chunk.GetComponentDataNativeArray<MoveVelocity>();

            for (int index = 0; index < positions.Length; index++)
            {
                MovePosition position = positions[index];
                position.Value += velocities[index].Value * DeltaTime;
                positions[index] = position;
            }
        }
    }

    // ────────────────────────────────────────────
    // Light IJobEntity: Position += Velocity * dt
    // ────────────────────────────────────────────

    [NativeTranspiler.NativeTranspile]
    public struct MoveJobEntity_Cpp : IJobEntity
    {
        public float DeltaTime;

        public void Execute(ref MovePosition position, in MoveVelocity velocity)
        {
            position.Value += velocity.Value * DeltaTime;
        }
    }

    [NativeTranspiler.NativeTranspile(AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
    public struct MoveJobEntity_SIMD : IJobEntity
    {
        public float DeltaTime;

        public void Execute(ref MovePosition position, in MoveVelocity velocity)
        {
            position.Value += velocity.Value * DeltaTime;
        }
    }

    // ────────────────────────────────────────────
    // Heavy IJobChunk: complex math inner loop
    // ────────────────────────────────────────────

    [NativeTranspiler.NativeTranspile]
    public struct HeavyJobChunk_Cpp : IJobChunk
    {
        public float DeltaTime;

        public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
        {
            NativeArray<MovePosition> positions = chunk.GetComponentDataNativeArray<MovePosition>();
            NativeArray<MoveVelocity> velocities = chunk.GetComponentDataNativeArray<MoveVelocity>();

            for (int index = 0; index < positions.Length; index++)
            {
                MovePosition position = positions[index];
                MoveVelocity velocity = velocities[index];

                float px = position.Value.x;
                float py = position.Value.y;
                float vx = velocity.Value.x;
                float vy = velocity.Value.y;
                float accX = px * 0.001f + vx * 0.01f;
                float accY = py * 0.001f + vy * 0.01f;

                for (int iteration = 0; iteration < 16; iteration++)
                {
                    float phaseX = accX + iteration * 0.03125f;
                    float phaseY = accY - iteration * 0.0625f;
                    float wave = MathF.Sin(phaseX) + MathF.Cos(phaseY);
                    float radius = MathF.Sqrt(accX * accX + accY * accY + 1.0f);
                    accX = accX * 0.985f + wave * 0.015f + radius * 0.0002f + vx * 0.0001f;
                    accY = accY * 0.982f - wave * 0.012f + radius * 0.0003f + vy * 0.0001f;
                }

                position.Value.x = px + vx * DeltaTime + accX * 0.001f;
                position.Value.y = py + vy * DeltaTime + accY * 0.001f;
                positions[index] = position;
            }
        }
    }

    [NativeTranspiler.NativeTranspile(AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
    public struct HeavyJobChunk_SIMD : IJobChunk
    {
        public float DeltaTime;

        public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
        {
            NativeArray<MovePosition> positions = chunk.GetComponentDataNativeArray<MovePosition>();
            NativeArray<MoveVelocity> velocities = chunk.GetComponentDataNativeArray<MoveVelocity>();

            for (int index = 0; index < positions.Length; index++)
            {
                MovePosition position = positions[index];
                MoveVelocity velocity = velocities[index];

                float px = position.Value.x;
                float py = position.Value.y;
                float vx = velocity.Value.x;
                float vy = velocity.Value.y;
                float accX = px * 0.001f + vx * 0.01f;
                float accY = py * 0.001f + vy * 0.01f;

                for (int iteration = 0; iteration < 16; iteration++)
                {
                    float phaseX = accX + iteration * 0.03125f;
                    float phaseY = accY - iteration * 0.0625f;
                    float wave = MathF.Sin(phaseX) + MathF.Cos(phaseY);
                    float radius = MathF.Sqrt(accX * accX + accY * accY + 1.0f);
                    accX = accX * 0.985f + wave * 0.015f + radius * 0.0002f + vx * 0.0001f;
                    accY = accY * 0.982f - wave * 0.012f + radius * 0.0003f + vy * 0.0001f;
                }

                position.Value.x = px + vx * DeltaTime + accX * 0.001f;
                position.Value.y = py + vy * DeltaTime + accY * 0.001f;
                positions[index] = position;
            }
        }
    }

    // ────────────────────────────────────────────
    // Heavy IJobEntity: complex math inner loop
    // ────────────────────────────────────────────

    [NativeTranspiler.NativeTranspile]
    public struct HeavyJobEntity_Cpp : IJobEntity
    {
        public float DeltaTime;

        public void Execute(ref MovePosition position, in MoveVelocity velocity)
        {
            float px = position.Value.x;
            float py = position.Value.y;
            float vx = velocity.Value.x;
            float vy = velocity.Value.y;
            float accX = px * 0.001f + vx * 0.01f;
            float accY = py * 0.001f + vy * 0.01f;

            for (int iteration = 0; iteration < 16; iteration++)
            {
                float phaseX = accX + iteration * 0.03125f;
                float phaseY = accY - iteration * 0.0625f;
                float wave = MathF.Sin(phaseX) + MathF.Cos(phaseY);
                float radius = MathF.Sqrt(accX * accX + accY * accY + 1.0f);
                accX = accX * 0.985f + wave * 0.015f + radius * 0.0002f + vx * 0.0001f;
                accY = accY * 0.982f - wave * 0.012f + radius * 0.0003f + vy * 0.0001f;
            }

            position.Value.x = px + vx * DeltaTime + accX * 0.001f;
            position.Value.y = py + vy * DeltaTime + accY * 0.001f;
        }
    }

    [NativeTranspiler.NativeTranspile(AutoSIMD = NativeTranspiler.AutoSIMD.Enabled)]
    public struct HeavyJobEntity_SIMD : IJobEntity
    {
        public float DeltaTime;

        public void Execute(ref MovePosition position, in MoveVelocity velocity)
        {
            float px = position.Value.x;
            float py = position.Value.y;
            float vx = velocity.Value.x;
            float vy = velocity.Value.y;
            float accX = px * 0.001f + vx * 0.01f;
            float accY = py * 0.001f + vy * 0.01f;

            for (int iteration = 0; iteration < 16; iteration++)
            {
                float phaseX = accX + iteration * 0.03125f;
                float phaseY = accY - iteration * 0.0625f;
                float wave = MathF.Sin(phaseX) + MathF.Cos(phaseY);
                float radius = MathF.Sqrt(accX * accX + accY * accY + 1.0f);
                accX = accX * 0.985f + wave * 0.015f + radius * 0.0002f + vx * 0.0001f;
                accY = accY * 0.982f - wave * 0.012f + radius * 0.0003f + vy * 0.0001f;
            }

            position.Value.x = px + vx * DeltaTime + accX * 0.001f;
            position.Value.y = py + vy * DeltaTime + accY * 0.001f;
        }
    }
}
