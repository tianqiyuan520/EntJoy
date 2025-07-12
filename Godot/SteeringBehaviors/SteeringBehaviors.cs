using EntJoy;
using Godot;
using System;
using System.Collections.Generic;

public struct SteeringBehaviorsAgent : IComponent
{
    public Vector2 Target;

}




// SteeringBehaviorsSystem 结构体
public partial struct SteeringBehaviorsSystem : ISystem<Position, Vel, QTComp, SteeringBehaviorsAgent>
{
    public QTree<QTComp> qTree;
    public double dt;
    public List<Vector2> line;
    public const float MaxSpeed = 200f;
    public const float MaxForce = 1000f; // 添加最大力限制

    public void Execute(ref Entity entity, ref Position pos, ref Vel vel, ref QTComp qTComp, ref SteeringBehaviorsAgent agent)
    {
        Separation(ref entity, ref pos, ref vel, ref qTComp, ref agent);
        // 限制速度（考虑dt）
        LimitSpeed(ref vel, (float)dt);
    }

    // 添加速度限制方法（考虑dt）
    private void LimitSpeed(ref Vel vel, float delta)
    {
        float currentSpeed = vel.vel.Length();
        if (currentSpeed > MaxSpeed)
        {
            vel.vel = vel.vel.Normalized() * MaxSpeed * delta;
        }
    }

    // 修正分离行为
    public void Separation(ref Entity entity, ref Position pos, ref Vel vel, ref QTComp qTComp, ref SteeringBehaviorsAgent agent)
    {
        List<QTComp> list = new List<QTComp>();
        const float separationRadius = 200f;

        // 使用分离半径作为查询范围
        QTComp tempQTComp = new QTComp()
        {
            Id = entity.Id,
            X = pos.pos.X,
            Y = pos.pos.Y,
            Width = (int)(separationRadius * 2),
            Height = (int)(separationRadius * 2)
        };

        qTree.GetAroundObj(ref tempQTComp, ref list);

        Vector2 separationForce = Vector2.Zero;
        int neighborCount = 0;
        const float minDistance = 0.001f; // 最小有效距离

        foreach (var neighbor in list)
        {
            if (neighbor.Id == entity.Id) continue;

            Vector2 neighborPos = new Vector2(neighbor.X, neighbor.Y);
            Vector2 toAgent = pos.pos - neighborPos;
            float distance = toAgent.Length();

            // 添加距离检查
            if (distance > minDistance && distance < separationRadius)
            {
                // 使用更合理的力计算：线性衰减
                float strength = (separationRadius - distance) / separationRadius;

                if (toAgent != Vector2.Zero)
                {
                    separationForce += toAgent.Normalized() * strength;
                    neighborCount++;
                }
            }
        }

        if (neighborCount > 0)
        {
            //separationForce /= neighborCount;
            separationForce *= 100f;
            // 应用力限制
            if (separationForce.Length() > MaxForce)
            {
                separationForce = separationForce.Normalized() * MaxForce;
            }

            // 应用力到速度
            vel.vel += separationForce * (float)dt;

            // 修正调试绘制
            if (line != null)
            {
                line.Add(pos.pos); // 起点位置
                line.Add(pos.pos + separationForce); // 终点位置
            }
        }
    }
}



public partial class SteeringBehaviors : Node2D
{
    [Export]
    public Node MultiMeshgroup;
    [Export]
    public PackedScene packedScene;
    public MultiMeshInstance2D[] multiMeshInstances;
    public int SpawnMultiMeshCount = 1; // MultiMesh 个数
                                        // 每个MultiMesh的实体数量
    public int ENTITIES_PER_MESH = 10000;
    public int EntityCount = 200;
    public World myWorld;
    public Rect2 viewportRect;
    public const int GODOT_FLOATS_PER_INSTANCE = 8;
    public bool isPaused = false;

    public QueryBuilder queryBuilder = new QueryBuilder().WithAll<Position, Vel, QTComp>();
    public QueryBuilder queryBuilder2 = new QueryBuilder().WithAll<Position, QTComp>();
    public QueryBuilder SteeringBehaviorsQueryBuilder = new QueryBuilder().WithAll<Position, Vel, QTComp, SteeringBehaviorsAgent>();

    public MultiMeshInstance2D GenerateMultiMesh()
    {
        var multiMeshInstance2D = packedScene.Instantiate<MultiMeshInstance2D>();
        MultiMeshgroup.AddChild(multiMeshInstance2D);
        return multiMeshInstance2D;
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
            var entity = myWorld.NewEntity(typeof(Position), typeof(Vel), typeof(QTComp), typeof(SteeringBehaviorsAgent));
            myWorld.AddComponent(entity, new Position()
            {
                pos = new Vector2(
                        (float)GD.RandRange(viewportRect.Position.X, viewportRect.Position.X + viewportRect.Size.X),
                        (float)GD.RandRange(viewportRect.Position.Y, viewportRect.Position.Y + viewportRect.Size.Y)
                    )
            });
            myWorld.AddComponent(entity, new Vel()
            {
                vel = new Vector2
                (
                    (float)GD.RandRange(-100.0, 100.0),
                    (float)GD.RandRange(-100.0, 100.0)
                )
            });
            myWorld.AddComponent(entity, new QTComp()
            {
                Id = entity.Id,
                X = 0,
                Y = 0,
                Width = 1,
                Height = 1
            });
            myWorld.AddComponent(entity, new SteeringBehaviorsAgent()
            {
                Target = new Vector2(0, 0),
            }
                );
        }
        GD.Print($"NewEntity Success ");

        RenderingInit();
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
            for (int entityIndex = startIndex; entityIndex < endIndex - 1; entityIndex++)
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

    public void Display()
    {
        var queryBuilder = new QueryBuilder().WithAll<Position, QTComp>().SetLimit(100);
        GD.Print("Display:");
        DisplayLimitedEntityPosSystem2 displayLimitedEntityPosSystem2 = new() { EntityCount = EntityCount};
        myWorld.Query(queryBuilder, displayLimitedEntityPosSystem2);

    }

    public void Pause()
    {
        isPaused = !isPaused;
    }


    public override void _Ready()
    {
        GetNode("CanvasLayer/HBoxContainer").GetNode<Button>("CreateWorld").Pressed += CreateWorld;
        GetNode("CanvasLayer/HBoxContainer").GetNode<Button>("CreateEntity").Pressed += NewEntity;
        GetNode("CanvasLayer/HBoxContainer").GetNode<Button>("PrintEntity").Pressed += Display;
        GetNode("CanvasLayer/HBoxContainer").GetNode<Button>("Pause").Pressed += Pause;
        GetNode("CanvasLayer/HBoxContainer").GetNode<Button>("UpdateTree").Pressed += UpdateTree;
        //GetNode<Timer>("Timer").Timeout += UpdateTree;
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
        quadTreeUpdateSystem.viewportSize = viewportRect.Size;

        qTree.Init();
        qTree.CreateRoot();
        qTree.InitRect(viewportRect.Position.X + viewportRect.Size.X / 2,
            viewportRect.Position.Y + viewportRect.Size.Y / 2,
            viewportRect.Size.X, viewportRect.Size.Y);
    }

    public override void _Process(double delta)
    {
        if (myWorld == null)
        {
            return;
        }
    }
    public override void _PhysicsProcess(double delta)
    {

        if (myWorld == null)
        {
            return;
        }
        if (!isPaused) TickLoop(delta);
        //UpdateTree();
    }
    public QuadTreeMoveSystem quadTreeUpdateSystem = new();
    public SteeringBehaviorsSystem steeringBehaviorsSystem = new SteeringBehaviorsSystem();
    public void TickLoop(double delta)
    {
        //var start = DateTime.Now;
        quadTreeUpdateSystem.dt = (float)delta;
        myWorld.Query(queryBuilder, quadTreeUpdateSystem);
        DisplaySprites();
        UpdateTree();
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

    //quad tree
    public QTree<QTComp> qTree = new QTree<QTComp>();
    public TreeUpdateSystem treeUpdateSystem = new();

    bool IsRuningTreeBuild = false;
    public async void UpdateTree()
    {
        if (myWorld == null)
        {
            return;
        }
        if (isPaused || IsRuningTreeBuild) return;
        var start = DateTime.Now;
        //await Task.Run(() =>
        //{
        IsRuningTreeBuild = true;
        qTree.Clear();
        treeUpdateSystem.qTree = qTree;
        myWorld.Query(queryBuilder2, treeUpdateSystem);
        qTree = treeUpdateSystem.qTree;
        IsRuningTreeBuild = false;
        //});
        var end = DateTime.Now;
        GD.Print($"四叉树构建耗时 :{(end - start).TotalMilliseconds}ms");
        //
        //四叉树构建完成后
        steeringBehaviorsSystem.dt = GetPhysicsProcessDeltaTime();
        steeringBehaviorsSystem.qTree = qTree;
        steeringBehaviorsSystem.line = new();
        myWorld.Query(SteeringBehaviorsQueryBuilder, steeringBehaviorsSystem);
        lineQueue = steeringBehaviorsSystem.line;
        //
        DrawTree();

    }
    List<Vector2> lineQueue;

    private void DrawTree()
    {
        QueueRedraw();
    }

    public override void _Draw()
    {
        List<QTree<QTComp>> qtList = new();
        qtList.Clear();
        qTree.GetAllChildNodes(ref qtList);
        for (int i = 0; i < qtList.Count; ++i)
        {
            float halfX = qtList[i].Width / 2f;
            float halfY = qtList[i].Height / 2f;
            Vector2[] point = new Vector2[4];
            point[0].X = point[1].X = point[2].X = point[3].X = qtList[i].X;
            point[0].Y = point[1].Y = point[2].Y = point[3].Y = qtList[i].Y;

            point[0].X -= halfX; point[0].Y -= halfY;
            point[1].X -= halfX; point[1].Y += halfY;
            point[2].X += halfX; point[2].Y += halfY;
            point[3].X += halfX; point[3].Y -= halfY;
            for (int j = 0; j < point.Length; j++)
            {

                DrawLine(point[j], point[(j + 1) % point.Length], new Color("#000000"), 1f);
            }
        }


        if (lineQueue == null || lineQueue.Count == 0) return;
        // 修正绘制逻辑：每两个点构成一条线
        for (int i = 0; i < lineQueue.Count; i += 2)
        {
            if (i + 1 >= lineQueue.Count) break;

            // 直接使用存储的两个点
            DrawLine(lineQueue[i], lineQueue[i + 1], new Color("#FF0000"), 2f); // 使用红色更明显
        }

    }

}
