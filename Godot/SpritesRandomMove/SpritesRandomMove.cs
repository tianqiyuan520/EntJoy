using EntJoy;
using Godot;
using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

public struct Position : IComponent
{
    public Vector2 pos;
}

public struct Vel : IComponent
{
    public Vector2 vel;
}



public partial struct DisplayLimitedEntityPosSystem : ISystem<Position>
{
    public int EntityCount;

    public void Execute(ref Entity entity, ref Position pos)
    {
        GD.Print(entity.Id, "/", EntityCount, " ", pos.pos);
    }
}

public partial struct GetAllEntityPosSystem : ISystem<Position>
{
    public List<Position> poses;
    public void Execute(ref Entity entity, ref Position pos)
    {
        poses.Add(pos);
    }
}

public unsafe partial struct MoveSystem : ISystem<Position,Vel>
{
    public Vector2 viewportSize;
    public float dt;

    public void Execute(ref Position pos,ref Vel vel)
    {
        //pos.pos.X += pos.vel.X * dt;
        //pos.pos.Y += pos.vel.Y * dt;
        ////pos.pos += vel.vel * dt;
        //if (pos.pos.X < 0 || pos.pos.X > viewportSize.X) pos.vel.X *= -1;
        //if (pos.pos.Y < 0 || pos.pos.Y > viewportSize.Y) pos.vel.Y *= -1;
        pos.pos.X += vel.vel.X * dt;
        pos.pos.Y += vel.vel.Y * dt;
        //pos.pos += vel.vel * dt;
        if (pos.pos.X < 0 || pos.pos.X > viewportSize.X) vel.vel.X *= -1;
        if (pos.pos.Y < 0 || pos.pos.Y > viewportSize.Y) vel.vel.Y *= -1;
    }
}

public partial class SpritesRandomMove : Node2D
{
    [Export]
    Node MultiMeshgroup;
    [Export]
    PackedScene packedScene;

    int SpawnMultiMeshCount = 1;


    public World myWorld;

    public MultiMeshInstance2D[] multiMeshInstances;
    private Rect2 viewportRect;
    const int GODOT_FLOATS_PER_INSTANCE = 8;
    // 每个MultiMesh的实体数量
    public int ENTITIES_PER_MESH = 10_0000;
    public int EntityCount = 100_0000;
    public bool isPaused = false;

    public MultiMeshInstance2D GenerateMultiMesh()
    {
        var multiMeshInstance2D = packedScene.Instantiate<MultiMeshInstance2D>();
        MultiMeshgroup.AddChild(multiMeshInstance2D);
        return multiMeshInstance2D;
    }

    public override void _Ready()
    {
        GetNode("CanvasLayer/HBoxContainer").GetNode<Button>("CreateWorld").Pressed += CreateWorld;
        GetNode("CanvasLayer/HBoxContainer").GetNode<Button>("CreateEntity").Pressed += NewEntity;
        GetNode("CanvasLayer/HBoxContainer").GetNode<Button>("PrintEntity").Pressed += Display;
        GetNode("CanvasLayer/HBoxContainer").GetNode<Button>("Report").Pressed += Report;
        GetNode("CanvasLayer/HBoxContainer").GetNode<Button>("Pause").Pressed += Pause;
        //multiMeshInstance = GetNode<MultiMeshInstance2D>("MultiMeshInstance2D");
        //multiMeshInstance.Multimesh.InstanceCount = 30000;

        multiMeshInstances = new MultiMeshInstance2D[SpawnMultiMeshCount];

        for (int i = 0; i < SpawnMultiMeshCount; i++)
        {
            multiMeshInstances[i] = GenerateMultiMesh();
        }
        //if (SpawnMultiMeshCount != 0) ENTITIES_PER_MESH = EntityCount / SpawnMultiMeshCount;
        // 初始化所有MultiMesh实例
        for (int i = 0; i < SpawnMultiMeshCount; i++)
        {
            multiMeshInstances[i].Multimesh.InstanceCount = ENTITIES_PER_MESH;
        }
        GD.Print(multiMeshInstances.Length, " ", "ENTITIES_PER_MESH:", ENTITIES_PER_MESH, " ", "EntityCount:", EntityCount);
        viewportRect = GetViewportRect();

        //moveSystemSIMD.viewportSize = viewportRect.Size;
    }

    public void CreateWorld()
    {
        myWorld = new World();
        World_Recorder.RecordWorld(myWorld);
        GD.Print($"创建世界成功; 当前世界总数:{World_Recorder.worldList.Count}");
    }


    public void NewEntity()
    {
        for (int i = 0; i < EntityCount; i++)
        {
            var entity = myWorld.NewEntity(typeof(Position),typeof(Vel));
            myWorld.AddComponent(entity, new Position()
            {
                pos = new Vector2(100, 100),
                //vel = new Vector2
                //(
                //    (float)GD.RandRange(100.0, 200.0),
                //    (float)GD.RandRange(-200.0, 200.0)
                //)
            });
            myWorld.AddComponent(entity, new Vel()
            {
                vel = new Vector2
                (
                    (float)GD.RandRange(100.0, 200.0),
                    (float)GD.RandRange(-200.0, 200.0)
                )
            });
        }
        GD.Print($"NewEntity Success ");

        RenderingInit();
    }

    public QueryBuilder queryBuilder = new QueryBuilder().WithAll<Position>();
    public QueryBuilder queryBuilder2 = new QueryBuilder().WithAll<Position>();

    public override void _PhysicsProcess(double delta)
    {

        if (myWorld == null)
        {
            return;
        }
        if (!isPaused)
            TickLoop(delta);
    }
    public MoveSystem moveSystem = new();

    public MoveSystemSIMD moveSystemSIMD = new MoveSystemSIMD();

    double time = 0;
    double time2 = 0;
    int count = 0;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void TickLoop(double delta)
    {
        PerformanceDetection.Start();

        moveSystem.viewportSize = viewportRect.Size;
        moveSystem.dt = (float)delta;
        myWorld.Query(queryBuilder, moveSystem);


        //moveSystemSIMD.viewportSize = viewportRect.Size;
        //moveSystemSIMD.dt = (float)delta;
        //myWorld.MultiQuery(queryBuilder, moveSystemSIMD);


        //
        PerformanceDetection.End();
        double t = PerformanceDetection.GetAverage();
        PerformanceDetection.Start();
        DisplaySprites();
        PerformanceDetection.End();
        double t2 = PerformanceDetection.GetAverage();

        time += t;
        time2 += t2;
        count++;

        if (Engine.GetPhysicsFrames() % 60 == 0)
        {
            GD.Print($"逻辑平均耗时: {time / count}ms 共{count}次");
            GD.Print($"-渲染平均耗时: {time2 / count}ms 共{count}次");
            time = 0;
            time2 = 0;
            count = 0;
        }

    }



    public void Display()
    {
        var queryBuilder = new QueryBuilder().WithAll<Position>().SetLimit(690);
        GD.Print("Display:");
        DisplayLimitedEntityPosSystem displayLimitedEntityPosSystem = new() { EntityCount = EntityCount };
        myWorld.Query(queryBuilder, displayLimitedEntityPosSystem);

    }

    public void Report()
    {
        //myWorld.ReportAllArchetypes();
    }

    public void Pause()
    {
        isPaused = !isPaused;
    }


    public void RenderingInit()
    {
        if (multiMeshInstances.Length == 0) return;
        for (int meshIndex = 0; meshIndex < multiMeshInstances.Length; meshIndex++)
        {
            var multiMesh = multiMeshInstances[meshIndex].Multimesh;
            if (multiMesh == null) continue;

            var buffer = RenderingServer.MultimeshGetBuffer(multiMesh.GetRid());
            QueryBuilder queryBuilder = new QueryBuilder().WithAll<Position>();

            int startIndex = meshIndex * ENTITIES_PER_MESH;
            int endIndex = startIndex + ENTITIES_PER_MESH;

            int bufferIndex = 0;
            for (int entityIndex = startIndex; entityIndex < endIndex -1; entityIndex++)
            {
                if (entityIndex >= ENTITIES_PER_MESH) break;
                int baseIndex = bufferIndex * GODOT_FLOATS_PER_INSTANCE;
                float rotation = 0.0f;
                float cosX = Mathf.Cos(rotation);
                float sinX = Mathf.Sin(rotation);
                buffer[baseIndex] = cosX;    // x.x
                buffer[baseIndex + 1] = -sinX;   // y.x
                buffer[baseIndex + 2] = 0.0f;    // padding
                buffer[baseIndex + 3] = 0; // origin.x
                buffer[baseIndex + 4] = sinX;    // x.y
                buffer[baseIndex + 5] = cosX;    // y.y
                buffer[baseIndex + 6] = 0.0f;    // padding
                buffer[baseIndex + 7] = 0; // origin.y
                bufferIndex++;
            }

            try
            {
                RenderingServer.MultimeshSetBuffer(
                    multiMesh.GetRid(),
                    buffer
                );
            }
            catch (Exception e)
            {
                GD.PrintErr($"更新失败: {e.Message}");
            }
        }
    }

    public unsafe void DisplaySprites()
    {
        if (multiMeshInstances.Length == 0) return;

        List<Position> poses = new();
        GetAllEntityPosSystem getAllEntityPosSystem = new();
        getAllEntityPosSystem.poses = poses;
        myWorld.Query(new QueryBuilder().WithAll<Position>().SetLimit(ENTITIES_PER_MESH), getAllEntityPosSystem);
        poses = getAllEntityPosSystem.poses;
        if (poses.Count == 0) return;
        for (int meshIndex = 0; meshIndex < multiMeshInstances.Length; meshIndex++)
        {

            var multiMesh = multiMeshInstances[meshIndex].Multimesh;
            if (multiMesh == null) continue;
            multiMesh.SetInstanceTransform2D(0, new Transform2D(0.0f, Vector2.Zero)); //解决放大缩小摄像机时，缓冲区不一致导致渲染bug
            Span<float> bufferArray = RenderingServer.MultimeshGetBuffer(multiMesh.GetRid()).AsSpan();
            fixed (float* bufferPtr = bufferArray)
            {
                int startEntity = meshIndex * ENTITIES_PER_MESH;
                int endEntity = startEntity + ENTITIES_PER_MESH;

                // 仅处理属于当前MultiMesh的实体
                for (int entityId = startEntity; entityId < endEntity; entityId++)
                {
                    if (entityId >= poses.Count) break;

                    var pos = poses[entityId];

                    // 计算在缓冲区中的位置
                    int instanceIndex = entityId - startEntity;
                    int baseIndex = instanceIndex * GODOT_FLOATS_PER_INSTANCE;
                    bufferPtr[baseIndex + 3] = pos.pos.X;
                    bufferPtr[baseIndex + 7] = pos.pos.Y;
                }
            }
            try
            {
                RenderingServer.MultimeshSetBuffer(
                    multiMesh.GetRid(),
                    bufferArray
                );

            }
            catch (Exception e)
            {
                GD.PrintErr($"更新失败: {e.Message}");
            }
        }
    }


}

//public partial struct MoveSystemSIMD : ISystem<Position, Vel>
//{
//    public double dt;
//    public Vector2 viewportSize;

//    [MethodImpl(MethodImplOptions.AggressiveInlining)]
//    public unsafe void _execute(Entity* entity, Position* pos, Vel* vel, int Count, int _Generated_LimitCount)
//    {
//        unchecked
//        {
//            UpdatePhysicsSIMD((nint)pos, (nint)vel, Count, viewportSize, (float)dt, _Generated_LimitCount);
//        }
//    }


//    private unsafe void UpdatePhysicsSIMD(IntPtr posPtr, IntPtr velPtr, int count, Vector2 viewportSize, float delta, int LimitCount)
//    {
//        if (posPtr == IntPtr.Zero || velPtr == IntPtr.Zero)
//            return;
//        Vector2* positions = (Vector2*)posPtr.ToPointer();
//        Vector2* velocities = (Vector2*)velPtr.ToPointer();

//        // 处理能被8整除的部分
//        int simdCount = count - (count % 8);
//        int i = 0;
//        if (Avx2.IsSupported && count >= 8)
//        {
//            var dtVec = Vector256.Create(delta);
//            var viewportX = Vector256.Create(viewportSize.X);
//            var viewportY = Vector256.Create(viewportSize.Y);
//            var zero = Vector256<float>.Zero;
//            var negativeOne = Vector256.Create(-1f);

//            for (; i <= count - 8; i += 8)
//            {
//                if (LimitCount > 0 && i >= LimitCount) break;
//                // 加载位置和速度
//                var posX = Avx.LoadVector256((float*)(positions + i));
//                var posY = Avx.LoadVector256((float*)(positions + i) + 1);
//                var velX = Avx.LoadVector256((float*)(velocities + i));
//                var velY = Avx.LoadVector256((float*)(velocities + i) + 1);

//                // 更新位置
//                posX = Avx.Add(posX, Avx.Multiply(velX, dtVec));
//                posY = Avx.Add(posY, Avx.Multiply(velY, dtVec));

//                // 边界检测
//                var minMaskX = Avx.Compare(posX, zero, FloatComparisonMode.OrderedLessThanSignaling);
//                var maxMaskX = Avx.Compare(posX, viewportX, FloatComparisonMode.OrderedGreaterThanSignaling);
//                var minMaskY = Avx.Compare(posY, zero, FloatComparisonMode.OrderedLessThanSignaling);
//                var maxMaskY = Avx.Compare(posY, viewportY, FloatComparisonMode.OrderedGreaterThanSignaling);

//                var bounceMaskX = Avx.Or(minMaskX, maxMaskX);
//                var bounceMaskY = Avx.Or(minMaskY, maxMaskY);

//                // 应用反弹
//                velX = Avx.BlendVariable(velX, Avx.Multiply(velX, negativeOne), bounceMaskX);
//                velY = Avx.BlendVariable(velY, Avx.Multiply(velY, negativeOne), bounceMaskY);

//                // 存储结果
//                Avx.Store((float*)(positions + i), posX);
//                Avx.Store((float*)(positions + i) + 1, posY);
//                Avx.Store((float*)(velocities + i), velX);
//                Avx.Store((float*)(velocities + i) + 1, velY);
//            }
//        }
//        // 处理剩余实体
//        for (; i < count; i++)
//        {
//            if (LimitCount > 0 && i >= LimitCount) break;
//            ref var pos = ref positions[i];
//            ref var vel = ref velocities[i];
//            pos.X += vel.X * delta;
//            pos.Y += vel.Y * delta;

//            if (pos.X < 0 || pos.X > viewportSize.X) vel.X *= -1;

//            if (pos.Y < 0 || pos.Y > viewportSize.Y) vel.Y *= -1;
//        }
//    }

//}
