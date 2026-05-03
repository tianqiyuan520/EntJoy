using EntJoy;
using Godot;
using System.Diagnostics;

public struct Position : IComponentData
{
    public Vector2 pos;
}

public struct Vel : IComponentData
{
    public Vector2 vel;
}

public partial class SpritesRandomMove : Node2D
{
    [Export]
    Node MultiMeshgroup;

    public World myWorld;

    public int EntityCount = 100_0000;
    public bool isPaused = false;

    private Rect2 viewportRect;

    // 精准计时
    private Stopwatch _sw = new Stopwatch();
    private double _totalMs = 0;
    private int _frameCount = 0;

    // 缓存 Query 和 Job 避免每帧分配
    private QueryBuilder _moveQuery = new QueryBuilder().WithAll<Position, Vel>();
    private MoveSystemJob _moveJob;

    public override void _Ready()
    {
        GetNode("CanvasLayer/HBoxContainer").GetNode<Button>("CreateWorld").Pressed += CreateWorld;
        GetNode("CanvasLayer/HBoxContainer").GetNode<Button>("CreateEntity").Pressed += NewEntity;
        GetNode("CanvasLayer/HBoxContainer").GetNode<Button>("PrintEntity").Pressed += Display;
        GetNode("CanvasLayer/HBoxContainer").GetNode<Button>("Report").Pressed += Report;
        GetNode("CanvasLayer/HBoxContainer").GetNode<Button>("Pause").Pressed += Pause;

        // Benchmark 按钮
        var benchBtn = GetNode<Button>("CanvasLayer/HBoxContainer/Benchmark");
        if (benchBtn == null)
        {
            benchBtn = new Button { Text = "Benchmark" };
            var hbox = GetNode("CanvasLayer/HBoxContainer");
            hbox.AddChild(benchBtn);
        }
        benchBtn.Pressed += RunBenchmark;

        viewportRect = GetViewportRect();
    }

    public override void _PhysicsProcess(double delta)
    {
        if (myWorld == null || isPaused || myWorld.EntityManager.EntityCount == 0)
            return;

        _moveJob.dt = (float)delta;

        _sw.Restart();
        _moveJob.Schedule(_moveQuery).Complete();
        _sw.Stop();

        _totalMs += _sw.Elapsed.TotalMilliseconds;
        _frameCount++;

        if (_frameCount >= 60)
        {
            double avg = _totalMs / _frameCount;
            GD.Print($"每帧平均耗时(60帧): {avg:F4} ms");
            _totalMs = 0;
            _frameCount = 0;
        }
    }

    public void RunBenchmark()
    {
        if (myWorld == null || myWorld.EntityManager.EntityCount == 0)
        {
            GD.Print("请先创建世界和实体");
            return;
        }

        const int WARMUP = 3;
        const int ITERATIONS = 1000;
        var query = new QueryBuilder().WithAll<Position, Vel>();
        var sw = new Stopwatch();

        GD.Print($"预热 {WARMUP} 次...");
        for (int i = 0; i < WARMUP; i++)
        {
            var job = new MoveSystemJob { dt = 0.016f };
            job.Schedule(query).Complete();
        }

        GD.Print($"开始基准测试 {ITERATIONS} 次...");
        double total = 0;
        for (int i = 0; i < ITERATIONS; i++)
        {
            var job = new MoveSystemJob { dt = 0.016f };
            sw.Restart();
            job.Schedule(query).Complete();
            sw.Stop();
            total += sw.Elapsed.TotalMilliseconds;
        }

        double avg = total / ITERATIONS;
        GD.Print($"Benchmark 完成: 平均 {avg:F3} ms (共 {ITERATIONS} 次)");
    }

    public void CreateWorld()
    {
        myWorld = new World();
        GD.Print($"创建世界成功;");
    }

    public void NewEntity()
    {
        for (int i = 0; i < EntityCount; i++)
        {
            var entity = myWorld.EntityManager.NewEntity(typeof(Position), typeof(Vel));
            myWorld.EntityManager.AddComponent(entity, new Position()
            {
                pos = new Vector2(100, 100),
            });
            myWorld.EntityManager.AddComponent(entity, new Vel()
            {
                vel = new Vector2
                (
                    (float)GD.RandRange(100.0, 200.0),
                    (float)GD.RandRange(-200.0, 200.0)
                )
            });
        }

        int realCount = myWorld.EntityManager.EntityCount;
        GD.Print($"NewEntity Success 当前实体数 {realCount}");
    }

    public void Display()
    {
        int index = 0;
        foreach (var chunk in SystemAPI.QueryChunks<Position, Vel>())
        {
            var positions = chunk.GetSpan0();
            int length = chunk.Length;
            for (int i = 0; i < length && index < 30; i++)
            {
                index++;
                GD.Print(index, " ", positions[i].pos);
            }
        }
    }

    public void Report()
    {
        //myWorld.ReportAllArchetypes();
    }

    public void Pause()
    {
        isPaused = !isPaused;
        GD.Print($"暂停状态: {isPaused}");
    }
}

/// <summary>
/// IJobChunk 实体位移 Job
/// </summary>
public unsafe struct MoveSystemJob : IJobChunk
{
    public float dt;

    public void Execute(ArchetypeChunk chunk, in ChunkEnabledMask enabledMask)
    {
        var positions = chunk.GetComponentDataSpan<Position>();
        var velocities = chunk.GetComponentDataSpan<Vel>();
        int count = chunk.Count;

        for (int i = 0; i < count; i++)
        {
            positions[i].pos.X += velocities[i].vel.X * dt;
            positions[i].pos.Y += velocities[i].vel.Y * dt;
        }
    }
}
