//using EntJoy.Collections;
//using EntJoy.Mathematics;
//using System.Runtime.CompilerServices;

//namespace EntJoy.MovementTest
//{
//    /// <summary>
//    /// IJobParallelForBatch 版本的 MoveEntitiesJob。
//    /// 使用 UnsafeUtility 直接访问 NativeArray 缓冲区，绕过安全检查。
//    /// 适用于性能敏感场景。
//    /// </summary>
//    public struct MoveEntitiesJob_Batch : IJobParallelForBatch
//    {
//        public NativeArray<float2> Positions;
//        public NativeArray<float2> Velocities;
//        public float Dt;
//        public float ViewportWidth;
//        public float ViewportHeight;
//        public int Count;

//        public unsafe void Execute(int start, int count)
//        {
//            int end = start + count;
//            for (int i = start; i < end; i++)
//            {
//                float2 pos = UnsafeUtility.ReadArrayElement<float2>(Positions.GetUnsafePtr(), i);
//                float2 vel = UnsafeUtility.ReadArrayElement<float2>(Velocities.GetUnsafePtr(), i);

//                pos.x += vel.x * Dt;
//                pos.y += vel.y * Dt;

//                if (pos.x < 0f || pos.x > ViewportWidth) vel.x = -vel.x;
//                if (pos.y < 0f || pos.y > ViewportHeight) vel.y = -vel.y;

//                UnsafeUtility.WriteArrayElement(Positions.GetUnsafePtr(), i, pos);
//                UnsafeUtility.WriteArrayElement(Velocities.GetUnsafePtr(), i, vel);
//            }
//        }
//    }
//}
