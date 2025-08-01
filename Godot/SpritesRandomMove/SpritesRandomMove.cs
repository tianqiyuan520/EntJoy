using EntJoy;
using Godot;
using System;
using System.Collections.Generic;

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
    public int Count;
    public int limitCount;

    public void Execute(ref Entity entity, ref Position pos)
    {
        if (Count > limitCount) return;
        GD.Print(entity.Id, "/", EntityCount, " ", pos.pos);
        Count++;
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

public unsafe partial struct MoveSystem : ISystem<Position, Vel>
{
    public static Vector2 viewportSize;
    public static float dt;

    public void Execute(ref Entity entity, ref Position pos, ref Vel vel)
    {
        pos.pos.X += vel.vel.X * dt;
        pos.pos.Y += vel.vel.Y * dt;
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

    int SpawnMultiMeshCount = 0;


    public World myWorld;
    public int count = 0;

    public MultiMeshInstance2D[] multiMeshInstances;
    private Rect2 viewportRect;
    const int GODOT_FLOATS_PER_INSTANCE = 8;
    // 每个MultiMesh的实体数量
    public int ENTITIES_PER_MESH = 10000;
    public int EntityCount = 100000;
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
        if (SpawnMultiMeshCount != 0) ENTITIES_PER_MESH = EntityCount / SpawnMultiMeshCount;
        // 初始化所有MultiMesh实例
        for (int i = 0; i < SpawnMultiMeshCount; i++)
        {
            multiMeshInstances[i].Multimesh.InstanceCount = ENTITIES_PER_MESH;
        }
        GD.Print(multiMeshInstances.Length, " ", "ENTITIES_PER_MESH:", ENTITIES_PER_MESH, " ", "EntityCount:", EntityCount);
        viewportRect = GetViewportRect();
        MoveSystem.viewportSize = viewportRect.Size;
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
            var entity = myWorld.NewEntity(typeof(Position), typeof(Vel));
            myWorld.AddComponent(entity, new Position()
            {
                pos = new Vector2(11, 11),
                //vel = new Vector2
                //(
                //	(float)GD.RandRange(-200.0, 200.0),
                //	(float)GD.RandRange(-200.0, 200.0)
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

    public QueryBuilder queryBuilder = new QueryBuilder().WithAll<Position, Vel>();
    public QueryBuilder queryBuilder2 = new QueryBuilder().WithAll<Position>();

    public override void _Process(double delta)
    {
        if (myWorld == null)
        {
            return;
        }

        //if (Engine.GetProcessFrames() % 2 == 0)
        //{
        //	DisplaySpritesOptimized2();
        //}
    }
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

    public void TickLoop(double delta)
    {
        //var start = DateTime.Now;
        MoveSystem.dt = (float)delta;
        myWorld.Query(queryBuilder, moveSystem);
        DisplaySprites();
        //var end = DateTime.Now;
        //if (Engine.GetPhysicsFrames() % 15 == 0)
        //{
        //    GD.Print($"TickLoop:{(end - start).TotalMilliseconds}ms");
        //}

    }



    public void Display()
    {
        var queryBuilder = new QueryBuilder().WithAll<Position>();
        GD.Print("Display:");
        DisplayLimitedEntityPosSystem displayLimitedEntityPosSystem = new() { EntityCount = EntityCount, Count = 0, limitCount = 100 };
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

    //public struct MoveSystemSIMD : IForeachWithSIMD<Position, Vel>
    //{
    //    public double dt;
    //    public Vector2 viewportSize;
    //    public void Execute(ref IntPtr posPtr, ref IntPtr velPtr, int count)
    //    {
    //        UpdatePhysicsSIMD(posPtr, velPtr, count, viewportSize, (float)dt);
    //    }


    //    private unsafe void UpdatePhysicsSIMD(
    //    IntPtr posPtr,
    //    IntPtr velPtr,
    //    int count,
    //    Vector2 viewportSize,
    //    float delta)
    //    {
    //        //GD.Print(posPtr," ", velPtr);
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

    //                // 加载位置和速度
    //                var posX = Avx.LoadVector256((float*)(positions + i));
    //                var posY = Avx.LoadVector256((float*)(positions + i) + 8);
    //                var velX = Avx.LoadVector256((float*)(velocities + i));
    //                var velY = Avx.LoadVector256((float*)(velocities + i) + 8);

    //                // 更新位置
    //                posX = Avx.Add(posX, Avx.Multiply(velX, dtVec));
    //                posY = Avx.Add(posY, Avx.Multiply(velY, dtVec));

    //                // 边界检测和反弹
    //                var maskXMin = Avx.CompareLessThan(posX, zero);
    //                var maskXMax = Avx.CompareGreaterThan(posX, viewportX);
    //                var maskYMin = Avx.CompareLessThan(posY, zero);
    //                var maskYMax = Avx.CompareGreaterThan(posY, viewportY);

    //                velX = Avx.BlendVariable(velX,
    //                    Avx.Multiply(velX, negativeOne),
    //                    Avx.Or(maskXMin, maskXMax));

    //                velY = Avx.BlendVariable(velY,
    //                    Avx.Multiply(velY, negativeOne),
    //                    Avx.Or(maskYMin, maskYMax));

    //                // 位置钳制
    //                //posX = Avx.Min(Avx.Max(posX, zero), viewportX);
    //                //posY = Avx.Min(Avx.Max(posY, zero), viewportY);

    //                // 存储结果
    //                Avx.Store((float*)(positions + i), posX);
    //                Avx.Store((float*)(positions + i) + 8, posY);
    //                Avx.Store((float*)(velocities + i), velX);
    //                Avx.Store((float*)(velocities + i) + 8, velY);
    //            }
    //        }

    //        // 处理剩余部分（使用小批量处理）
    //        //int remaining = count - simdCount;

    //        //if (remaining > 0)
    //        //{
    //        //    ProcessRemainingEntities(
    //        //        positions,
    //        //        velocities,
    //        //        remaining, delta);
    //        //}

    //        // 处理剩余实体
    //        for (; i < count; i++)
    //        {
    //            ref var pos = ref positions[i];
    //            ref var vel = ref velocities[i];

    //            pos += vel * delta;

    //            if (pos.X < 0 || pos.X > viewportSize.X) vel.X *= -1;

    //            if (pos.Y < 0 || pos.Y > viewportSize.Y) vel.Y *= -1;
    //        }
    //    }

    //    private unsafe void ProcessRemainingEntities(
    //    Vector2* positions, Vector2* velocities, int count, float delta)
    //    {
    //        for (int i = 0; i < count; i++)
    //        {
    //            ref var pos = ref positions[i];
    //            ref var vel = ref velocities[i];
    //            pos += vel * delta;
    //            if (pos.X < 0 || pos.X > viewportSize.X) vel.X *= -1;
    //            if (pos.Y < 0 || pos.Y > viewportSize.Y) vel.Y *= -1;
    //        }
    //    }
    //}



    // public unsafe partial struct RenderingSystemOptimized : ISystem<Position>
    // {
    // 	public float* BufferPtr;
    // 	public int InstanceCount;

    // 	public void Execute(ref Entity entity, ref Position pos)
    // 	{
    // 		// 预先计算好的偏移量
    // 		const int xOffset = 3;
    // 		const int yOffset = 7;
    // 		const int stride = GODOT_FLOATS_PER_INSTANCE;

    // 		int baseIndex = entity.Id * stride;
    // 		BufferPtr[baseIndex + xOffset] = pos.pos.X;
    // 		BufferPtr[baseIndex + yOffset] = pos.pos.Y;
    // 	}
    // }




    public void RenderingInit()
    {
        if (multiMeshInstances.Length == 0) return;
        List<Position> poses = new();
        GetAllEntityPosSystem getAllEntityPosSystem = new();
        getAllEntityPosSystem.poses = poses;
        myWorld.Query(queryBuilder, getAllEntityPosSystem);
        poses = getAllEntityPosSystem.poses;


        for (int meshIndex = 0; meshIndex < multiMeshInstances.Length; meshIndex++)
        {
            var multiMesh = multiMeshInstances[meshIndex].Multimesh;
            if (multiMesh == null) continue;

            var buffer = RenderingServer.MultimeshGetBuffer(multiMesh.GetRid());
            QueryBuilder queryBuilder = new QueryBuilder().WithAll<Position>();

            int startIndex = meshIndex * ENTITIES_PER_MESH;
            int endIndex = startIndex + ENTITIES_PER_MESH;

            int bufferIndex = 0;
            for (int entityIndex = startIndex; entityIndex < endIndex; entityIndex++)
            {
                if (entityIndex >= poses.Count) break;
                int baseIndex = bufferIndex * GODOT_FLOATS_PER_INSTANCE;
                float rotation = 0.0f;
                float cosX = Mathf.Cos(rotation);
                float sinX = Mathf.Sin(rotation);
                // 根据最新格式要求填充 (x.x, y.x, padding, origin.x, x.y, y.y, padding, origin.y)
                buffer[baseIndex] = cosX;    // x.x
                buffer[baseIndex + 1] = -sinX;   // y.x
                buffer[baseIndex + 2] = 0.0f;    // padding
                buffer[baseIndex + 3] = poses[entityIndex].pos[0]; // origin.x
                buffer[baseIndex + 4] = sinX;    // x.y
                buffer[baseIndex + 5] = cosX;    // y.y
                buffer[baseIndex + 6] = 0.0f;    // padding
                buffer[baseIndex + 7] = poses[entityIndex].pos[1]; // origin.y
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
        myWorld.Query(queryBuilder, getAllEntityPosSystem);
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

    //public unsafe void DisplaySpritesOptimized3()
    //{
    //    Position[] poses = new Position[EntityCount];


    //    GetAllEntityPosSystem getAllEntityPosSystem = new();
    //    getAllEntityPosSystem.poses = poses.ToList();
    //    myWorld.Query(queryBuilder, getAllEntityPosSystem);
    //    poses = getAllEntityPosSystem.poses.ToArray();


    //    for (int meshIndex = 0; meshIndex < multiMeshInstances.Length; meshIndex++)
    //    {
    //        var multiMesh = multiMeshInstances[meshIndex].Multimesh;
    //        if (multiMesh == null) continue;
    //        int startEntity = meshIndex * ENTITIES_PER_MESH;
    //        int endEntity = startEntity + ENTITIES_PER_MESH;
    //        // 仅处理属于当前MultiMesh的实体
    //        int i = 0;
    //        for (int entityId = startEntity; entityId < endEntity; entityId++)
    //        {
    //            //multiMesh.SetInstanceTransform2D(i, new Transform2D(0.0f, poses[entityId].pos));
    //            RenderingServer.MultimeshInstanceSetTransform2D(multiMesh.GetRid(), i, new Transform2D(0.0f, poses[entityId].pos));
    //            i++;
    //        }
    //    }
    //}

}
