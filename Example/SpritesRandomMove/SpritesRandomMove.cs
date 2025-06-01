using EntJoy;
using Godot;
using System;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;



public struct Position : IComponent
{
    public Vector2 pos;
    //public Vector2 vel;
    public override string ToString()
    {
        return $"({pos[0]:F2}, {pos[1]:F2})";
    }
}

public struct Vel : IComponent
{
    public Vector2 vel;
    public override string ToString()
    {
        return $"({vel[0]:F2}, {vel[1]:F2})";
    }
}


// [DebuggerDisplay("test,{myWorld}")]
public partial class SpritesRandomMove : Node2D
{
    public World myWorld;
    public int count = 0;
    [Export]
    public MultiMeshInstance2D multiMeshInstance;
    private Rect2 viewportRect;
    const int GODOT_FLOATS_PER_INSTANCE = 8;

    public override void _Ready()
    {
        GetNode("CanvasLayer/HBoxContainer").GetNode<Button>("CreateWorld").Pressed += CreateWorld;
        GetNode("CanvasLayer/HBoxContainer").GetNode<Button>("CreateEntity").Pressed += NewEntity;
        GetNode("CanvasLayer/HBoxContainer").GetNode<Button>("PrintEntity").Pressed += Display;
        GetNode("CanvasLayer/HBoxContainer").GetNode<Button>("Report").Pressed += Report;
        //multiMeshInstance = GetNode<MultiMeshInstance2D>("MultiMeshInstance2D");
        multiMeshInstance.Multimesh.InstanceCount = 30000;
        viewportRect = GetViewportRect();
        moveSystem.viewportSize = viewportRect.Size;
        moveSystemSIMD.viewportSize = viewportRect.Size;
    }

    public void CreateWorld()
    {
        myWorld = new World();
        World_Recorder.RecordWorld(myWorld);
        GD.Print($"创建世界成功; 当前世界总数:{World_Recorder.worldList.Count}");
    }


    public void NewEntity()
    {
        //for (int i = 0; i < 2; i++)
        //{
        //	var entity = myWorld.NewEntity(typeof(Position));
        //	myWorld.AddComponent(entity, new Position()
        //	{
        //		pos = new Vector2(11, i % 800),
        //		//vel = new Vector2(100, 0),
        //	});
        //}
        for (int i = 0; i < 30000; i++)
        {
            var entity = myWorld.NewEntity(typeof(Position), typeof(Vel));
            myWorld.AddComponent(entity, new Position()
            {
                pos = new Vector2(11, i % 800),
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
                    (float)GD.RandRange(-200.0, 200.0),
                    (float)GD.RandRange(-200.0, 200.0)
                )
            });
        }
        GD.Print($"NewEntity Success ");

        RenderingInit();
    }

    public QueryBuilder queryBuilder = new QueryBuilder().WithAll<Position, Vel>();
    public QueryBuilder queryBuilder2 = new QueryBuilder().WithAll<Position>();

    public override void _PhysicsProcess(double delta)
    {
        if (myWorld == null)
        {
            return;
        }

        TickLoop(delta);
        //myWorld.Query(
        //	new QueryBuilder().WithAll<Position, Vel>(),
        //	(Entity ent, params IComponent[] components) =>
        //	{
        //		Position pos = (Position)components[0];
        //		Vel vel = (Vel)components[1];

        //              pos.pos += vel.vel * dt;
        //	},
        //	typeof(Position), typeof(Vel));

    }
    public MoveSystem moveSystem = new();
    public MoveSystemSIMD moveSystemSIMD = new();
    public RenderingSystem renderingSystem = new();
    public void TickLoop(double delta)
    {
        //Vector2 viewportSize = viewportRect.Size;

        moveSystemSIMD.dt = delta;
        myWorld.QuerySIMD(
            queryBuilder,
            moveSystemSIMD
        );
        //moveSystem.dt = delta;
        //myWorld.Query(queryBuilder, moveSystem);

        //myWorld.Query(queryBuilder, (ref Entity ent, ref Position pos, ref Vel vel) =>
        //{
        //    //pos.pos += vel.vel * dt;
        //    pos.pos += vel.vel * (float)delta;
        //    if (pos.pos.X < 0 || pos.pos.X > viewportSize.X) vel.vel.X *= -1;
        //    if (pos.pos.Y < 0 || pos.pos.Y > viewportSize.Y) vel.vel.Y *= -1;
        //    //UpdateInstancePosition(ref pos, ref vel, delta);
        //});

        //DisplaySprites();
        DisplaySpritesOptimized();
    }


    public struct MoveSystem : ISystem<Position, Vel>
    {
        public double dt;
        public Vector2 viewportSize;

        public void Execute(ref Entity entity, ref Position pos, ref Vel vel)
        {
            pos.pos += vel.vel * (float)dt;
            if (pos.pos.X < 0 || pos.pos.X > viewportSize.X) vel.vel.X *= -1;
            if (pos.pos.Y < 0 || pos.pos.Y > viewportSize.Y) vel.vel.Y *= -1;
        }
    }


    private void UpdateInstancePosition(ref Position pos, ref Vel vel, double delta)
    {
        Vector2 viewportSize = viewportRect.Size;

        pos.pos += vel.vel * (float)delta;

        if (pos.pos.X < 0 || pos.pos.X > viewportSize.X)
        {
            vel.vel.X *= -1;
            //pos.pos.X = Mathf.Clamp(pos.pos.X, 0.0f, viewportSize.X);
        }

        if (pos.pos.Y < 0 || pos.pos.Y > viewportSize.Y)
        {
            vel.vel.Y *= -1;
            //pos.pos.Y = Mathf.Clamp(pos.pos.Y, 0.0f, viewportSize.Y);
        }

    }

    public void Display()
    {
        var queryBuilder = new QueryBuilder().WithAll<Position>().SetLimit(100);
        GD.Print("Display:");
        myWorld.Query(queryBuilder, (ref Entity ent, ref Position pos) =>
        {
            GD.Print(ent.Id, pos.pos);
        });

    }

    public void Report()
    {
        myWorld.ReportAllArchetypes();
    }

    public void DisplaySprites()
    {
        multiMeshInstance = GetNode<MultiMeshInstance2D>("MultiMeshInstance2D");
        if (multiMeshInstance?.Multimesh == null) return;
        var multiMesh = multiMeshInstance.Multimesh;

        //var buffer = RenderingServer.MultimeshGetBuffer(multiMesh.GetRid());
        renderingSystem.index = 0;
        renderingSystem._buffer = RenderingServer.MultimeshGetBuffer(multiMesh.GetRid());
        myWorld.Query(queryBuilder2, renderingSystem);
        try
        {
            RenderingServer.MultimeshSetBuffer(
                multiMesh.GetRid(),
                renderingSystem._buffer
            );
        }
        catch (Exception e)
        {
            GD.PrintErr($"更新失败: {e.Message}");
        }
    }

    public struct RenderingSystem : ISystem<Position>
    {
        public float[] _buffer;
        public int index = 0;

        public RenderingSystem(float[] buffer)
        {
            _buffer = buffer;
        }

        public void Execute(ref Entity entity, ref Position pos)
        {
            if (_buffer.Length == 0) return;
            float rotation = 0.0f;
            float cosX = Mathf.Cos(rotation);
            float sinX = Mathf.Sin(rotation);

            int baseIndex = index * GODOT_FLOATS_PER_INSTANCE;
            // 根据最新格式要求填充 (x.x, y.x, padding, origin.x, x.y, y.y, padding, origin.y)
            _buffer[baseIndex] = cosX;    // x.x
            _buffer[baseIndex + 1] = -sinX;   // y.x
            _buffer[baseIndex + 2] = 0.0f;    // padding
            _buffer[baseIndex + 3] = pos.pos.X; // origin.x
            _buffer[baseIndex + 4] = sinX;    // x.y
            _buffer[baseIndex + 5] = cosX;    // y.y
            _buffer[baseIndex + 6] = 0.0f;    // padding
            _buffer[baseIndex + 7] = pos.pos.Y; // origin.y

            index++;
        }
    }

    public struct MoveSystemSIMD : IForeachWithSIMD<Position, Vel>
    {
        public double dt;
        public Vector2 viewportSize;
        public void Execute(ref IntPtr posPtr, ref IntPtr velPtr, int count)
        {
            UpdatePhysicsSIMD(posPtr, velPtr, count, viewportSize, (float)dt);
        }


        private unsafe void UpdatePhysicsSIMD(
        IntPtr posPtr,
        IntPtr velPtr,
        int count,
        Vector2 viewportSize,
        float delta)
        {
            //GD.Print(posPtr," ", velPtr);
            if (posPtr == IntPtr.Zero || velPtr == IntPtr.Zero)
                return;
            Vector2* positions = (Vector2*)posPtr.ToPointer();
            Vector2* velocities = (Vector2*)velPtr.ToPointer();

            // 处理能被8整除的部分
            int simdCount = count - (count % 8);
            int i = 0;
            if (Avx2.IsSupported && count >= 8)
            {
                var dtVec = Vector256.Create(delta);
                var viewportX = Vector256.Create(viewportSize.X);
                var viewportY = Vector256.Create(viewportSize.Y);
                var zero = Vector256<float>.Zero;
                var negativeOne = Vector256.Create(-1f);

                for (; i <= count - 8; i += 8)
                {
                    
                    // 加载位置和速度
                    var posX = Avx.LoadVector256((float*)(positions + i));
                    var posY = Avx.LoadVector256((float*)(positions + i) + 8);
                    var velX = Avx.LoadVector256((float*)(velocities + i));
                    var velY = Avx.LoadVector256((float*)(velocities + i) + 8);

                    // 更新位置
                    posX = Avx.Add(posX, Avx.Multiply(velX, dtVec));
                    posY = Avx.Add(posY, Avx.Multiply(velY, dtVec));

                    // 边界检测和反弹
                    var maskXMin = Avx.CompareLessThan(posX, zero);
                    var maskXMax = Avx.CompareGreaterThan(posX, viewportX);
                    var maskYMin = Avx.CompareLessThan(posY, zero);
                    var maskYMax = Avx.CompareGreaterThan(posY, viewportY);

                    velX = Avx.BlendVariable(velX,
                        Avx.Multiply(velX, negativeOne),
                        Avx.Or(maskXMin, maskXMax));

                    velY = Avx.BlendVariable(velY,
                        Avx.Multiply(velY, negativeOne),
                        Avx.Or(maskYMin, maskYMax));

                    // 位置钳制
                    //posX = Avx.Min(Avx.Max(posX, zero), viewportX);
                    //posY = Avx.Min(Avx.Max(posY, zero), viewportY);

                    // 存储结果
                    Avx.Store((float*)(positions + i), posX);
                    Avx.Store((float*)(positions + i) + 8, posY);
                    Avx.Store((float*)(velocities + i), velX);
                    Avx.Store((float*)(velocities + i) + 8, velY);
                }
            }

            // 处理剩余部分（使用小批量处理）
            //int remaining = count - simdCount;

            //if (remaining > 0)
            //{
            //    ProcessRemainingEntities(
            //        positions,
            //        velocities,
            //        remaining, delta);
            //}

            // 处理剩余实体
            for (; i < count; i++)
            {
                ref var pos = ref positions[i];
                ref var vel = ref velocities[i];

                pos += vel * delta;

                if (pos.X < 0 || pos.X > viewportSize.X) vel.X *= -1;

                if (pos.Y < 0 || pos.Y > viewportSize.Y) vel.Y *= -1;
            }
        }

        private unsafe void ProcessRemainingEntities(
        Vector2* positions, Vector2* velocities, int count, float delta)
        {
            for (int i = 0; i < count; i++)
            {
                ref var pos = ref positions[i];
                ref var vel = ref velocities[i];
                pos += vel * delta;
                if (pos.X < 0 || pos.X > viewportSize.X) vel.X *= -1;
                if (pos.Y < 0 || pos.Y > viewportSize.Y) vel.Y *= -1;
            }
        }
    }



    public unsafe struct RenderingSystemOptimized : ISystem<Position>
    {
        public float* BufferPtr;
        public int InstanceCount;

        public void Execute(ref Entity entity, ref Position pos)
        {
            // 预先计算好的偏移量
            const int xOffset = 3;
            const int yOffset = 7;
            const int stride = GODOT_FLOATS_PER_INSTANCE;

            int baseIndex = entity.Id * stride;
            BufferPtr[baseIndex + xOffset] = pos.pos.X;
            BufferPtr[baseIndex + yOffset] = pos.pos.Y;
        }
    }

    // 在DisplaySprites中使用
    public unsafe void DisplaySpritesOptimized()
    {
        multiMeshInstance = GetNode<MultiMeshInstance2D>("MultiMeshInstance2D");
        if (multiMeshInstance?.Multimesh == null) return;
        var multiMesh = multiMeshInstance.Multimesh;

        float[] bufferArray = RenderingServer.MultimeshGetBuffer(multiMesh.GetRid());
        fixed (float* bufferPtr = bufferArray)
        {
            var renderingSystem = new RenderingSystemOptimized
            {
                BufferPtr = bufferPtr,
                InstanceCount = bufferArray.Length / GODOT_FLOATS_PER_INSTANCE
            };

            myWorld.Query(queryBuilder2, renderingSystem);
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

    public void RenderingInit()
    {
        multiMeshInstance = GetNode<MultiMeshInstance2D>("MultiMeshInstance2D");
        if (multiMeshInstance?.Multimesh == null) return;
        var multiMesh = multiMeshInstance.Multimesh;
        var buffer = RenderingServer.MultimeshGetBuffer(multiMesh.GetRid());
        int index = 0;
        myWorld.Query(queryBuilder, (ref Entity ent, ref Position pos) =>
        {
            float rotation = 0.0f;
            float cosX = Mathf.Cos(rotation);
            float sinX = Mathf.Sin(rotation);

            int baseIndex = index * GODOT_FLOATS_PER_INSTANCE;
            // 根据最新格式要求填充 (x.x, y.x, padding, origin.x, x.y, y.y, padding, origin.y)
            buffer[baseIndex] = cosX;    // x.x
            buffer[baseIndex + 1] = -sinX;   // y.x
            buffer[baseIndex + 2] = 0.0f;    // padding
            buffer[baseIndex + 3] = pos.pos[0]; // origin.x
            buffer[baseIndex + 4] = sinX;    // x.y
            buffer[baseIndex + 5] = cosX;    // y.y
            buffer[baseIndex + 6] = 0.0f;    // padding
            buffer[baseIndex + 7] = pos.pos[1]; // origin.y

            index++;
        });
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
