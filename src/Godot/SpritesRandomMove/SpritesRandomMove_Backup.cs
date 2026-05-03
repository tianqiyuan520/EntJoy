//using EntJoy;
//using Godot;
//using System.Collections.Generic;
//using System.Runtime.CompilerServices;

//public struct Position : IComponentData
//{
//    public Vector2 pos;
//}

//public struct Vel : IComponentData
//{
//    public Vector2 vel;
//}

//public struct TestEnableableComponent : IComponentData, IEnableableComponent
//{

//}

//public partial struct DisplayLimitedEntityPosSystem : ISystem
//{
//    public int LimitCount;
//    public int EndIndex;
//    public int StartIndex;

//    public void OnUpdate()
//    {
//        int index = 0;

//        foreach (var chunk in SystemAPI.QueryChunks<Position, Vel>())
//        {
//            var positions = chunk.GetSpan0();
//            var velocities = chunk.GetSpan1();

//            int length = chunk.Length;
//            for (int i = 0; i < length && index < EndIndex; i++)
//            {
//                if (index < StartIndex)
//                {
//                    index++;
//                    continue;
//                }
//                index++;
//                ref var pos = ref positions[i];
//                ref var vel = ref velocities[i];

//                GD.Print(index, " ", pos.pos);
//            }
//        }
//    }
//}

//public unsafe partial struct MoveSystem : ISystem
//{
//    public Vector2 viewportSize;
//    public float dt;
//    //public int* ProcessedCount;

//    public void OnUpdate()
//    {
//        foreach (var chunk in SystemAPI.QueryChunks<Position, Vel>())
//        {
//            var positions = chunk.GetSpan0();
//            var velocities = chunk.GetSpan1();
//            int length = chunk.Length;
//            for (int i = 0; i < length; i++)
//            {
//                ref var pos = ref positions[i];
//                ref var vel = ref velocities[i];

//                pos.pos.X += vel.vel.X * dt;
//                pos.pos.Y += vel.vel.Y * dt;
//                //System.Threading.Interlocked.Add(ref *ProcessedCount, 1);
//            }
//        }
//    }
//}

//public unsafe struct MoveSystemJob : IJobChunk
//{
//    public float dt;
//    //public int* ProcessedCount;

//    [MethodImpl(MethodImplOptions.AggressiveInlining)]
//    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
//    {
//        var positions = chunk.GetComponentDataSpan<Position>();
//        var velocities = chunk.GetComponentDataSpan<Vel>();
//        int count = chunk.Count;

//        //for (int i = 0; i < chunk.Count; i++)
//        //{
//        //    ref var pos = ref positions[i];
//        //    ref var vel = ref velocities[i];

//        //    pos.pos.X += vel.vel.X * dt;
//        //    pos.pos.Y += vel.vel.Y * dt;
//        //}

//        if (enabledMask.Length == 0)
//        {
//            // 无启用过滤：处理所有实体
//            for (int i = 0; i < count; i++)
//            {
//                positions[i].pos.X += velocities[i].vel.X * dt;
//                positions[i].pos.Y += velocities[i].vel.Y * dt;
//            }
//        }
//        else
//        {
//            // 使用范围迭代处理启用的实体
//            //int start = 0;
//            //while (enabledMask.TryGetNextRange(ref start, out int rangeStart, out int rangeEnd))
//            //{
//            //    for (int i = rangeStart; i < rangeEnd; i++)
//            //    {
//            //        positions[i].pos.X += velocities[i].vel.X * dt;
//            //        positions[i].pos.Y += velocities[i].vel.Y * dt;
//            //    }
//            //}
//            for (int i = 0; i < count; i++)
//            {
//                if (enabledMask.IsEnabled(i))
//                {
//                    positions[i].pos.X += velocities[i].vel.X * dt;
//                    positions[i].pos.Y += velocities[i].vel.Y * dt;
//                }
//            }
//        }

//        //System.Threading.Interlocked.Add(ref *ProcessedCount, chunk.Count);
//    }
//}

//// SIMD
////public unsafe struct MoveSystemSIMDJob : IJobChunk
////{
////    public float dt;

////    [MethodImpl(MethodImplOptions.AggressiveInlining)]
////    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
////    {
////        int count = chunk.Count;
////        if (count == 0) return;

////        // 获取组件数组的指针（直接操作指针以获得最佳性能）
////        Position* posPtr = chunk.GetComponentDataPtr<Position>();
////        Vel* velPtr = chunk.GetComponentDataPtr<Vel>();

////        float dtVal = dt;

////        // ========== SIMD 优化部分 ==========
////        if (Avx2.IsSupported)
////        {
////            // AVX2: 每次处理 4 个实体（8 个 float）
////            int simdCount = count / 4 * 4;
////            int i = 0;
////            Vector256<float> dtVec = Vector256.Create(dtVal); // 广播 dt

////            for (; i < simdCount; i += 4)
////            {
////                // 加载 4 个实体的位置 (8 floats)
////                Vector256<float> posVec = Avx.LoadVector256((float*)(posPtr + i));
////                // 加载 4 个实体的速度
////                Vector256<float> velVec = Avx.LoadVector256((float*)(velPtr + i));

////                // 计算 vel * dt
////                Vector256<float> velDt = Avx.Multiply(velVec, dtVec);
////                // 新位置 = 原位置 + vel * dt
////                Vector256<float> newPos = Avx.Add(posVec, velDt);

////                // 存储回内存
////                Avx.Store((float*)(posPtr + i), newPos);
////            }

////            // 处理剩余不足 4 个的实体
////            for (; i < count; i++)
////            {
////                posPtr[i].pos.X += velPtr[i].vel.X * dtVal;
////                posPtr[i].pos.Y += velPtr[i].vel.Y * dtVal;
////            }
////        }
////        else if (Sse.IsSupported)
////        {
////            // SSE: 每次处理 2 个实体（4 个 float）
////            int simdCount = count / 2 * 2;
////            int i = 0;
////            Vector128<float> dtVec = Vector128.Create(dtVal);

////            for (; i < simdCount; i += 2)
////            {
////                Vector128<float> posVec = Sse.LoadVector128((float*)(posPtr + i));
////                Vector128<float> velVec = Sse.LoadVector128((float*)(velPtr + i));

////                Vector128<float> velDt = Sse.Multiply(velVec, dtVec);
////                Vector128<float> newPos = Sse.Add(posVec, velDt);

////                Sse.Store((float*)(posPtr + i), newPos);
////            }

////            for (; i < count; i++)
////            {
////                posPtr[i].pos.X += velPtr[i].vel.X * dtVal;
////                posPtr[i].pos.Y += velPtr[i].vel.Y * dtVal;
////            }
////        }
////        else
////        {
////            // 无硬件加速，退化为标量循环
////            for (int i = 0; i < count; i++)
////            {
////                posPtr[i].pos.X += velPtr[i].vel.X * dtVal;
////                posPtr[i].pos.Y += velPtr[i].vel.Y * dtVal;
////            }
////        }
////    }

////    public void Execute(ArchetypeChunk chunk, int chunkIndex, in ChunkEnabledMask enabledMask)
////    {
////        throw new NotImplementedException();
////    }
////}


//public partial class SpritesRandomMove : Node2D
//{
//    [Export]
//    Node MultiMeshgroup;
//    [Export]
//    PackedScene packedScene;

//    int SpawnMultiMeshCount = 0;


//    public World myWorld;

//    public MultiMeshInstance2D[] multiMeshInstances;
//    private Rect2 viewportRect;
//    const int GODOT_FLOATS_PER_INSTANCE = 8;
//    // 每个MultiMesh的实体数量
//    public int ENTITIES_PER_MESH = 1_0000;
//    public int EntityCount = 100_0000;
//    //public int EntityCount = 50;
//    public bool isPaused = false;

//    public MultiMeshInstance2D GenerateMultiMesh()
//    {
//        var multiMeshInstance2D = packedScene.Instantiate<MultiMeshInstance2D>();
//        MultiMeshgroup.AddChild(multiMeshInstance2D);
//        return multiMeshInstance2D;
//    }

//    public override void _Ready()
//    {
//        GetNode("CanvasLayer/HBoxContainer").GetNode<Button>("CreateWorld").Pressed += CreateWorld;
//        GetNode("CanvasLayer/HBoxContainer").GetNode<Button>("CreateEntity").Pressed += NewEntity;
//        GetNode("CanvasLayer/HBoxContainer").GetNode<Button>("PrintEntity").Pressed += Display;
//        GetNode("CanvasLayer/HBoxContainer").GetNode<Button>("Report").Pressed += Report;
//        GetNode("CanvasLayer/HBoxContainer").GetNode<Button>("Pause").Pressed += Pause;
//        //multiMeshInstance = GetNode<MultiMeshInstance2D>("MultiMeshInstance2D");
//        //multiMeshInstance.Multimesh.InstanceCount = 30000;

//        multiMeshInstances = new MultiMeshInstance2D[SpawnMultiMeshCount];

//        for (int i = 0; i < SpawnMultiMeshCount; i++)
//        {
//            multiMeshInstances[i] = GenerateMultiMesh();
//        }
//        //if (spawnMultiMeshCount != 0) ENTITIES_PER_MESH = entityCount / spawnMultiMeshCount;
//        // 初始化所有MultiMesh实例
//        for (int i = 0; i < SpawnMultiMeshCount; i++)
//        {
//            multiMeshInstances[i].Multimesh.InstanceCount = ENTITIES_PER_MESH;
//        }
//        GD.Print(multiMeshInstances.Length, " ", "ENTITIES_PER_MESH:", ENTITIES_PER_MESH, " ", "entityCount:", EntityCount);
//        viewportRect = GetViewportRect();

//        //moveSystemSIMD.viewportSize = viewportRect.Size;
//    }

//    public void CreateWorld()
//    {
//        myWorld = new World();
//        World_Recorder.RecordWorld(myWorld);
//        GD.Print($"创建世界成功; 当前世界总数:{World_Recorder.worldList.Count}");
//    }


//    public void NewEntity()
//    {
//        for (int i = 0; i < EntityCount; i++)
//        {
//            var entity = myWorld.EntityManager.NewEntity(typeof(Position), typeof(Vel));
//            myWorld.EntityManager.AddComponent(entity, new Position()
//            {
//                pos = new Vector2(100, 100),
//            });
//            myWorld.EntityManager.AddComponent(entity, new Vel()
//            {
//                vel = new Vector2
//                (
//                    (float)GD.RandRange(100.0, 200.0),
//                    (float)GD.RandRange(-200.0, 200.0)
//                )
//            });
//            myWorld.EntityManager.AddComponent<TestEnableableComponent>(entity, new());

//            //if (i % 2 == 0) myWorld.EntityManager.SetComponentEnabled<TestEnableableComponent>(entity, false);

//            if (i < 20)
//            {
//                //myWorld.EntityManager.SetComponentEnabled<TestEnableableComponent>(entity, true);
//                GD.Print(myWorld.EntityManager.IsComponentEnabled<TestEnableableComponent>(entity));
//            }

//        }
//        GD.Print($"NewEntity Success", " 当前实体数 ", myWorld.EntityManager.EntityCount);

//        //RenderingInit();
//    }

//    public override void _PhysicsProcess(double delta)
//    {

//        if (myWorld == null)
//        {
//            return;
//        }
//        if (!isPaused && myWorld.EntityManager.EntityCount > 0)
//            TickLoop(delta);
//    }
//    public MoveSystem moveSystem = new();

//    //public MoveSystemSIMD moveSystemSIMD = new MoveSystemSIMD();

//    double time = 0;
//    double time2 = 0;
//    int count = 0;

//    private List<Chunk> _cachedChunks;
//    private QueryBuilder _moveQuery = new QueryBuilder().WithAll<Position, Vel>();

//    private void RefreshMoveChunks()
//    {
//        _cachedChunks = new List<Chunk>();
//        foreach (var arch in myWorld.EntityManager.Archetypes)
//        {
//            if (arch != null && arch.IsMatch(_moveQuery))
//            {
//                foreach (var chunk in arch.GetChunks())
//                {
//                    if (chunk.EntityCount > 0)
//                        _cachedChunks.Add(chunk);
//                }
//            }
//        }
//    }


//    [MethodImpl(MethodImplOptions.AggressiveInlining)]
//    public unsafe void TickLoop(double delta)
//    {
//        PerformanceDetection.Start();

//        ////int processed = 0;
//        //moveSystem.viewportSize = viewportRect.Size;
//        //moveSystem.dt = (float)delta;
//        ////moveSystem.ProcessedCount = &processed;
//        //moveSystem.OnUpdate();
//        ////GD.Print(processed);

//        //int processed = 0;
//        var job = new MoveSystemJob
//        {
//            dt = (float)delta,
//            //ProcessedCount = &processed
//        };
//        var query = new QueryBuilder().WithAll<Position, Vel>().WithEnabled<TestEnableableComponent>();
//        JobHandle handle = job.Schedule(query);
//        handle.Complete();
//        //GD.Print(processed);


//        PerformanceDetection.End();
//        double t = PerformanceDetection.GetAverage();

//        //PerformanceDetection.Start();
//        //DisplaySprites();
//        //PerformanceDetection.End();
//        //double t2 = PerformanceDetection.GetAverage();

//        time += t;
//        //time2 += t2;
//        count++;

//        if (Engine.GetPhysicsFrames() % 20 == 0)
//        {
//            GD.Print($"逻辑平均耗时: {time / count}ms 共{count}次");
//            //GD.Print($"-渲染平均耗时: {time2 / count}ms 共{count}次");
//            time = 0;
//            time2 = 0;
//            count = 0;
//        }

//    }



//    public void Display()
//    {
//        GD.Print("Display:");
//        DisplayLimitedEntityPosSystem displayLimitedEntityPosSystem = new DisplayLimitedEntityPosSystem
//        {
//            StartIndex = 0,
//            EndIndex = 30,
//            LimitCount = 10,
//        };
//        displayLimitedEntityPosSystem.OnUpdate();

//        var getAll = new GetAllEntityPosSystem { };
//        getAll.OnUpdate();
//        GD.Print(getAll.Index);
//    }

//    public void Report()
//    {
//        //myWorld.ReportAllArchetypes();
//    }

//    public void Pause()
//    {
//        isPaused = !isPaused;
//    }


//}




////// Render
//public partial struct GetAllEntityPosSystem : ISystem
//{
//    public int Index;

//    public void OnUpdate()
//    {
//        int index = 0;
//        foreach (var chunk in SystemAPI.QueryChunks<Position, Vel>())
//        {
//            var positions = chunk.GetSpan0();
//            var velocities = chunk.GetSpan1();
//            int length = chunk.Length;
//            for (int i = 0; i < length; i++)
//            {
//                index++;
//            }
//        }

//        Index = index;
//    }
//}


////public partial class SpritesRandomMove
////{

////    public void RenderingInit()
////    {
////        if (multiMeshInstances.Length == 0) return;
////        for (int meshIndex = 0; meshIndex < multiMeshInstances.Length; meshIndex++)
////        {
////            var multiMesh = multiMeshInstances[meshIndex].Multimesh;
////            if (multiMesh == null) continue;

////            var buffer = RenderingServer.MultimeshGetBuffer(multiMesh.GetRid());
////            QueryBuilder queryBuilder = new QueryBuilder().WithAll<Position>();

////            int startIndex = meshIndex * ENTITIES_PER_MESH;
////            int endIndex = startIndex + ENTITIES_PER_MESH;

////            int bufferIndex = 0;
////            for (int entityIndex = startIndex; entityIndex < endIndex - 1; entityIndex++)
////            {
////                if (entityIndex >= Mathf.Min(EntityCount, ENTITIES_PER_MESH)) break;
////                int baseIndex = bufferIndex * GODOT_FLOATS_PER_INSTANCE;
////                float rotation = 0.0f;
////                float cosX = Mathf.Cos(rotation);
////                float sinX = Mathf.Sin(rotation);
////                buffer[baseIndex] = cosX;    // x.x
////                buffer[baseIndex + 1] = -sinX;   // y.x
////                buffer[baseIndex + 2] = 0.0f;    // padding
////                buffer[baseIndex + 3] = 0; // origin.x
////                buffer[baseIndex + 4] = sinX;    // x.y
////                buffer[baseIndex + 5] = cosX;    // y.y
////                buffer[baseIndex + 6] = 0.0f;    // padding
////                buffer[baseIndex + 7] = 0; // origin.y
////                bufferIndex++;
////            }

////            try
////            {
////                RenderingServer.MultimeshSetBuffer(
////                    multiMesh.GetRid(),
////                    buffer
////                );
////            }
////            catch (Exception e)
////            {
////                GD.PrintErr($"更新失败: {e.Message}");
////            }
////        }
////    }

////    public unsafe void DisplaySprites()
////    {
////        if (multiMeshInstances.Length == 0) return;

////        List<Position> poses = new();
////        GetAllEntityPosSystem getAllEntityPosSystem = new();
////        getAllEntityPosSystem.poses = poses;
////        myWorld.EntityManager.Query(new QueryBuilder().WithAll<Position>().SetLimit(ENTITIES_PER_MESH), getAllEntityPosSystem);
////        poses = getAllEntityPosSystem.poses;
////        if (poses.Count == 0) return;
////        for (int meshIndex = 0; meshIndex < multiMeshInstances.Length; meshIndex++)
////        {

////            var multiMesh = multiMeshInstances[meshIndex].Multimesh;
////            if (multiMesh == null) continue;
////            multiMesh.SetInstanceTransform2D(0, new Transform2D(0.0f, Vector2.Zero)); //解决放大缩小摄像机时，缓冲区不一致导致渲染bug
////            Span<float> bufferArray = RenderingServer.MultimeshGetBuffer(multiMesh.GetRid()).AsSpan();
////            fixed (float* bufferPtr = bufferArray)
////            {
////                int startEntity = meshIndex * ENTITIES_PER_MESH;
////                int endEntity = startEntity + ENTITIES_PER_MESH;

////                // 仅处理属于当前MultiMesh的实体
////                for (int entityId = startEntity; entityId < endEntity; entityId++)
////                {
////                    if (entityId >= poses.Count) break;

////                    var pos = poses[entityId];

////                    // 计算在缓冲区中的位置
////                    int instanceIndex = entityId - startEntity;
////                    int baseIndex = instanceIndex * GODOT_FLOATS_PER_INSTANCE;
////                    bufferPtr[baseIndex + 3] = pos.pos.X;
////                    bufferPtr[baseIndex + 7] = pos.pos.Y;
////                }
////            }
////            try
////            {
////                RenderingServer.MultimeshSetBuffer(
////                    multiMesh.GetRid(),
////                    bufferArray
////                );

////            }
////            catch (Exception e)
////            {
////                GD.PrintErr($"更新失败: {e.Message}");
////            }
////        }
////    }


////}



////public partial struct MoveSystemSIMD : ISystem<Position, Vel>
////{
////    public double dt;
////    public Vector2 viewportSize;

////    [MethodImpl(MethodImplOptions.AggressiveInlining)]
////    public unsafe void _execute(Entity* entity, Position* pos, Vel* vel, int Count, int _Generated_LimitCount)
////    {
////        unchecked
////        {
////            UpdatePhysicsSIMD((nint)pos, (nint)vel, Count, viewportSize, (float)dt, _Generated_LimitCount);
////        }
////    }


////    private unsafe void UpdatePhysicsSIMD(IntPtr posPtr, IntPtr velPtr, int count, Vector2 viewportSize, float delta, int LimitCount)
////    {
////        if (posPtr == IntPtr.Zero || velPtr == IntPtr.Zero)
////            return;
////        Vector2* positions = (Vector2*)posPtr.ToPointer();
////        Vector2* velocities = (Vector2*)velPtr.ToPointer();

////        // 处理能被8整除的部分
////        int simdCount = count - (count % 8);
////        int i = 0;
////        if (Avx2.IsSupported && count >= 8)
////        {
////            var dtVec = Vector256.Create(delta);
////            var viewportX = Vector256.Create(viewportSize.X);
////            var viewportY = Vector256.Create(viewportSize.Y);
////            var zero = Vector256<float>.Zero;
////            var negativeOne = Vector256.Create(-1f);

////            for (; i <= count - 8; i += 8)
////            {
////                if (LimitCount > 0 && i >= LimitCount) break;
////                // 加载位置和速度
////                var posX = Avx.LoadVector256((float*)(positions + i));
////                var posY = Avx.LoadVector256((float*)(positions + i) + 1);
////                var velX = Avx.LoadVector256((float*)(velocities + i));
////                var velY = Avx.LoadVector256((float*)(velocities + i) + 1);

////                // 更新位置
////                posX = Avx.Add(posX, Avx.Multiply(velX, dtVec));
////                posY = Avx.Add(posY, Avx.Multiply(velY, dtVec));

////                // 边界检测
////                var minMaskX = Avx.Compare(posX, zero, FloatComparisonMode.OrderedLessThanSignaling);
////                var maxMaskX = Avx.Compare(posX, viewportX, FloatComparisonMode.OrderedGreaterThanSignaling);
////                var minMaskY = Avx.Compare(posY, zero, FloatComparisonMode.OrderedLessThanSignaling);
////                var maxMaskY = Avx.Compare(posY, viewportY, FloatComparisonMode.OrderedGreaterThanSignaling);

////                var bounceMaskX = Avx.Or(minMaskX, maxMaskX);
////                var bounceMaskY = Avx.Or(minMaskY, maxMaskY);

////                // 应用反弹
////                velX = Avx.BlendVariable(velX, Avx.Multiply(velX, negativeOne), bounceMaskX);
////                velY = Avx.BlendVariable(velY, Avx.Multiply(velY, negativeOne), bounceMaskY);

////                // 存储结果
////                Avx.Store((float*)(positions + i), posX);
////                Avx.Store((float*)(positions + i) + 1, posY);
////                Avx.Store((float*)(velocities + i), velX);
////                Avx.Store((float*)(velocities + i) + 1, velY);
////            }
////        }
////        // 处理剩余实体
////        for (; i < count; i++)
////        {
////            if (LimitCount > 0 && i >= LimitCount) break;
////            ref var pos = ref positions[i];
////            ref var vel = ref velocities[i];
////            pos.X += vel.X * delta;
////            pos.Y += vel.Y * delta;

////            if (pos.X < 0 || pos.X > viewportSize.X) vel.X *= -1;

////            if (pos.Y < 0 || pos.Y > viewportSize.Y) vel.Y *= -1;
////        }
////    }

////}
