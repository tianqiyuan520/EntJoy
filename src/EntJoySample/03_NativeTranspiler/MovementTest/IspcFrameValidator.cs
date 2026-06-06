using EntJoy.Collections;
using EntJoy.Mathematics;
using EntJoy.JobSystem;
using System.Diagnostics;

namespace EntJoy.MovementTest
{
    /// <summary>
    /// 独立 ISPC 帧循环正确性验证器。
    /// 只运行 ISPC 和标量，确保数据流完全独立。
    /// </summary>
    public class IspcFrameValidator
    {
        private const int ENTITY_COUNT = 100_000;  // 用 10w 加速调试
        private const float VIEWPORT_WIDTH = 1920f;
        private const float VIEWPORT_HEIGHT = 1080f;
        private const float DT = 0.016f;
        private const int FRAMES = 10;
        private const int FRAME_INTERVAL_MS = 16;

        public static void Run()
        {
            Console.WriteLine("\n========== ISPC 帧循环独立验证 ==========");
            
            // 1. 生成初始数据（两份独立副本）
            var initialPositions = new NativeArray<float2>(ENTITY_COUNT, Allocator.Persistent);
            var initialVelocities = new NativeArray<float2>(ENTITY_COUNT, Allocator.Persistent);
            MoveEntitiesTest.GenerateInitialData(initialPositions, initialVelocities, ENTITY_COUNT, seed: 42);

            // ISPC 副本
            var ispcPositions = new NativeArray<float2>(ENTITY_COUNT, Allocator.Persistent);
            var ispcVelocities = new NativeArray<float2>(ENTITY_COUNT, Allocator.Persistent);
            initialPositions.CopyTo(ispcPositions);
            initialVelocities.CopyTo(ispcVelocities);

            // 标量副本
            var scalarPositions = new NativeArray<float2>(ENTITY_COUNT, Allocator.Persistent);
            var scalarVelocities = new NativeArray<float2>(ENTITY_COUNT, Allocator.Persistent);
            initialPositions.CopyTo(scalarPositions);
            initialVelocities.CopyTo(scalarVelocities);

            // 2. 逐帧调用，每帧同时跑标量和 ISPC，然后对比
            Console.WriteLine($"实体数: {ENTITY_COUNT:N0}, 帧数: {FRAMES}");
            Console.WriteLine();
            
            for (int frame = 0; frame < FRAMES; frame++)
            {
                // 标量
                MoveEntitiesTest.RunScalar(scalarPositions, scalarVelocities, ENTITY_COUNT);
                
                // ISPC
                MoveEntitiesTest.RunNativeIspc(ispcPositions, ispcVelocities, ENTITY_COUNT);

                // 对比
                float maxDiff = 0f;
                int mismatchCount = 0;
                const float EPSILON = 1e-4f;
                for (int i = 0; i < ENTITY_COUNT; i++)
                {
                    float dx = Math.Abs(scalarPositions[i].x - ispcPositions[i].x);
                    float dy = Math.Abs(scalarPositions[i].y - ispcPositions[i].y);
                    float diff = Math.Max(dx, dy);
                    if (diff > maxDiff) maxDiff = diff;
                    if (diff > EPSILON)
                    {
                        mismatchCount++;
                        if (mismatchCount <= 3)
                        {
                            Console.WriteLine($"  帧{frame}: Entity {i}: scalar({scalarPositions[i].x:F4},{scalarPositions[i].y:F4}) ispc({ispcPositions[i].x:F4},{ispcPositions[i].y:F4}) diff={diff:E4}");
                        }
                    }
                }

                if (mismatchCount == 0)
                {
                    Console.WriteLine($"  帧{frame}: ✓ 完全正确！maxDiff={maxDiff:E4}");
                }
                else
                {
                    Console.WriteLine($"  帧{frame}: ✗ 错误！{mismatchCount}/{ENTITY_COUNT} 不匹配, maxDiff={maxDiff:E4}");
                }

                Thread.Sleep(FRAME_INTERVAL_MS);
            }

            // 清理
            initialPositions.Dispose();
            initialVelocities.Dispose();
            ispcPositions.Dispose();
            ispcVelocities.Dispose();
            scalarPositions.Dispose();
            scalarVelocities.Dispose();
        }
    }
}
