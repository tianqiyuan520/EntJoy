using EntJoy;
using Godot;
using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

public partial struct MoveSystemSIMD : ISystem<Position, Vel>
{
    public double dt;
    public Vector2 viewportSize;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public unsafe void _execute(Entity* entity, Position* pos, Vel* vel, int Count, int _Generated_LimitCount)
    {
        unchecked
        {
            UpdatePhysicsSIMD((nint)pos, (nint)vel, Count, viewportSize, (float)dt, _Generated_LimitCount);
        }
    }

    private unsafe void UpdatePhysicsSIMD(IntPtr posPtr, IntPtr velPtr, int count, Vector2 viewportSize, float delta, int LimitCount)
    {
        if (posPtr == IntPtr.Zero || velPtr == IntPtr.Zero)
            return;

        Vector2* positions = (Vector2*)posPtr.ToPointer();
        Vector2* velocities = (Vector2*)velPtr.ToPointer();

        int i = 0;
        if (Avx2.IsSupported && count >= 8)
        {
            // 创建常量向量（循环外）
            var deltaVec = Vector256.Create(delta);
            var zero = Vector256<float>.Zero;
            var signFlipMask = Vector256.Create(-0.0f); // 用于翻转符号的掩码: 0x80000000

            // 创建边界向量 - 格式: [viewportX, viewportY, viewportX, viewportY, ...]
            var boundsVec = Vector256.Create(
                viewportSize.X, viewportSize.Y,
                viewportSize.X, viewportSize.Y,
                viewportSize.X, viewportSize.Y,
                viewportSize.X, viewportSize.Y
            );

            // 添加小偏移量解决边界精度问题
            var epsilon = Vector256.Create(0.0001f);
            var minBounds = Avx.Subtract(zero, epsilon);
            var maxBounds = Avx.Add(boundsVec, epsilon);

            // 提前计算边界检查上限
            int batchSize = LimitCount > 0 ? Math.Min(count, LimitCount) : count;
            batchSize = batchSize - batchSize % 8; // 对齐到8的倍数

            for (; i < batchSize; i += 8)
            {
                // 1. 加载位置和速度
                var pos0 = Avx.LoadVector256((float*)(positions + i));
                var pos1 = Avx.LoadVector256((float*)(positions + i + 4));
                var vel0 = Avx.LoadVector256((float*)(velocities + i));
                var vel1 = Avx.LoadVector256((float*)(velocities + i + 4));

                // 2. 更新位置
                var newPos0 = Avx.Add(pos0, Avx.Multiply(vel0, deltaVec));
                var newPos1 = Avx.Add(pos1, Avx.Multiply(vel1, deltaVec));

                // 3. 边界检测（使用带偏移量的比较解决精度问题）
                var bounceMask0 = Avx.Or(
                    Avx.CompareLessThan(newPos0, minBounds),
                    Avx.CompareGreaterThan(newPos0, maxBounds)
                );

                var bounceMask1 = Avx.Or(
                    Avx.CompareLessThan(newPos1, minBounds),
                    Avx.CompareGreaterThan(newPos1, maxBounds)
                );

                // 4. 使用异或翻转速度符号（比乘法更高效）
                var newVel0 = Avx.BlendVariable(
                    vel0,
                    Avx.Xor(vel0, signFlipMask),
                    bounceMask0
                );

                var newVel1 = Avx.BlendVariable(
                    vel1,
                    Avx.Xor(vel1, signFlipMask),
                    bounceMask1
                );

                // 5. 存储结果
                Avx.Store((float*)(positions + i), newPos0);
                Avx.Store((float*)(positions + i + 4), newPos1);
                Avx.Store((float*)(velocities + i), newVel0);
                Avx.Store((float*)(velocities + i + 4), newVel1);
            }
        }
        //
        else if (Sse.IsSupported && count >= 4)
        {
            var deltaVec = Vector128.Create(delta);
            var viewportX = Vector128.Create(viewportSize.X);
            var viewportY = Vector128.Create(viewportSize.Y);
            var zero = Vector128<float>.Zero;
            var negativeOne = Vector128.Create(-1f);

            for (; i <= count - 4; i += 4)
            {
                if (LimitCount > 0 && i >= LimitCount) break;

                // 1. 加载位置和速度
                var posVec0 = Sse.LoadVector128((float*)(positions + i));
                var posVec1 = Sse.LoadVector128((float*)(positions + i + 2));
                var velVec0 = Sse.LoadVector128((float*)(velocities + i));
                var velVec1 = Sse.LoadVector128((float*)(velocities + i + 2));

                // 2. 提取 X 和 Y 分量
                // 位置 X 分量: [p0.X, p1.X, p2.X, p3.X]
                var posX = Sse.Shuffle(posVec0, posVec1, 0b1000_1000);
                // 位置 Y 分量: [p0.Y, p1.Y, p2.Y, p3.Y]
                var posY = Sse.Shuffle(posVec0, posVec1, 0b1101_1101);

                // 速度 X 分量: [v0.X, v1.X, v2.X, v3.X]
                var velX = Sse.Shuffle(velVec0, velVec1, 0b1000_1000);
                // 速度 Y 分量: [v0.Y, v1.Y, v2.Y, v3.Y]
                var velY = Sse.Shuffle(velVec0, velVec1, 0b1101_1101);

                // 3. 更新位置
                posX = Sse.Add(posX, Sse.Multiply(velX, deltaVec));
                posY = Sse.Add(posY, Sse.Multiply(velY, deltaVec));

                // 4. X 方向边界检测
                var xMinMask = Sse.CompareLessThan(posX, zero);
                var xMaxMask = Sse.CompareGreaterThan(posX, viewportX);
                var xBounceMask = Sse.Or(xMinMask, xMaxMask);

                // 5. Y 方向边界检测
                var yMinMask = Sse.CompareLessThan(posY, zero);
                var yMaxMask = Sse.CompareGreaterThan(posY, viewportY);
                var yBounceMask = Sse.Or(yMinMask, yMaxMask);

                // 6. 反弹速度 (X 和 Y 方向)
                var bounceVelX = Sse.Multiply(velX, negativeOne);
                var bounceVelY = Sse.Multiply(velY, negativeOne);

                // 使用掩码选择正确的值：反弹部分使用 bounceVel，其他部分保持原速度
                var allOnes = Vector128.Create(-1).AsSingle();

                // X 方向反弹
                var bouncePartX = Sse.And(bounceVelX, xBounceMask);
                var keepPartX = Sse.And(velX, Sse.AndNot(xBounceMask, allOnes));
                velX = Sse.Or(bouncePartX, keepPartX);

                // Y 方向反弹
                var bouncePartY = Sse.And(bounceVelY, yBounceMask);
                var keepPartY = Sse.And(velY, Sse.AndNot(yBounceMask, allOnes));
                velY = Sse.Or(bouncePartY, keepPartY);

                // 7. 重新组合位置数据
                var newPos0 = Sse.UnpackLow(posX, posY); // [p0.X, p0.Y, p1.X, p1.Y]
                var newPos1 = Sse.UnpackHigh(posX, posY); // [p2.X, p2.Y, p3.X, p3.Y]

                // 8. 重新组合速度数据
                var newVel0 = Sse.UnpackLow(velX, velY); // [v0.X, v0.Y, v1.X, v1.Y]
                var newVel1 = Sse.UnpackHigh(velX, velY); // [v2.X, v2.Y, v3.X, v3.Y]

                // 9. 存储结果
                Sse.Store((float*)(positions + i), newPos0);
                Sse.Store((float*)(positions + i + 2), newPos1);
                Sse.Store((float*)(velocities + i), newVel0);
                Sse.Store((float*)(velocities + i + 2), newVel1);
            }
        }
        //

        // 标量处理剩余实体
        for (; i < count; i++)
        {
            if (LimitCount > 0 && i >= LimitCount) break;
            ref var pos = ref positions[i];
            ref var vel = ref velocities[i];
            pos.X += vel.X * delta;
            pos.Y += vel.Y * delta;

            if (pos.X < 0 || pos.X > viewportSize.X) vel.X = -vel.X;

            if (pos.Y < 0 || pos.Y > viewportSize.Y) vel.Y = -vel.Y;
        }
    }
}