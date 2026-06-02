using EntJoy;
using EntJoy.Collections;
using EntJoy.Mathematics;
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

    // 模式切换
    private bool _useECS = true; // true=ECS模式, false=NativeArray模式
    private Label _modeLabel;

    // ECS 模式缓存
    private QueryBuilder _moveQuery = new QueryBuilder().WithAll<Position, Vel>();
    private MoveSystemJob _moveJob;

    // NativeArray 模式（百万级）
    private NativeArray<float2> _naPositions;
    private NativeArray<float2> _naVelocities;
    private bool _naInitialized = false;

    // 三个 Job 实例分别对应三种实现
    private NativeMoveJob _naMoveJob;
    private NativeMoveJob_NativeCpp _naMoveJobCpp;
    private NativeMoveJob_NativeIspc _naMoveJobIspc;

    // 当前 NativeArray 使用的 Job 类型: 0=JobSystem(C#), 1=Native C++, 2=Native ISPC
    private int _naJobType = 0;
    private string[] _naJobTypeNames = { "JobSystem(C#)", "Native C++", "Native ISPC" };

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

        // 添加模式切换按钮
        var toggleBtn = GetNode<Button>("CanvasLayer/HBoxContainer/ToggleMode");
        if (toggleBtn == null)
        {
            toggleBtn = new Button { Text = "切换模式" };
            var hbox = GetNode("CanvasLayer/HBoxContainer");
            hbox.AddChild(toggleBtn);
        }
        toggleBtn.Pressed += ToggleMode;

        // 添加 NativeArray Job 类型切换按钮
        var toggleJobBtn = GetNode<Button>("CanvasLayer/HBoxContainer/ToggleJobType");
        if (toggleJobBtn == null)
        {
            toggleJobBtn = new Button { Text = "切换Job类型" };
            var hbox = GetNode("CanvasLayer/HBoxContainer");
            hbox.AddChild(toggleJobBtn);
        }
        toggleJobBtn.Pressed += ToggleNaJobType;

        // 添加模式标签
        _modeLabel = GetNode<Label>("CanvasLayer/HBoxContainer/ModeLabel");
        if (_modeLabel == null)
        {
            _modeLabel = new Label { Text = "[ECS]" };
            var hbox = GetNode("CanvasLayer/HBoxContainer");
            hbox.AddChild(_modeLabel);
        }

        viewportRect = GetViewportRect();
        UpdateModeLabel();
    }

    public override void _PhysicsProcess(double delta)
    {
        if (isPaused)
            return;

        if (_useECS)
        {
            // ======= ECS 模式 =======
            if (myWorld == null || myWorld.EntityManager.EntityCount == 0)
                return;

            _moveJob.dt = (float)delta;

            _sw.Restart();
            _moveJob.Schedule(_moveQuery).Complete();
            _sw.Stop();
        }
        else
        {
            // ======= NativeArray 模式 =======
            if (!_naInitialized || _naPositions.Length == 0)
                return;

            _sw.Restart();
            switch (_naJobType)
            {
                case 0: // JobSystem (C#)
                    _naMoveJob.Dt = (float)delta;
                    _naMoveJob.ViewportWidth = (float)viewportRect.Size.X;
                    _naMoveJob.ViewportHeight = (float)viewportRect.Size.Y;
                    _naMoveJob.Schedule(EntityCount, 0).Complete();
                    break;
                case 1: // Native C++
                    _naMoveJobCpp.Dt = (float)delta;
                    _naMoveJobCpp.ViewportWidth = (float)viewportRect.Size.X;
                    _naMoveJobCpp.ViewportHeight = (float)viewportRect.Size.Y;
                    _naMoveJobCpp.Schedule(EntityCount, 0).Complete();
                    break;
                case 2: // Native ISPC
                    _naMoveJobIspc.Dt = (float)delta;
                    _naMoveJobIspc.ViewportWidth = (float)viewportRect.Size.X;
                    _naMoveJobIspc.ViewportHeight = (float)viewportRect.Size.Y;
                    _naMoveJobIspc.Schedule(EntityCount, 0).Complete();
                    break;
            }
            _sw.Stop();
        }

        _totalMs += _sw.Elapsed.TotalMilliseconds;
        _frameCount++;

        if (_frameCount >= 60)
        {
            double avg = _totalMs / _frameCount;
            string modeStr = _useECS ? "ECS" : _naJobTypeNames[_naJobType];
            GD.Print($"[{modeStr}] 每帧平均耗时(60帧): {avg:F4} ms");
            _totalMs = 0;
            _frameCount = 0;
        }
    }

    public void ToggleMode()
    {
        _useECS = !_useECS;
        UpdateModeLabel();
        GD.Print($"切换至 {(_useECS ? "ECS" : _naJobTypeNames[_naJobType])} 模式");

        // 切换到 NativeArray 时如果未初始化则从 ECS 复制
        if (!_useECS && !_naInitialized && myWorld != null)
        {
            InitNativeArraysFromECS();
        }
    }

    public void ToggleNaJobType()
    {
        if (_useECS) return; // ECS 模式下不切换
        _naJobType = (_naJobType + 1) % 3;
        UpdateModeLabel();
        GD.Print($"NativeArray Job 类型切换至: {_naJobTypeNames[_naJobType]}");

        // 切换到 Native C++ 或 ISPC 时输出调试路径信息
        if (_naJobType == 1 || _naJobType == 2)
        {
            string cwd = System.Environment.CurrentDirectory;
            string asmLocation = System.Reflection.Assembly.GetExecutingAssembly().Location;
            string entryLocation = System.Reflection.Assembly.GetEntryAssembly()?.Location ?? "null";

            GD.Print("");
            GD.Print("========== Native DLL 调试信息 ==========");
            GD.Print($"CurrentDirectory: {cwd}");
            GD.Print($"Assembly.Location: {asmLocation}");
            GD.Print($"EntryAssembly.Location: {entryLocation}");

            // 检查几个关键的 DLL 路径
            string[] pathsToCheck = new string[]
            {
                System.IO.Path.Combine(cwd, ".godot", "mono", "temp", "bin", "Debug", "NativeDll.dll"),
                System.IO.Path.Combine(cwd, "..", "..", "bin", "NativeDll.dll"),
                @"D:\Godot\Project\EntJoy\bin\NativeDll.dll",
                System.IO.Path.Combine(System.IO.Path.GetDirectoryName(asmLocation) ?? "", "NativeDll.dll"),
            };

            foreach (var p in pathsToCheck)
            {
                string full = System.IO.Path.GetFullPath(p);
                GD.Print($"  {(System.IO.File.Exists(full) ? "[OK] " : "[MISS]")} {full}");
            }
            GD.Print("=========================================");
            GD.Print("");
        }
    }

    private void UpdateModeLabel()
    {
        if (_modeLabel != null)
        {
            _modeLabel.Text = _useECS ? "[ECS]" : $"[{_naJobTypeNames[_naJobType]}]";
        }
    }

    private void InitNativeArraysFromECS()
    {
        if (myWorld == null || myWorld.EntityManager.EntityCount == 0)
        {
            GD.Print("NativeArray: 无实体数据可用");
            return;
        }

        GD.Print("正在从 ECS 复制数据到 NativeArray...");
        var sw = Stopwatch.StartNew();

        // 分配 NativeArray
        if (_naPositions.IsCreated) _naPositions.Dispose();
        if (_naVelocities.IsCreated) _naVelocities.Dispose();

        _naPositions = new NativeArray<float2>(EntityCount, Allocator.Persistent);
        _naVelocities = new NativeArray<float2>(EntityCount, Allocator.Persistent);

        int idx = 0;
        foreach (var chunk in SystemAPI.QueryChunks<Position, Vel>())
        {
            var positions = chunk.GetSpan0();
            var velocities = chunk.GetSpan1();
            int len = chunk.Length;

            for (int i = 0; i < len; i++)
            {
                Vector2 p = positions[i].pos;
                Vector2 v = velocities[i].vel;
                _naPositions[idx] = new float2(p.X, p.Y);
                _naVelocities[idx] = new float2(v.X, v.Y);
                idx++;
            }
        }

        float vw = (float)viewportRect.Size.X;
        float vh = (float)viewportRect.Size.Y;

        // 初始化三个 Job
        _naMoveJob = new NativeMoveJob
        {
            Positions = _naPositions,
            Velocities = _naVelocities,
            Dt = 0.016f,
            ViewportWidth = vw,
            ViewportHeight = vh
        };

        _naMoveJobCpp = new NativeMoveJob_NativeCpp
        {
            Positions = _naPositions,
            Velocities = _naVelocities,
            Dt = 0.016f,
            ViewportWidth = vw,
            ViewportHeight = vh
        };

        _naMoveJobIspc = new NativeMoveJob_NativeIspc
        {
            Positions = _naPositions,
            Velocities = _naVelocities,
            Dt = 0.016f,
            ViewportWidth = vw,
            ViewportHeight = vh
        };

        _naInitialized = true;
        sw.Stop();
        GD.Print($"NativeArray 初始化完成: {EntityCount:N0} 个实体, 耗时 {sw.Elapsed.TotalMilliseconds:F1} ms");
    }

    public void RunBenchmark()
    {
        const int WARMUP = 3;
        const int ITERATIONS = 1000;
        var sw = new Stopwatch();

        if (_useECS)
        {
            if (myWorld == null || myWorld.EntityManager.EntityCount == 0)
            {
                GD.Print("请先创建世界和实体");
                return;
            }

            var query = new QueryBuilder().WithAll<Position, Vel>();

            GD.Print($"[ECS] 预热 {WARMUP} 次...");
            for (int i = 0; i < WARMUP; i++)
            {
                var job = new MoveSystemJob { dt = 0.016f };
                job.Schedule(query).Complete();
            }

            GD.Print($"[ECS] 开始基准测试 {ITERATIONS} 次...");
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
            GD.Print($"[ECS] Benchmark 完成: 平均 {avg:F3} ms (共 {ITERATIONS} 次)");
        }
        else
        {
            if (!_naInitialized || _naPositions.Length == 0)
            {
                GD.Print("请先创建实体");
                return;
            }

            float vw = (float)viewportRect.Size.X;
            float vh = (float)viewportRect.Size.Y;

            // 对三种 NativeArray Job 类型分别做基准测试
            GD.Print("\n=== NativeArray Benchmark ===");

            // 1. JobSystem (C#)
            GD.Print($"\n[JobSystem(C#)] 预热 {WARMUP} 次...");
            for (int i = 0; i < WARMUP; i++)
            {
                var job = new NativeMoveJob
                {
                    Positions = _naPositions,
                    Velocities = _naVelocities,
                    Dt = 0.016f,
                    ViewportWidth = vw,
                    ViewportHeight = vh
                };
                job.Schedule(EntityCount, 65536).Complete();
            }

            GD.Print($"[JobSystem(C#)] 开始基准测试 {ITERATIONS} 次...");
            double total1 = 0;
            for (int i = 0; i < ITERATIONS; i++)
            {
                var job = new NativeMoveJob
                {
                    Positions = _naPositions,
                    Velocities = _naVelocities,
                    Dt = 0.016f,
                    ViewportWidth = vw,
                    ViewportHeight = vh
                };
                sw.Restart();
                job.Schedule(EntityCount, 65536).Complete();
                sw.Stop();
                total1 += sw.Elapsed.TotalMilliseconds;
            }
            double avg1 = total1 / ITERATIONS;

            // 2. Native C++
            GD.Print($"\n[Native C++] 预热 {WARMUP} 次...");
            for (int i = 0; i < WARMUP; i++)
            {
                var job = new NativeMoveJob_NativeCpp
                {
                    Positions = _naPositions,
                    Velocities = _naVelocities,
                    Dt = 0.016f,
                    ViewportWidth = vw,
                    ViewportHeight = vh
                };
                job.Schedule(EntityCount, 65536).Complete();
            }

            GD.Print($"[Native C++] 开始基准测试 {ITERATIONS} 次...");
            double total2 = 0;
            for (int i = 0; i < ITERATIONS; i++)
            {
                var job = new NativeMoveJob_NativeCpp
                {
                    Positions = _naPositions,
                    Velocities = _naVelocities,
                    Dt = 0.016f,
                    ViewportWidth = vw,
                    ViewportHeight = vh
                };
                sw.Restart();
                job.Schedule(EntityCount, 65536).Complete();
                sw.Stop();
                total2 += sw.Elapsed.TotalMilliseconds;
            }
            double avg2 = total2 / ITERATIONS;

            // 3. Native ISPC
            GD.Print($"\n[Native ISPC] 预热 {WARMUP} 次...");
            for (int i = 0; i < WARMUP; i++)
            {
                var job = new NativeMoveJob_NativeIspc
                {
                    Positions = _naPositions,
                    Velocities = _naVelocities,
                    Dt = 0.016f,
                    ViewportWidth = vw,
                    ViewportHeight = vh
                };
                job.Schedule(EntityCount, 65536).Complete();
            }

            GD.Print($"[Native ISPC] 开始基准测试 {ITERATIONS} 次...");
            double total3 = 0;
            for (int i = 0; i < ITERATIONS; i++)
            {
                var job = new NativeMoveJob_NativeIspc
                {
                    Positions = _naPositions,
                    Velocities = _naVelocities,
                    Dt = 0.016f,
                    ViewportWidth = vw,
                    ViewportHeight = vh
                };
                sw.Restart();
                job.Schedule(EntityCount, 65536).Complete();
                sw.Stop();
                total3 += sw.Elapsed.TotalMilliseconds;
            }
            double avg3 = total3 / ITERATIONS;

            GD.Print($"\n=== NativeArray Benchmark 结果 ===");
            GD.Print($"JobSystem(C#):     {avg1,8:F3} ms");
            GD.Print($"Native C++:        {avg2,8:F3} ms (加速比 {avg1 / avg2:F2}x)");
            GD.Print($"Native ISPC:       {avg3,8:F3} ms (加速比 {avg1 / avg3:F2}x)");
        }
    }

    public void CreateWorld()
    {
        myWorld = new World();
        GD.Print($"创建世界成功;");
    }

    public void NewEntity()
    {
        if (_useECS)
        {
            // ECS 模式：创建实体
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
        else
        {
            // NativeArray 模式：使用 float2 直接创建数组
            if (_naPositions.IsCreated) _naPositions.Dispose();
            if (_naVelocities.IsCreated) _naVelocities.Dispose();

            _naPositions = new NativeArray<float2>(EntityCount, Allocator.Persistent);
            _naVelocities = new NativeArray<float2>(EntityCount, Allocator.Persistent);

            for (int i = 0; i < EntityCount; i++)
            {
                _naPositions[i] = new float2(100f, 100f);
                _naVelocities[i] = new float2
                (
                    (float)GD.RandRange(100.0, 200.0),
                    (float)GD.RandRange(-200.0, 200.0)
                );
            }

            float vw = (float)viewportRect.Size.X;
            float vh = (float)viewportRect.Size.Y;

            _naMoveJob = new NativeMoveJob
            {
                Positions = _naPositions,
                Velocities = _naVelocities,
                Dt = 0.016f,
                ViewportWidth = vw,
                ViewportHeight = vh
            };

            _naMoveJobCpp = new NativeMoveJob_NativeCpp
            {
                Positions = _naPositions,
                Velocities = _naVelocities,
                Dt = 0.016f,
                ViewportWidth = vw,
                ViewportHeight = vh
            };

            _naMoveJobIspc = new NativeMoveJob_NativeIspc
            {
                Positions = _naPositions,
                Velocities = _naVelocities,
                Dt = 0.016f,
                ViewportWidth = vw,
                ViewportHeight = vh
            };

            _naInitialized = true;
            GD.Print($"NativeArray NewEntity Success 当前实体数 {EntityCount}");
        }
    }

    public void Display()
    {
        if (_useECS)
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
        else
        {
            int count = Mathf.Min(30, (int)_naPositions.Length);
            for (int i = 0; i < count; i++)
            {
                float2 p = _naPositions[i];
                GD.Print(i + 1, " (", p.x, ", ", p.y, ")");
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

    public override void _ExitTree()
    {
        base._ExitTree();
        if (_naPositions.IsCreated) _naPositions.Dispose();
        if (_naVelocities.IsCreated) _naVelocities.Dispose();
    }
}

/// <summary>
/// IJobChunk 实体位移 Job (ECS 模式)
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

// ==================== NativeArray Job 三种实现 ====================

/// <summary>
/// 纯 C# IJobParallelFor — 对应 MoveEntitiesTest 的 MoveEntitiesJob
/// 使用 float2 (EntJoy.Mathematics) 兼容 NativeTranspile
/// 包含边界反弹
/// </summary>
public struct NativeMoveJob : IJobParallelFor
{
    public NativeArray<float2> Positions;
    public NativeArray<float2> Velocities;
    public float Dt;
    public float ViewportWidth;
    public float ViewportHeight;

    public void Execute(int index)
    {
        float2 pos = Positions[index];
        float2 vel = Velocities[index];

        pos.x += vel.x * Dt;
        pos.y += vel.y * Dt;

        // 边界反弹
        if (pos.x < 0f || pos.x > ViewportWidth) vel.x = -vel.x;
        if (pos.y < 0f || pos.y > ViewportHeight) vel.y = -vel.y;

        Positions[index] = pos;
        Velocities[index] = vel;
    }
}

/// <summary>
/// Native C++ 编译版本 — 对应 MoveEntitiesTest 的 MoveEntitiesJob_NativeCpp
/// </summary>
[NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Cpp)]
public struct NativeMoveJob_NativeCpp : IJobParallelFor
{
    public NativeArray<float2> Positions;
    public NativeArray<float2> Velocities;
    public float Dt;
    public float ViewportWidth;
    public float ViewportHeight;

    public void Execute(int index)
    {
        float2 pos = Positions[index];
        float2 vel = Velocities[index];

        pos.x += vel.x * Dt;
        pos.y += vel.y * Dt;

        // 边界反弹
        if (pos.x < 0f || pos.x > ViewportWidth) vel.x = -vel.x;
        if (pos.y < 0f || pos.y > ViewportHeight) vel.y = -vel.y;

        Positions[index] = pos;
        Velocities[index] = vel;
    }
}

/// <summary>
/// Native ISPC SIMD 编译版本 — 对应 MoveEntitiesTest 的 MoveEntitiesJob_NativeIspc
/// </summary>
[NativeTranspiler.NativeTranspile(Target = NativeTranspiler.BackendTarget.Ispc, MathLib = NativeTranspiler.IspcMathLib.fast)]
public struct NativeMoveJob_NativeIspc : IJobParallelFor
{
    public NativeArray<float2> Positions;
    public NativeArray<float2> Velocities;
    public float Dt;
    public float ViewportWidth;
    public float ViewportHeight;

    public void Execute(int index)
    {
        float2 pos = Positions[index];
        float2 vel = Velocities[index];

        pos.x += vel.x * Dt;
        pos.y += vel.y * Dt;

        // 边界反弹
        if (pos.x < 0f || pos.x > ViewportWidth) vel.x = -vel.x;
        if (pos.y < 0f || pos.y > ViewportHeight) vel.y = -vel.y;

        Positions[index] = pos;
        Velocities[index] = vel;
    }
}
